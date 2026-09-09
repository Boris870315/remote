#include "remote_freerdp_bridge.h"

#include <freerdp/freerdp.h>
#include <freerdp/codec/color.h>
#include <freerdp/display.h>
#include <freerdp/channels/disp.h>
#include <freerdp/client/cmdline.h>
#include <freerdp/addin.h>
#include <freerdp/client.h>
#include <freerdp/client/channels.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/input.h>
#include <freerdp/settings.h>
#include <freerdp/update.h>
#include <winpr/synch.h>
#include <winpr/input.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <pthread.h>

struct remote_rdp_session {
    freerdp* instance;
    char last_error[512];
    remote_rdp_config config;
    volatile int stopping;
    uint8_t mouse_buttons;
    pthread_mutex_t state_mutex;
    uint32_t pending_width;
    uint32_t pending_height;
};

typedef struct remote_context {
    rdpContext base;
    remote_rdp_session* owner;
} remote_context;

static pthread_once_t addin_provider_once = PTHREAD_ONCE_INIT;

static void register_addin_provider(void) {
    (void)freerdp_register_addin_provider(freerdp_channels_load_static_addin_entry, 0);
}

static remote_rdp_session* owner_from_context(rdpContext* context) {
    return context ? ((remote_context*)context)->owner : NULL;
}

static BOOL remote_begin_paint(rdpContext* context) {
    if (context && context->gdi && context->gdi->primary && context->gdi->primary->hdc &&
        context->gdi->primary->hdc->hwnd && context->gdi->primary->hdc->hwnd->invalid)
        context->gdi->primary->hdc->hwnd->invalid->null = TRUE;
    return TRUE;
}

static BOOL remote_end_paint(rdpContext* context) {
    remote_rdp_session* session = owner_from_context(context);
    if (!session || !context->gdi || !session->config.frame_callback) return TRUE;
    session->config.frame_callback(session->config.callback_state, context->gdi->primary_buffer,
                                   (uint32_t)context->gdi->width, (uint32_t)context->gdi->height,
                                   context->gdi->stride);
    return TRUE;
}

static BOOL remote_desktop_resize(rdpContext* context) {
    return context && context->gdi && gdi_resize(context->gdi,
        freerdp_settings_get_uint32(context->settings, FreeRDP_DesktopWidth),
        freerdp_settings_get_uint32(context->settings, FreeRDP_DesktopHeight));
}

static BOOL remote_pre_connect(freerdp* instance) {
    if (!instance || !instance->context || !instance->context->pubSub) return FALSE;
    if (PubSub_SubscribeChannelConnected(instance->context->pubSub,
                                         freerdp_client_OnChannelConnectedEventHandler) < 0)
        return FALSE;
    if (PubSub_SubscribeChannelDisconnected(instance->context->pubSub,
                                            freerdp_client_OnChannelDisconnectedEventHandler) < 0) {
        PubSub_UnsubscribeChannelConnected(instance->context->pubSub,
                                           freerdp_client_OnChannelConnectedEventHandler);
        return FALSE;
    }
    return TRUE;
}

static BOOL remote_post_connect(freerdp* instance) {
    if (!gdi_init(instance, PIXEL_FORMAT_BGRA32)) return FALSE;
    instance->context->update->BeginPaint = remote_begin_paint;
    instance->context->update->EndPaint = remote_end_paint;
    instance->context->update->DesktopResize = remote_desktop_resize;
    return TRUE;
}

static void remote_post_disconnect(freerdp* instance) {
    if (!instance || !instance->context) return;
    PubSub_UnsubscribeChannelConnected(instance->context->pubSub,
                                       freerdp_client_OnChannelConnectedEventHandler);
    PubSub_UnsubscribeChannelDisconnected(instance->context->pubSub,
                                          freerdp_client_OnChannelDisconnectedEventHandler);
    if (instance->context->gdi) gdi_free(instance);
}

