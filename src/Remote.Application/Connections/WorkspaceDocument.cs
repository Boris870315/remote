using Remote.Application.Credentials;
using Remote.Application.Vaults;

namespace Remote.Application.Connections;

/// <summary>The secret-free model stored inside the encrypted Workspace envelope.</summary>
public sealed record WorkspaceDocument
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<ConnectionFolder> Folders { get; init; } = [];

    public IReadOnlyList<ConnectionProfile> Connections { get; init; } = [];

    public IReadOnlyList<IdentityCard> IdentityCards { get; init; } = [];

    /// <summary>An independently encrypted primary Vault archive. Never contains plaintext secrets.</summary>
    public byte[]? EncryptedPrimaryVault { get; init; }

    public WorkspacePreferences Preferences { get; init; } = new();
}

public sealed record WorkspacePreferences
{
    public VaultLockSettings VaultLock { get; init; } = new();
    public bool AutomaticBackupEnabled { get; init; }
    public PasswordRevealMode PasswordReveal { get; init; } = PasswordRevealMode.TenSeconds;
    public bool HideSensitiveContentFromCapture { get; init; } = true;
    public bool ClearClipboardAfterUse { get; init; }
}

public enum PasswordRevealMode { FiveSeconds, TenSeconds, Permanent }
