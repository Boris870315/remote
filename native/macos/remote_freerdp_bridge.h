#pragma once
#include <stdint.h>

#if defined(__cplusplus)
extern "C" {
#endif

#define REMOTE_FREERDP_ABI_VERSION 1u
#define REMOTE_RDP_EXPORT __attribute__((visibility("default")))

typedef struct remote_rdp_session remote_rdp_session;
typedef void (*remote_rdp_frame_callback)(void* state, const uint8_t* pixels,
                                          uint32_t width, uint32_t height, uint32_t stride);
typedef void (*remote_rdp_state_callback)(void* state, uint32_t state_code,
                                          uint32_t error_code, const char* message);

typedef struct remote_rdp_config {
    const char* hostname;
    const char* username;
    const char* password;
    const char* domain;
    uint16_t port;
    uint32_t width;
    uint32_t height;
    uint8_t view_only;
    uint8_t allow_untrusted_certificate;
    uint8_t reserved[6];
    void* callback_state;
    remote_rdp_frame_callback frame_callback;
    remote_rdp_state_callback state_callback;
} remote_rdp_config;

typedef struct remote_rdp_capabilities {
    uint32_t abi_version;
    uint32_t freerdp_major;
    uint32_t freerdp_minor;
    uint32_t freerdp_revision;
    uint8_t supports_framebuffer;
    uint8_t supports_dynamic_resolution;
    uint8_t reserved[6];
} remote_rdp_capabilities;

REMOTE_RDP_EXPORT uint32_t remote_rdp_get_capabilities(remote_rdp_capabilities* capabilities);
REMOTE_RDP_EXPORT remote_rdp_session* remote_rdp_session_new(void);
REMOTE_RDP_EXPORT void remote_rdp_session_free(remote_rdp_session* session);
REMOTE_RDP_EXPORT const char* remote_rdp_session_last_error(const remote_rdp_session* session);
REMOTE_RDP_EXPORT uint32_t remote_rdp_session_connect(remote_rdp_session* session,
                                                       const remote_rdp_config* config);
REMOTE_RDP_EXPORT void remote_rdp_session_disconnect(remote_rdp_session* session);
REMOTE_RDP_EXPORT uint32_t remote_rdp_session_send_mouse(remote_rdp_session* session,
                                                          uint16_t x, uint16_t y,
                                                          uint8_t button_mask);
REMOTE_RDP_EXPORT uint32_t remote_rdp_session_send_wheel(remote_rdp_session* session,
                                                          uint16_t x, uint16_t y,
                                                          int16_t delta);
REMOTE_RDP_EXPORT uint32_t remote_rdp_session_send_key(remote_rdp_session* session,
                                                        uint32_t virtual_key,
                                                        uint8_t down);

#if defined(__cplusplus)
}
#endif