static int remote_verify_certificate(freerdp* instance, const BYTE* data, size_t length,
                                     const char* hostname, UINT16 port, DWORD flags) {
    (void)data; (void)length; (void)hostname; (void)port; (void)flags;
    remote_rdp_session* session = instance ? owner_from_context(instance->context) : NULL;
    return session && session->config.allow_untrusted_certificate ? 2 : 0;
}

uint32_t remote_rdp_get_capabilities(remote_rdp_capabilities* capabilities) {
    if (!capabilities) return 1u;
    memset(capabilities, 0, sizeof(*capabilities));
    capabilities->abi_version = REMOTE_FREERDP_ABI_VERSION;
    freerdp_get_version((int*)&capabilities->freerdp_major,
                        (int*)&capabilities->freerdp_minor,
                        (int*)&capabilities->freerdp_revision);
    capabilities->supports_framebuffer = 1u;
    capabilities->supports_dynamic_resolution = 1u;
    return 0u;
}

remote_rdp_session* remote_rdp_session_new(void) {
    (void)pthread_once(&addin_provider_once, register_addin_provider);
    remote_rdp_session* session = calloc(1, sizeof(*session));
    if (!session) return NULL;
    if (pthread_mutex_init(&session->state_mutex, NULL) != 0) {
        free(session);
        return NULL;
    }
    session->instance = freerdp_new();
    if (!session->instance) {
        strncpy(session->last_error, "freerdp_new failed", sizeof(session->last_error) - 1);
    } else {
        session->instance->ContextSize = sizeof(remote_context);
        session->instance->LoadChannels = freerdp_client_load_channels;
        session->instance->PreConnect = remote_pre_connect;
        session->instance->PostConnect = remote_post_connect;
        session->instance->PostDisconnect = remote_post_disconnect;
        session->instance->VerifyX509Certificate = remote_verify_certificate;
        if (!freerdp_context_new(session->instance)) {
            freerdp_free(session->instance);
            session->instance = NULL;
            strncpy(session->last_error, "freerdp_context_new failed", sizeof(session->last_error) - 1);
        } else {
            ((remote_context*)session->instance->context)->owner = session;
        }
    }
    return session;
}

void remote_rdp_session_free(remote_rdp_session* session) {
    if (!session) return;
    if (session->instance) freerdp_free(session->instance);
    pthread_mutex_destroy(&session->state_mutex);
    memset(session, 0, sizeof(*session));
    free(session);
}

static void apply_pending_resize(remote_rdp_session* session) {
    uint32_t width = 0;
    uint32_t height = 0;
    pthread_mutex_lock(&session->state_mutex);
    width = session->pending_width;
    height = session->pending_height;
    pthread_mutex_unlock(&session->state_mutex);
    if (!width || !height) return;

    MONITOR_DEF monitor = { 0 };
    monitor.right = (INT32)width - 1;
    monitor.bottom = (INT32)height - 1;
    monitor.flags = DISPLAY_CONTROL_MONITOR_PRIMARY;
    if (!freerdp_display_send_monitor_layout(session->instance->context, 1u, &monitor)) return;

    pthread_mutex_lock(&session->state_mutex);
    if (session->pending_width == width && session->pending_height == height) {
        session->pending_width = 0;
        session->pending_height = 0;
    }
    pthread_mutex_unlock(&session->state_mutex);
}

const char* remote_rdp_session_last_error(const remote_rdp_session* session) {
    return session ? session->last_error : "invalid session";
}

