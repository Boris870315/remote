using Remote.Application.Connections;

namespace Remote.Application.Credentials;

/// <summary>Metadata for a reusable, protocol-scoped username credential.</summary>
public sealed record IdentityCard
{
    public required VaultId VaultId { get; init; }

    public required CredentialId CredentialId { get; init; }

    public required string Name { get; init; }

    public required string ProtocolId { get; init; }

    public required string Username { get; init; }

    public string? Domain { get; init; }

    public bool CanBeUsedBy(ConnectionProfile connection) =>
        string.Equals(ProtocolId, connection.ProtocolId, StringComparison.OrdinalIgnoreCase);
}
