# ADR 0002: RDP host strategy

## Status

Accepted

## Decision

Remote uses platform-equivalent RDP hosts behind the same Connection and Session contracts:

1. On Windows, generation 1 embeds Microsoft's installed Remote Desktop ActiveX control in each Avalonia Session tab. Every tab owns a separate control instance. Remote supplies the endpoint and resolved Identity Card directly, uses Smart Sizing and dynamic display resize where available, and disables the control window when View Only is active.
2. On macOS, generation 1 hands interactive Sessions to Microsoft's documented `rdp://` integration. Passwords are never placed in command-line arguments or URIs.
3. A future cross-platform embedded host may use FreeRDP behind the existing RDP host boundary after its native binaries, signing, packaging, credential handoff, display integration, and View Only enforcement are complete.

The external fallback must reject View Only because Remote cannot prove that another process will suppress all outbound input. It may expose only settings that the platform client can enforce. Unsupported settings fail clearly instead of being silently ignored.

## Consequences

- Windows can keep multiple embedded RDP controls alive and switch their visibility with the Session tabs.
- Windows RDP behavior depends on the interfaces exposed by the installed Microsoft control, so unavailable optional COM members must degrade safely and never terminate the application.
- macOS interactive RDP currently opens externally and cannot provide an in-window Session surface.
- View Only requires a host controlled by Remote; it must not silently fall back to an external client.
- If FreeRDP is adopted later, its Apache-2.0 notices and platform-native build, signing, scanning, and packaging obligations become release requirements.
