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
#include <freerdp/scancode.h>
#include <freerdp/settings.h>
#include <freerdp/update.h>
#include <winpr/synch.h>
#include <winpr/input.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <pthread.h>
#include <time.h>

struct remote_rdp_session {
    freerdp* instance;
    rdpContext* client_context;
    char last_error[512];
    remote_rdp_config config;
    volatile int stopping;
    uint8_t mouse_buttons;
    pthread_mutex_t state_mutex;
    uint32_t pending_width;
    uint32_t pending_height;
    HANDLE input_event;
    struct remote_input_event {
        uint8_t type;
        uint8_t button_mask;
        uint8_t down;
        int16_t wheel_delta;
        uint16_t x;
        uint16_t y;
        uint32_t virtual_key;
    } input_queue[512];
    size_t input_head;
    size_t input_count;
    uint8_t input_focused;
    uint64_t last_frame_time_ms;
    uint8_t has_frame;
    int32_t dirty_left;
    int32_t dirty_top;
    int32_t dirty_right;
    int32_t dirty_bottom;
};

enum {
    REMOTE_INPUT_MOUSE = 1,
    REMOTE_INPUT_WHEEL = 2,
    REMOTE_INPUT_KEY = 3
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
    HGDI_RGN invalid = context->gdi->primary->hdc->hwnd->invalid;
    const int32_t desktop_width = context->gdi->width;
    const int32_t desktop_height = context->gdi->height;
    int32_t left = 0;
    int32_t top = 0;
    int32_t right = desktop_width;
    int32_t bottom = desktop_height;
    if (session->has_frame && invalid && !invalid->null) {
        left = invalid->x;
        top = invalid->y;
        right = invalid->x + invalid->w;
        bottom = invalid->y + invalid->h;
    }
    left = left < 0 ? 0 : left;
    top = top < 0 ? 0 : top;
    right = right > desktop_width ? desktop_width : right;
    bottom = bottom > desktop_height ? desktop_height : bottom;
    if (right <= left || bottom <= top) return TRUE;

