#pragma once
#include <stdint.h>

#if defined(__cplusplus)
extern "C" {
#endif

#define REMOTE_FREERDP_ABI_VERSION 1u
#define REMOTE_RDP_EXPORT __attribute__((visibility("default")))

typedef struct remote_rdp_session remote_rdp_session;

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

#if defined(__cplusplus)
}
#endif
