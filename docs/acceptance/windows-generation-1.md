# Windows generation-1 acceptance

Status: **not accepted**.

Passing builds and automated tests establish engineering readiness only. They do not complete visual, interaction, accessibility, or real-host protocol acceptance. Every required item below remains incomplete until a human verifies it in the packaged Windows application.

## Application and Vault

- [ ] A new user can open the packaged application without a crash or missing-runtime prompt.
- [ ] The main layout remains usable at normal, maximized, narrow, and high-DPI window sizes.
- [ ] Navigation, connection tree, Session tabs, inspector, dialogs, and notifications have no clipping or overlap.
- [ ] Mouse and keyboard navigation reach every first-generation action with visible focus.
- [ ] Enter unlocks the Vault from the master-password field; the button produces the same result.
- [ ] Enter unlocks with a Recovery Key; incorrect secrets show a clear error and keep the Vault locked.
- [ ] Workspace create, reopen, auto-lock, manual lock, backup, and restore work with real encrypted data.

## Connection management

- [ ] Folder create, rename, delete confirmation, inheritance, search, favorites, and tags behave as shown.
- [ ] Connection create and edit work for every first-generation protocol.
- [ ] ID Card create, edit, assignment, inheritance, deletion, and Vault-lock behavior are understandable and correct.
- [ ] VNC asks only for a password and never requires or displays a username or domain.
- [ ] mRemoteNG import reports imported and skipped items accurately.

## Windows RDP — release blocker

- [ ] A real RDP host reaches the Connected state instead of remaining at ActiveX `Connected=2`.
- [ ] The remote desktop renders once, fills the intended Session surface, and does not split, duplicate, or cover the application UI.
- [ ] Keyboard, pointer, clipboard, scaling, resize, full screen, and dynamic View Only work against the real host.
- [ ] Enabled printer, drive, microphone, camera, audio, administrator Session, Gateway, and certificate options behave as documented.
- [ ] Authentication failure, certificate failure, host unreachable, timeout, and remote disconnect show actionable messages and preserve unrelated Sessions.
- [ ] Multiple RDP tabs can connect, switch, resize, disconnect, and close independently.
- [ ] Closing the application during connection and after connection leaves no Remote process or ActiveX host behind.

## Windows VNC

- [ ] A real VNC server completes no-authentication and password-authentication connections as configured.
- [ ] Repeated framebuffer updates remain responsive during an extended Session.
- [ ] Keyboard, pointer, drag, wheel, text clipboard, scaling, resize, full screen, and dynamic View Only work against the real server.
- [ ] Authentication failure, host unreachable, timeout, malformed updates, and disconnect show actionable messages.
- [ ] The UI states that standard VNC does not redirect drives and directs file exchange to SSH/SFTP or an operating-system share.

## Other generation-1 protocols and release

- [ ] SSH2 host-key verification, interactive terminal, resize, disconnect, and error handling work against a real SSH host.
- [ ] HTTP and HTTPS embedded browsing, navigation, close, and certificate failure behavior are acceptable.
- [ ] Local Terminal input, output, resize, View Only, close, and shutdown work on Windows.
- [ ] The packaged build preserves existing schema-3 workspaces and produces a usable diagnostic log without secrets.
- [ ] The accepted commit is merged to the release branch and the final package is rebuilt from that exact commit.
