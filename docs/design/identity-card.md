# Vault and ID Card interaction

## Vault

The Vault is the separately unlockable encrypted container. Its screen owns master-password unlock, biometric quick unlock when a platform adapter is available, inactivity/sleep/logout locking, recovery keys, and encrypted backups. It does not represent a login that a Connection can select.

## ID Card

An ID Card is a username/password Credential stored inside a Vault. It has a name, one protocol scope, a username, a password, and an optional domain. The first release supports RDP, VNC, SSH2, HTTP, and HTTPS scopes. Terminal uses the local operating-system identity and does not offer an ID Card.

The ID Card screen is independent from the Vault security screen. When the Vault is locked it shows an unlock route without exposing metadata. When unlocked it supports create, edit, permanent delete, mRemoteNG import, assignment to a Connection, assignment to a Folder, and a usage list containing both direct and inherited consumers.

Changing only the name, username, or domain preserves the current password. Entering a new password creates a new retained secret version. The protocol scope cannot be changed while editing an existing card; create a separate card for a different protocol.

## Connection credential source

A Connection stores only a credential reference. Its editor offers:

1. inherit the protocol-scoped ID Card assigned to its Folder;
2. select a compatible ID Card from the unlocked Vault;
3. create a compatible ID Card and store it in the Vault;
4. keep no saved reference and request session-only credentials when connecting.

The inspector shows the resolved card and whether it is direct, inherited, or session-only. A saved direct or inherited ID Card is supplied to the protocol adapter automatically while the Vault is unlocked; routine saved connections do not prompt again.
