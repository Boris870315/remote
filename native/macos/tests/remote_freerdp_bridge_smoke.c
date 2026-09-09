#include "../remote_freerdp_bridge.h"
#include <stdio.h>

int main(void) {
    remote_rdp_capabilities capabilities = { 0 };
    if (remote_rdp_get_capabilities(&capabilities) != 0u ||
        capabilities.abi_version != REMOTE_FREERDP_ABI_VERSION ||
        !capabilities.supports_framebuffer || !capabilities.supports_dynamic_resolution) {
        fputs("capability probe failed\n", stderr);
        return 1;
    }
    remote_rdp_session* session = remote_rdp_session_new();
    if (!session) {
        fputs("session allocation failed\n", stderr);
        return 2;
    }
    remote_rdp_session_free(session);
    printf("FreeRDP %u.%u.%u ABI %u OK\n", capabilities.freerdp_major,
           capabilities.freerdp_minor, capabilities.freerdp_revision,
           capabilities.abi_version);
    return 0;
}
