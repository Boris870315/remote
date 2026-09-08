# Remote product scope

This is the short implementation contract distilled from the product grilling. `CONTEXT.md` defines domain terms; ADRs define technical decisions; the design HTML defines presentation.

## Generation 1

- Personal, local-first workspace with one primary encrypted Vault and no cloud dependency.
- Folder and Connection tree, Favorites, Tags, editing, deletion, search, and one-time mRemoteNG connection import.
- Protocol order: RDP, VNC, Vault plus Connection tree plus SSH2, HTTP/HTTPS, then local Terminal.
- Reusable, protocol-scoped Identity Cards assigned directly to Connections or inherited from Folders. Session-only credentials remain available.
- Multiple concurrent Sessions as tabs in one main window. Double-clicking a Connection always creates a new tab.
- Shared View Only policy, local error log, and modal notification for actionable failures such as rejected credentials and connection timeout.
- Adaptive wide, compact, narrow, and portrait layouts with independently collapsible Connection tree and inspector.
- Per-Connection Fit, Fill, 100%, and Scroll display modes; single-monitor and all-monitor selection where the protocol host supports them.
- Local encrypted backup is optional and disabled by default. Credential deletion is permanent and clears references.

## Generation 2

- Windows Hello and Touch ID quick unlock, with Master Password fallback.
- Multiple Vaults.
- A separately controllable tab or native window for every remote monitor.
- Cross-platform embedded RDP when a host can meet packaging, credential, display, and View Only requirements.
- Modern encrypted VNC security such as VeNCrypt/TLS after certificate trust UX is specified.

## Acceptance boundary

A visible control is not considered complete until its backing operation succeeds or fails safely, records a secret-free diagnostic, and preserves every unrelated Session. Protocol capabilities that cannot be enforced must be shown as unavailable; they must not silently downgrade.
