# macOS generation-1 acceptance

Status: **partially accepted**.

Checked items have direct evidence from this Mac. Automated protocol stress tests establish engineering confidence, but do not replace an extended Session against a real remote host.

## Vault — accepted

- [x] Master Password and Recovery Key unlock, incorrect-secret handling, workspace create/reopen, automatic/manual/sleep/logout locking, encrypted backup/restore, schema compatibility, and secret-free diagnostics are accepted.

## Application and UI

- [x] The application launches and the normal-size main window, Connection tree, inspector, Vault unlock layer, and primary controls render without clipping or overlap.
- [x] The Vault unlock layer blocks interaction with the workspace while locked.
- [x] Adaptive layout calculations pass for 1440×900, 1024×768, 700×900, 1080×1920, and bounded compact-editor widths.
- [ ] Compact, minimum-size, maximized, and full-screen windows have been visually inspected by resizing the real application.
- [ ] Keyboard navigation reaches every generation-1 action with visible focus.
- [ ] The macOS application menu displays `Remote` instead of `Avalonia Application`.

The real-window resize and keyboard checks are pending because macOS denied Accessibility control to the test runner. This is an environment limitation, not a passing result.

## RDP

- [ ] A real RDP host connects and renders reliably through the embedded FreeRDP bridge.
- [ ] Keyboard, pointer, clipboard, scaling, resize, full screen, and dynamic View Only work against the real host.
- [ ] Enabled printer, drive, microphone, camera, audio, administrator Session, Gateway, and certificate options behave as documented.
- [ ] Authentication failure, certificate failure, host unreachable, timeout, and remote disconnect show actionable messages and preserve unrelated Sessions.
- [ ] Multiple RDP tabs connect, switch, enter the background-throttled state, resume, resize, disconnect, and close independently.
- [ ] Closing the application during and after an RDP connection leaves no Remote or FreeRDP process behind.

## VNC

- [x] RFB negotiation, no-authentication, classic password authentication, framebuffer updates, CopyRect, malformed updates, disconnect behavior, clipboard limits, input gating, and View Only pass the automated protocol suite.
- [x] The VNC-related protocol suite completed 20 consecutive stress rounds without a failure.
- [ ] A real VNC server remains responsive during an extended interactive Session, including pointer, keyboard, drag, wheel, clipboard, scaling, resize, and full screen.

No VNC server was listening on this Mac during acceptance, so real-host behavior remains pending.

## Deferred protocol evidence — next version

The results below are retained as engineering evidence, but SSH2, HTTP/HTTPS, and local Terminal do not count toward 1.0 feature acceptance.

### SSH2

- [x] The local SSH endpoint is reachable and rejects unauthenticated access normally.
- [x] Known Host policy behavior passes the automated test suite.
- [ ] A password-authenticated SSH Session has completed interactive input, resize, View Only, disconnect, and extended-duration testing.

Remote currently uses password authentication for SSH. No operator password was provided to the test runner, so successful login was not attempted.

### HTTP and HTTPS

- [x] URL validation, HTTPS defaults, credential-bearing URL rejection, and View Only navigation policy pass the automated test suite.
- [x] The Web-related policy suite completed 20 consecutive stress rounds without a failure.
- [ ] The embedded WKWebView has completed navigation, refresh, back, close, certificate failure, and extended-duration testing in an unlocked workspace.

### Local Terminal

- [x] The real macOS PTY completes command round trips and blocks writes in View Only.
- [x] Terminal creation, command round trip, View Only, and disposal completed 20 consecutive stress rounds without a failure.
- [ ] The Terminal UI has completed extended interactive input, output, resize, tab switching, close, and application-shutdown testing in an unlocked workspace.

## Resource observation

- [x] With the locked workspace open for approximately two minutes, the tested Debug application instance sampled at 0% CPU and approximately 54 MB resident memory.
- [ ] CPU, memory, and network behavior have been measured with simultaneous real RDP and VNC Sessions.
