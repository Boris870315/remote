#include "remote_freerdp_bridge.h"

#include <freerdp/freerdp.h>
#include <stdlib.h>
#include <string.h>

struct remote_rdp_session {
    freerdp* instance;
    char last_error[512];
};

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
    remote_rdp_session* session = calloc(1, sizeof(*session));
    if (!session) return NULL;
    session->instance = freerdp_new();
    if (!session->instance) {
        strncpy(session->last_error, "freerdp_new failed", sizeof(session->last_error) - 1);
    }
    return session;
}

void remote_rdp_session_free(remote_rdp_session* session) {
    if (!session) return;
    if (session->instance) freerdp_free(session->instance);
    memset(session, 0, sizeof(*session));
    free(session);
}

const char* remote_rdp_session_last_error(const remote_rdp_session* session) {
    return session ? session->last_error : "invalid session";
}
