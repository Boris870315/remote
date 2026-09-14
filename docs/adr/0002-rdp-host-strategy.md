# ADR 0002: RDP host strategy

## Status

Accepted

## Decision

Remote uses platform-equivalent RDP hosts behind the same Connection and Session contracts:

1. On Windows, generation 1 embeds Microsoft's installed Remote Desktop ActiveX control in each Avalonia Session tab. Every tab owns a separate control instance. Remote supplies the endpoint and resolved Identity Card directly, uses Smart Sizing and dynamic display resize where available, and disables the control window when View Only is active.
2. On macOS, generation 1 embeds FreeRDP behind a native bridge. Each Session owns its framebuffer, input queue, dynamic display channel, clipboard channel, and device channels. The signed application bundle carries the required native runtime and notices.
3. The platform hosts implement the same Session contract: in-window display, concurrent tabs, dynamic View Only, display resize, clipboard, selected device redirection, credential handoff, and deterministic disconnect cleanup. Platform-specific capabilities may use different native mechanisms but must preserve the same user-visible policy.

The external fallback must reject View Only because Remote cannot prove that another process will suppress all outbound input. It may expose only settings that the platform client can enforce. Unsupported settings fail clearly instead of being silently ignored.

## Consequences

- Windows can keep multiple embedded RDP controls alive and switch their visibility with the Session tabs.
- Windows prefers the newest installed Microsoft RDP control and falls back through supported older versions. Camera redirection uses the dedicated camera configuration collection instead of enabling every PnP device.
- macOS depends on the bundled FreeRDP bridge and renders its framebuffer in the Avalonia Session surface.
- Windows RDP behavior depends on the interfaces exposed by the installed Microsoft control, so unavailable optional COM members must fail clearly or degrade safely without terminating unrelated Sessions.
- View Only requires a host controlled by Remote; it must not silently fall back to an external client.
- FreeRDP's Apache-2.0 notices and platform-native build, signing, scanning, and packaging obligations are release requirements.
