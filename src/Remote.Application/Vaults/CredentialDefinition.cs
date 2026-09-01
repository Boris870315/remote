using Remote.Application.Connections;

namespace Remote.Application.Vaults;

/// <summary>Secret-free metadata for one Vault Credential.</summary>
public sealed record CredentialDefinition
{
    public required CredentialId Id { get; init; }

    public required VaultId VaultId { get; init; }

    public required string Name { get; init; }

    public required CredentialKind Kind { get; init; }

    public required string ProtocolScope { get; init; }

    public string? Username { get; init; }

    public string? Domain { get; init; }
}

public enum CredentialKind
{
    UsernamePassword,
    SshKey,
    ApiToken,
    Totp,
    SecureNote,
}

public sealed record CredentialDeletionResult(
    CredentialId CredentialId,
    int ClearedConnectionReferences,
    bool WasDeleted);