static BOOL configure(remote_rdp_session* session, const remote_rdp_config* config) {
    rdpSettings* settings = session->instance->context->settings;
    return freerdp_settings_set_string(settings, FreeRDP_ServerHostname, config->hostname) &&
           freerdp_settings_set_uint32(settings, FreeRDP_ServerPort, config->port ? config->port : 3389) &&
           freerdp_settings_set_string(settings, FreeRDP_Username, config->username ? config->username : "") &&
           freerdp_settings_set_string(settings, FreeRDP_Password, config->password ? config->password : "") &&
           freerdp_settings_set_string(settings, FreeRDP_Domain, config->domain ? config->domain : "") &&
           freerdp_settings_set_uint32(settings, FreeRDP_DesktopWidth, config->width) &&
           freerdp_settings_set_uint32(settings, FreeRDP_DesktopHeight, config->height) &&
           freerdp_settings_set_uint32(settings, FreeRDP_ColorDepth, 32) &&
           freerdp_settings_set_bool(settings, FreeRDP_SupportDisplayControl, TRUE) &&
           freerdp_settings_set_bool(settings, FreeRDP_DynamicResolutionUpdate, TRUE) &&
           freerdp_settings_set_bool(settings, FreeRDP_UseMultimon,
                                     config->use_all_monitors != 0) &&
           freerdp_settings_set_bool(settings, FreeRDP_SpanMonitors,
                                     config->use_all_monitors != 0) &&
           freerdp_settings_set_bool(settings, FreeRDP_RedirectClipboard,
                                     !config->view_only && config->redirect_clipboard) &&
           freerdp_settings_set_bool(settings, FreeRDP_RedirectPrinters,
                                     !config->view_only && config->redirect_printers) &&
           freerdp_settings_set_bool(settings, FreeRDP_RedirectDrives,
                                     !config->view_only && config->redirect_drives) &&
           freerdp_settings_set_bool(settings, FreeRDP_DeviceRedirection,
                                     !config->view_only &&
                                     (config->redirect_printers || config->redirect_drives)) &&
           freerdp_settings_set_bool(settings, FreeRDP_IgnoreCertificate,
                                     config->allow_untrusted_certificate != 0);
}

uint32_t remote_rdp_session_connect(remote_rdp_session* session, const remote_rdp_config* config) {
    if (!session || !session->instance || !config || !config->hostname) return 1u;
    session->config = *config;
    session->stopping = 0;
    if (!configure(session, config)) {
        snprintf(session->last_error, sizeof(session->last_error),
                 "FreeRDP settings configuration failed");
        return 2u;
    }
    if (!freerdp_connect(session->instance)) {
        uint32_t error = freerdp_get_last_error(session->instance->context);
        const char* message = error ? freerdp_get_last_error_string(error) : "FreeRDP connection failed";
        snprintf(session->last_error, sizeof(session->last_error), "%s", message);
        return error ? error : 2u;
    }
    if (config->password) {
        const size_t password_length = strlen(config->password);
        memset((void*)config->password, 0, password_length);
    }
    if (config->state_callback) config->state_callback(config->callback_state, 1u, 0u, "connected");
    uint32_t disconnect_error = 0u;
    session->last_error[0] = '\0';
    while (!session->stopping && !freerdp_shall_disconnect_context(session->instance->context)) {
        HANDLE handles[64] = { 0 };
        DWORD count = freerdp_get_event_handles(session->instance->context, handles, 64);
        if (!count) {
            disconnect_error = freerdp_get_last_error(session->instance->context);
            if (!disconnect_error) disconnect_error = 3u;
            snprintf(session->last_error, sizeof(session->last_error),
                     "RDP event loop has no event handles; HRESULT=0x%08x", disconnect_error);
            break;
        }
        DWORD wait_result = WaitForMultipleObjects(count, handles, FALSE, 100);
        if (wait_result == WAIT_FAILED) {
            disconnect_error = freerdp_get_last_error(session->instance->context);
            if (!disconnect_error) disconnect_error = 3u;
            snprintf(session->last_error, sizeof(session->last_error),
                     "RDP event wait failed; HRESULT=0x%08x", disconnect_error);
            break;
        }
        if (!freerdp_check_event_handles(session->instance->context)) {
            disconnect_error = freerdp_get_last_error(session->instance->context);
            if (!disconnect_error) disconnect_error = 3u;
            snprintf(session->last_error, sizeof(session->last_error),
                     "RDP event processing failed; HRESULT=0x%08x: %s", disconnect_error,
                     freerdp_get_last_error_string(disconnect_error));
            break;
        }
        apply_pending_resize(session);
    }
    if (!session->stopping && !disconnect_error) {
        disconnect_error = freerdp_get_last_error(session->instance->context);
        snprintf(session->last_error, sizeof(session->last_error),
                 "RDP server requested disconnect; HRESULT=0x%08x: %s", disconnect_error,
                 disconnect_error ? freerdp_get_last_error_string(disconnect_error) : "no error supplied");
    }
    freerdp_disconnect(session->instance);
    (void)freerdp_settings_set_string(session->instance->context->settings, FreeRDP_Password, "");
    const char* disconnect_message = session->last_error[0] ? session->last_error : "disconnected";
    if (config->state_callback)
        config->state_callback(config->callback_state, 2u, disconnect_error, disconnect_message);
    return disconnect_error;
}

