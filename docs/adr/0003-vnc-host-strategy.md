# ADR 0003: Managed VNC host strategy

- Status: Accepted
- Date: 2026-09-02

## Decision

Remote uses an embedded, managed RFB client for VNC sessions. The first implementation negotiates RFB 3.3, 3.7, or 3.8, preferring 3.8, and requests Hextile, CopyRect, Raw framebuffer, and DesktopSize pseudo-encoding. The desktop composes BGRA rectangles into an Avalonia `WriteableBitmap` and maps pointer coordinates for Fit, Fill, 100%, and Scroll scaling.

View Only is enforced inside the RFB client before any keyboard or pointer message reaches the transport. The UI also avoids treating input as handled when the protocol gate rejects it.

Classic VNC Authentication is supported for compatibility, including the protocol-mandated DES challenge response. A supplied password must never silently downgrade to unauthenticated access. Passwords remain transient memory input and will be provided by the encrypted Vault/Identity Card layer; they are never stored in `ProtocolSettings`. The active Session status identifies whether None or classic VNC Authentication was negotiated and explicitly states that the transport is unencrypted.

## Security boundary

RFB None and classic VNC Authentication do not encrypt the framebuffer or later input. They are suitable only on a trusted network or inside an SSH/VPN tunnel. A later compatibility increment may add VeNCrypt/TLS after its certificate validation and trust UI are designed. The application must surface this transport-security state instead of implying that classic VNC Authentication encrypts the session.

## Compatibility evolution

Servers are asked to use Hextile, CopyRect, and Raw encodings, with Raw providing the required dependable baseline. Additional encodings such as Tight can be added behind isolated decoders without changing the frame sink or Session UI. Per-remote-monitor tabs or windows remain a required later display evolution.
