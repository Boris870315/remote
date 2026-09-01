# Remote Connection Management

Remote is a personal workspace for organizing remote systems, opening sessions through different protocols, and protecting the credentials used by those sessions.

## Language

**Operator**:
The individual who owns and uses a Remote workspace to manage remote systems.
_Avoid_: Customer, administrator, account

**Connection**:
A saved description of how to reach a remote system, including its endpoint, protocol, presentation preferences, and credential reference.
_Avoid_: Server, profile, bookmark

**Folder**:
A hierarchical grouping of Connections that can provide inherited settings and an Inherited Credential to its descendants.
_Avoid_: Group, directory, tag

**Tag**:
A non-hierarchical label for finding related Connections across Folders; it never supplies inherited settings.
_Avoid_: Folder, category

**Favorite**:
A Connection explicitly marked by the Operator for quick access without changing its Folder or Tags.
_Avoid_: Pinned folder, recent connection

**Session**:
A live interaction opened from a Connection.
_Avoid_: Connection, tab

**Protocol**:
The connection method used by a Connection, such as RDP, SSH, VNC, or HTTPS.
_Avoid_: Connection type, driver

**Known Host**:
An SSH endpoint whose host-key fingerprint the Operator has accepted; a changed fingerprint invalidates that trust until separately resolved.
_Avoid_: Saved server, trusted Credential

**Quick Connect**:
A temporary connection initiated without first saving a Connection; it may use an unsaved Credential for that session only.
_Avoid_: Temporary Connection, guest connection

**Vault**:
A separately unlockable protected collection of Credentials. The first release has one primary Vault, while the domain permits additional Vaults later.
_Avoid_: Password list, keychain, repository

**Credential**:
A reusable secret-bearing identity that a Connection references, such as a username and password, SSH key, API key, or one-time-password seed.
_Avoid_: Account, login, secret

**Identity Card**:
A named username, password, and optional domain Credential with an explicit intended protocol or target. Identity Cards are not shared across incompatible purposes: for example, an RDP card may be reused by selected RDP Connections, but is not automatically available to VNC. Editing a card changes the identity used by every compatible Connection that explicitly references it.
_Avoid_: Credential group, user profile, embedded password

**Inherited Credential**:
A Credential selected by a containing Folder and used by descendant Connections unless a Connection selects a different Credential.
_Avoid_: Copied credential, default password

**Master Password**:
The Operator-known recovery secret that can unlock the Vault when biometric authentication is unavailable.
_Avoid_: Login password, PIN

**Recovery Key**:
A high-entropy, one-time-issued secret that can unlock a Vault when its Master Password is unavailable.
_Avoid_: Backup password, security question

**Vault Backup**:
An encrypted, restorable snapshot of a Vault and its non-secret organizational data.
_Avoid_: Export, copy, sync

**Audit Event**:
A secret-free record that a sensitive Vault action occurred, identifying the action, affected item, and time without recording protected content.
_Avoid_: Activity log, debug log

**Legacy Import**:
A one-time conversion of supported mRemoteNG data into the Remote workspace; imported data is not kept synchronized with its source.
_Avoid_: Migration sync, legacy compatibility mode

**Platform Equivalent**:
A platform-native way to deliver the same user outcome on Windows and macOS when the underlying integration cannot be shared.
_Avoid_: Identical implementation, reduced version

## Display and session decisions

- Remote window orientation and remote-display orientation are independent.
- The default scaling mode is aspect-preserving Fit to Window. The Operator may select 100%, Fill, or Scroll.
- RDP uses dynamic resolution when the adapter and remote endpoint support it. VNC and other adapters fall back to client-side scaling without forcing a reconnect.
- Remote does not rotate captured pixels. Display rotation remains the responsibility of the remote operating system so pointer coordinates stay correct.
- Each Connection stores its own scaling, full-screen, resolution, DPI, monitor-selection, and session-toolbar preferences.
- Version 1 supports selecting one remote monitor and spanning all remote monitors.
- A later release must support opening each remote monitor in its own tab or native window.
- In a portrait or narrow local window, the Connection tree and inspector become drawers while the Session retains the available workspace.
- Small layouts collapse secondary labels and toolbar actions before reducing the Session below its usable minimum. Essential pointer targets remain at least 44 by 44 device-independent pixels.
- Clipboard text may be enabled per Connection. File transfer, drive, printer, microphone, camera, and other device redirection are disabled by default and require explicit per-Connection enablement.
- A Connection may have concurrent Sessions. Opening another Session prompts the Operator to switch to the existing Session or explicitly open another.
- Every Identity Card has one explicit protocol or intended-use scope. Reusing the same secret for another scope requires a separate Identity Card; a future safe-copy action may assist without making the records shared.
- VNC negotiates the official RFB protocol versions 3.3, 3.7, and 3.8, preferring 3.8. Newer security, encoding, and interoperability features are modeled as negotiated capabilities or extensions rather than invented RFB version numbers.
- Every Protocol must support the shared View Only Session mode. The setting is stored per Connection, may be overridden before opening a Session, and remains visibly indicated in the active Session toolbar.
- Each protocol adapter enforces View Only according to its interaction model: RDP and VNC suppress keyboard, pointer, outbound clipboard, file transfer, and device input; SSH and Terminal suppress standard input and command transmission while continuing to display output; HTTP and HTTPS block form submission, uploads, modifying requests, scripting bridges, and other state-changing actions while retaining read-only navigation where the embedded engine can enforce it.
- Server-enforced read-only permission is preferred when available. Client-side suppression is defense in depth and is not presented as a server-side authorization boundary.
- An adapter that cannot reliably enforce its View Only contract must fail the Session in View Only mode with a clear explanation; it must never silently fall back to interactive control.
