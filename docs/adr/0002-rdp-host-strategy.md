# ADR 0002: RDP host strategy

## Status

Accepted

## Decision

Remote uses two RDP hosts behind the same Connection and Session contracts:

1. A bundled embedded host based on FreeRDP 3.x is the preferred host. It renders into the Avalonia Session surface and enforces View Only in both managed input routing and the native shim.
2. The platform-native client is a fallback: `mstsc.exe` on Windows and Microsoft's documented `rdp://` URI on macOS. Passwords are never placed in command-line arguments or URIs.

The external fallback must reject View Only because Remote cannot prove that another process will suppress all outbound input. It may expose only settings that the platform client can enforce. Unsupported settings fail clearly instead of being silently ignored.

FreeRDP is Apache-2.0 licensed and is kept behind a narrow C ABI so the managed application does not depend on unstable native structures.

## Consequences

- Interactive RDP can use a platform client before the embedded host is installed.
- View Only requires the embedded host.
- Native binaries must be built, signed, scanned, and packaged separately for Windows and macOS architectures.
- FreeRDP notices and source-offer obligations must be included in release artifacts.
