using Remote.Application.Credentials;

namespace Remote.Application.Connections;

/// <summary>The secret-free model stored inside the encrypted Workspace envelope.</summary>
public sealed record WorkspaceDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<ConnectionFolder> Folders { get; init; } = [];

    public IReadOnlyList<ConnectionProfile> Connections { get; init; } = [];

    public IReadOnlyList<IdentityCard> IdentityCards { get; init; } = [];

    /// <summary>An independently encrypted primary Vault archive. Never contains plaintext secrets.</summary>
    public byte[]? EncryptedPrimaryVault { get; init; }
}
