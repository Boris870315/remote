# Windows generation-1 acceptance

Status: **not accepted**.

Passing builds and automated tests establish engineering readiness only. They do not complete visual, interaction, accessibility, or real-host protocol acceptance. Checked items record completed human acceptance. Packaging and publication are tracked separately and do not count toward the 1.0 feature-progress percentage.

## Application UI

- [x] The main layout remains usable at normal, maximized, narrow, and high-DPI window sizes.
- [x] Navigation, connection tree, Session tabs, inspector, dialogs, and notifications have no clipping or overlap.
- [ ] Mouse and keyboard navigation reach every first-generation action with visible focus.

## Vault — accepted

- [x] Enter unlocks the Vault from the master-password field; the button produces the same result.
- [x] Enter unlocks with a Recovery Key; incorrect secrets show a clear error and keep the Vault locked.
- [x] Workspace create, reopen, auto-lock, manual lock, sleep/logout lock, backup, and restore work with real encrypted data.

## Connection management

- [x] Folder create, rename, delete confirmation, inheritance, search, favorites, and tags behave as shown.
- [x] Connection create and edit work for every first-generation protocol.
- [x] ID Card create, edit, assignment, inheritance, deletion, and Vault-lock behavior are understandable and correct.
- [ ] VNC asks only for a password and never requires or displays a username or domain.
- [ ] mRemoteNG import reports imported and skipped items accurately.

## Windows RDP — accepted

- [x] A real RDP host reaches the Connected state instead of remaining at ActiveX `Connected=2`.
- [x] The remote desktop renders once, fills the intended Session surface, and does not split, duplicate, or cover the application UI.
- [x] Keyboard, pointer, clipboard, scaling, resize, full screen, and dynamic View Only work against the real host.
- [x] Enabled printer, drive, microphone, camera, audio, administrator Session, Gateway, and certificate options behave as documented.
- [x] Authentication failure, certificate failure, host unreachable, timeout, and remote disconnect show actionable messages and preserve unrelated Sessions.
- [x] Multiple RDP tabs can connect, switch, resize, disconnect, and close independently.
- [x] Closing the application during connection and after connection leaves no Remote process or ActiveX host behind.

## Windows VNC — accepted

- [x] A real VNC server completes no-authentication and password-authentication connections as configured.
- [x] Repeated framebuffer updates remain responsive during an extended Session.
- [x] Keyboard, pointer, drag, wheel, text clipboard, scaling, resize, full screen, and dynamic View Only work against the real server.
- [x] Authentication failure, host unreachable, timeout, malformed updates, and disconnect show actionable messages.
- [x] The UI states that standard VNC does not redirect drives and directs file exchange to SSH/SFTP or an operating-system share.

## Workspace compatibility

- [x] Existing schema-3 workspaces remain compatible and diagnostic logs contain no secrets.

SSH2, HTTP/HTTPS, and local Terminal are deferred to the next version and do not count toward 1.0 feature acceptance.

Release packaging and clean-machine checks are tracked in `release-1.0.md` and are excluded from the 1.0 feature-progress count.