    if (session->dirty_right <= session->dirty_left) {
        session->dirty_left = left;
        session->dirty_top = top;
        session->dirty_right = right;
        session->dirty_bottom = bottom;
    } else {
        if (left < session->dirty_left) session->dirty_left = left;
        if (top < session->dirty_top) session->dirty_top = top;
        if (right > session->dirty_right) session->dirty_right = right;
        if (bottom > session->dirty_bottom) session->dirty_bottom = bottom;
    }
    struct timespec now = { 0 };
    if (clock_gettime(CLOCK_MONOTONIC, &now) == 0) {
        const uint64_t now_ms = ((uint64_t)now.tv_sec * 1000u) +
                                ((uint64_t)now.tv_nsec / 1000000u);
        if (session->last_frame_time_ms && now_ms - session->last_frame_time_ms < 33u)
            return TRUE;
        session->last_frame_time_ms = now_ms;
    }
    session->config.frame_callback(session->config.callback_state, context->gdi->primary_buffer,
                                   (uint32_t)context->gdi->width, (uint32_t)context->gdi->height,
                                   context->gdi->stride,
                                   (uint32_t)session->dirty_left, (uint32_t)session->dirty_top,
                                   (uint32_t)(session->dirty_right - session->dirty_left),
                                   (uint32_t)(session->dirty_bottom - session->dirty_top));
    session->has_frame = 1;
    session->dirty_left = session->dirty_top = 0;
    session->dirty_right = session->dirty_bottom = 0;
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

static BOOL remote_client_new(freerdp* instance, rdpContext* context) {
    if (!instance || !context) return FALSE;
    instance->ContextSize = sizeof(remote_context);
    instance->LoadChannels = freerdp_client_load_channels;
    instance->PreConnect = remote_pre_connect;
    instance->PostConnect = remote_post_connect;
    instance->PostDisconnect = remote_post_disconnect;
    instance->VerifyX509Certificate = remote_verify_certificate;
    return TRUE;
}

static void remote_client_free(freerdp* instance, rdpContext* context) {
    (void)instance;
    (void)context;
}

static int remote_client_start(rdpContext* context) {
    return context ? 0 : -1;
}

static int remote_client_stop(rdpContext* context) {
    return context ? 0 : -1;
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
    session->input_event = CreateEvent(NULL, TRUE, FALSE, NULL);
    if (!session->input_event) {
        pthread_mutex_destroy(&session->state_mutex);
        free(session);
        return NULL;
    }
    RDP_CLIENT_ENTRY_POINTS entry_points = { 0 };
    entry_points.Size = sizeof(RDP_CLIENT_ENTRY_POINTS);
    entry_points.Version = RDP_CLIENT_INTERFACE_VERSION;
    entry_points.ContextSize = sizeof(remote_context);
    entry_points.ClientNew = remote_client_new;
    entry_points.ClientFree = remote_client_free;
    entry_points.ClientStart = remote_client_start;
    entry_points.ClientStop = remote_client_stop;
    session->client_context = freerdp_client_context_new(&entry_points);
    if (!session->client_context) {
        strncpy(session->last_error, "freerdp_client_context_new failed",
                sizeof(session->last_error) - 1);
    } else {
        session->instance = freerdp_client_get_instance(session->client_context);
        ((remote_context*)session->client_context)->owner = session;
        if (!session->instance || freerdp_client_start(session->client_context) != 0) {
            freerdp_client_context_free(session->client_context);
            session->client_context = NULL;
            session->instance = NULL;
            strncpy(session->last_error, "freerdp_client_start failed",
                    sizeof(session->last_error) - 1);
        }
    }
    return session;
}

void remote_rdp_session_free(remote_rdp_session* session) {
    if (!session) return;
    if (session->client_context) {
        (void)freerdp_client_stop(session->client_context);
        freerdp_client_context_free(session->client_context);
    }
    CloseHandle(session->input_event);
    pthread_mutex_destroy(&session->state_mutex);
    memset(session, 0, sizeof(*session));
    free(session);
}

static void apply_pending_resize(remote_rdp_session* session) {
    if (!session->config.use_all_monitors) return;
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

static BOOL enqueue_input(remote_rdp_session* session, const struct remote_input_event* event) {
    BOOL accepted = FALSE;
    pthread_mutex_lock(&session->state_mutex);
    if (session->input_count < 512) {
        const size_t tail = (session->input_head + session->input_count) % 512;
        session->input_queue[tail] = *event;
        session->input_count++;
        accepted = TRUE;
    }
    pthread_mutex_unlock(&session->state_mutex);
    if (accepted) SetEvent(session->input_event);
    return accepted;
}

static BOOL process_pending_input(remote_rdp_session* session) {
    for (;;) {
        struct remote_input_event event = { 0 };
        pthread_mutex_lock(&session->state_mutex);
        if (!session->input_count) {
            ResetEvent(session->input_event);
            pthread_mutex_unlock(&session->state_mutex);
            return TRUE;
        }
        event = session->input_queue[session->input_head];
        session->input_head = (session->input_head + 1) % 512;
        session->input_count--;
        pthread_mutex_unlock(&session->state_mutex);

        rdpInput* input = session->instance->context->input;
        if (!session->input_focused && input->FocusInEvent) {
            session->input_focused = input->FocusInEvent(input, 0u) ? 1u : 0u;
        }
        if (event.type == REMOTE_INPUT_MOUSE) {
            static const uint16_t flags[3] = {
                PTR_FLAGS_BUTTON1, PTR_FLAGS_BUTTON3, PTR_FLAGS_BUTTON2
            };
            for (uint8_t index = 0; index < 3; index++) {
                const uint8_t bit = (uint8_t)(1u << index);
                if ((session->mouse_buttons & bit) == (event.button_mask & bit)) continue;
                uint16_t event_flags = flags[index];
                if (event.button_mask & bit) event_flags |= PTR_FLAGS_DOWN;
                if (!input->MouseEvent ||
                    !input->MouseEvent(input, event_flags, event.x, event.y)) continue;
            }
            session->mouse_buttons = event.button_mask;
            if (input->MouseEvent)
                (void)input->MouseEvent(input, PTR_FLAGS_MOVE, event.x, event.y);
        } else if (event.type == REMOTE_INPUT_WHEEL) {
            uint16_t magnitude = (uint16_t)(event.wheel_delta < 0
                ? -event.wheel_delta : event.wheel_delta);
            if (magnitude > 0x00ffu) magnitude = 0x00ffu;
            uint16_t flags = (uint16_t)(PTR_FLAGS_WHEEL | magnitude);
            if (event.wheel_delta < 0) flags |= PTR_FLAGS_WHEEL_NEGATIVE;
            if (input->MouseEvent) (void)input->MouseEvent(input, flags, event.x, event.y);
        } else if (event.type == REMOTE_INPUT_KEY) {
            DWORD scan_code = GetVirtualScanCodeFromVirtualKeyCode(
                event.virtual_key, WINPR_KBD_TYPE_IBM_ENHANCED);
            if (!scan_code || !input->KeyboardEvent) continue;
            uint16_t flags = event.down ? 0u : KBD_FLAGS_RELEASE;
            if (RDP_SCANCODE_EXTENDED(scan_code)) flags |= KBD_FLAGS_EXTENDED;
            (void)input->KeyboardEvent(input, flags, RDP_SCANCODE_CODE(scan_code));
        }
    }
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
           freerdp_settings_set_bool(settings, FreeRDP_FastPathInput, FALSE) &&
           freerdp_settings_set_bool(settings, FreeRDP_SupportDisplayControl,
                                     config->use_all_monitors != 0) &&
           freerdp_settings_set_bool(settings, FreeRDP_DynamicResolutionUpdate,
                                     config->use_all_monitors != 0) &&
           freerdp_settings_set_bool(settings, FreeRDP_UseMultimon,
                                     config->use_all_monitors != 0) &&
           freerdp_settings_set_bool(settings, FreeRDP_SpanMonitors,
                                     config->use_all_monitors != 0) &&
           freerdp_settings_set_bool(settings, FreeRDP_RedirectClipboard, FALSE) &&
           freerdp_settings_set_bool(settings, FreeRDP_RedirectPrinters, FALSE) &&
           freerdp_settings_set_bool(settings, FreeRDP_RedirectDrives, FALSE) &&
           freerdp_settings_set_bool(settings, FreeRDP_DeviceRedirection, FALSE) &&
           freerdp_settings_set_bool(settings, FreeRDP_IgnoreCertificate,
                                     config->allow_untrusted_certificate != 0);
}

uint32_t remote_rdp_session_connect(remote_rdp_session* session, const remote_rdp_config* config) {
    if (!session || !session->instance || !config || !config->hostname) return 1u;
    session->config = *config;
    session->config.use_all_monitors = 0;
    session->stopping = 0;
    session->input_focused = 0;
    session->last_frame_time_ms = 0;
    session->has_frame = 0;
    session->dirty_left = session->dirty_top = 0;
    session->dirty_right = session->dirty_bottom = 0;
    if (!configure(session, &session->config)) {
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
        handles[0] = session->input_event;
        DWORD count = freerdp_get_event_handles(session->instance->context, &handles[1], 63);
        if (!count) {
            disconnect_error = freerdp_get_last_error(session->instance->context);
            if (!disconnect_error) disconnect_error = 3u;
            snprintf(session->last_error, sizeof(session->last_error),
                     "RDP event loop has no event handles; HRESULT=0x%08x", disconnect_error);
            break;
        }
        count++;
        DWORD wait_result = WaitForMultipleObjects(count, handles, FALSE, INFINITE);
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
            const uint32_t server_error = freerdp_error_info(session->instance);
            const int ultimatum = freerdp_get_disconnect_ultimatum(session->instance->context);
            snprintf(session->last_error, sizeof(session->last_error),
                     "RDP event processing failed; HRESULT=0x%08x; serverError=0x%08x; ultimatum=%d",
                     disconnect_error, server_error, ultimatum);
            break;
        }
        if (!process_pending_input(session)) {
            disconnect_error = 3u;
            snprintf(session->last_error, sizeof(session->last_error),
                     "RDP input processing failed; HRESULT=0x%08x", disconnect_error);
            break;
        }
        apply_pending_resize(session);
    }
    if (!session->stopping && !disconnect_error) {
        disconnect_error = freerdp_get_last_error(session->instance->context);
        const uint32_t server_error = freerdp_error_info(session->instance);
        const int ultimatum = freerdp_get_disconnect_ultimatum(session->instance->context);
        snprintf(session->last_error, sizeof(session->last_error),
                 "RDP server requested disconnect; HRESULT=0x%08x; serverError=0x%08x; ultimatum=%d",
                 disconnect_error, server_error, ultimatum);
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
    const struct remote_input_event event = {
        .type = REMOTE_INPUT_MOUSE, .button_mask = button_mask, .x = x, .y = y
    };
    return enqueue_input(session, &event) ? 0u : 3u;
}

uint32_t remote_rdp_session_send_wheel(remote_rdp_session* session, uint16_t x, uint16_t y,
                                       int16_t delta) {
    if (!session || !session->instance || !session->instance->context ||
        !session->instance->context->input) return 1u;
    if (session->config.view_only) return 2u;
    const struct remote_input_event event = {
        .type = REMOTE_INPUT_WHEEL, .wheel_delta = delta, .x = x, .y = y
    };
    return enqueue_input(session, &event) ? 0u : 3u;
}

uint32_t remote_rdp_session_send_key(remote_rdp_session* session, uint32_t virtual_key,
                                     uint8_t down) {
    if (!session || !session->instance || !session->instance->context ||
        !session->instance->context->input) return 1u;
    if (session->config.view_only) return 2u;
    const struct remote_input_event event = {
        .type = REMOTE_INPUT_KEY, .down = down, .virtual_key = virtual_key
    };
    return enqueue_input(session, &event) ? 0u : 3u;
}

uint32_t remote_rdp_session_resize(remote_rdp_session* session, uint32_t width, uint32_t height) {
    if (!session || !session->instance || !session->instance->context) return 1u;
    if (width < 200u || height < 200u || width > 8192u || height > 8192u) return 2u;
    if (session->config.use_all_monitors) return 2u;
    pthread_mutex_lock(&session->state_mutex);
    session->pending_width = width;
    session->pending_height = height;
    pthread_mutex_unlock(&session->state_mutex);
    SetEvent(session->input_event);
    return 0u;
}