void remote_rdp_session_disconnect(remote_rdp_session* session) {
    if (!session || !session->instance) return;
    session->stopping = 1;
    freerdp_abort_connect_context(session->instance->context);
}

uint32_t remote_rdp_session_send_mouse(remote_rdp_session* session, uint16_t x, uint16_t y,
                                       uint8_t button_mask) {
    if (!session || !session->instance || !session->instance->context ||
        !session->instance->context->input) return 1u;
    if (session->config.view_only) return 2u;
    static const uint16_t flags[3] = { PTR_FLAGS_BUTTON1, PTR_FLAGS_BUTTON3, PTR_FLAGS_BUTTON2 };
    for (uint8_t index = 0; index < 3; index++) {
        const uint8_t bit = (uint8_t)(1u << index);
        if ((session->mouse_buttons & bit) == (button_mask & bit)) continue;
        uint16_t event_flags = flags[index];
        if (button_mask & bit) event_flags |= PTR_FLAGS_DOWN;
        if (!freerdp_input_send_mouse_event(session->instance->context->input, event_flags, x, y)) return 3u;
    }
    session->mouse_buttons = button_mask;
    return freerdp_input_send_mouse_event(session->instance->context->input, PTR_FLAGS_MOVE, x, y) ? 0u : 3u;
}

uint32_t remote_rdp_session_send_wheel(remote_rdp_session* session, uint16_t x, uint16_t y,
                                       int16_t delta) {
    if (!session || !session->instance || !session->instance->context ||
        !session->instance->context->input) return 1u;
    if (session->config.view_only) return 2u;
    uint16_t magnitude = (uint16_t)(delta < 0 ? -delta : delta);
    if (magnitude > 0x00ffu) magnitude = 0x00ffu;
    uint16_t flags = (uint16_t)(PTR_FLAGS_WHEEL | magnitude);
    if (delta < 0) flags |= PTR_FLAGS_WHEEL_NEGATIVE;
    return freerdp_input_send_mouse_event(session->instance->context->input, flags, x, y) ? 0u : 3u;
}

uint32_t remote_rdp_session_send_key(remote_rdp_session* session, uint32_t virtual_key,
                                     uint8_t down) {
    if (!session || !session->instance || !session->instance->context ||
        !session->instance->context->input) return 1u;
    if (session->config.view_only) return 2u;
    DWORD scan_code = GetVirtualScanCodeFromVirtualKeyCode(virtual_key, WINPR_KBD_TYPE_IBM_ENHANCED);
    if (!scan_code) return 4u;
    return freerdp_input_send_keyboard_event_ex(session->instance->context->input, down != 0, FALSE,
                                                 scan_code) ? 0u : 3u;
}

uint32_t remote_rdp_session_resize(remote_rdp_session* session, uint32_t width, uint32_t height) {
    if (!session || !session->instance || !session->instance->context) return 1u;
    if (width < 200u || height < 200u || width > 8192u || height > 8192u) return 2u;
    if (session->config.use_all_monitors) return 2u;
    pthread_mutex_lock(&session->state_mutex);
    session->pending_width = width;
    session->pending_height = height;
    pthread_mutex_unlock(&session->state_mutex);
    return 0u;
}
