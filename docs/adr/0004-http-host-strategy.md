# ADR 0004: Native HTTP/HTTPS host strategy

- Status: Accepted
- Date: 2026-09-02

## Decision

Remote embeds web connections with the official open-source `Avalonia.Controls.WebView` 12.1.0 package. It uses Edge WebView2 on Windows and WKWebView on macOS, so the operating system browser engine owns TLS certificate validation and modern web compatibility.

Only absolute `http` and `https` destinations are accepted. When a user enters a host without a scheme, Remote defaults to HTTPS. URLs containing embedded username/password information are rejected so credentials cannot leak into history, logs, connection metadata, or screenshots. Identity Card integration remains separate from the URL.

View Only disables the address controls and WebView hit testing. This prevents user-originated navigation, form input, link activation, and other pointer/keyboard interaction while allowing the page to render and update. Remote does not weaken native TLS validation or add a certificate-warning bypass.

## Platform behavior

- Windows uses WebView2 and may require its runtime on Windows 10.
- macOS uses the system WKWebView and requires no bundled browser runtime.
- A missing native adapter is reported as a session failure; a later packaging step may provide an explicit external-browser fallback, but it must preserve the View Only contract and never silently downgrade it.
