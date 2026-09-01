namespace Remote.Application.Connections;

/// <summary>References credentials without placing secret material on a Connection.</summary>
public sealed record ConnectionCredentialReference
{
    private ConnectionCredentialReference(
        CredentialReferenceKind kind,
        VaultId? vaultId,
        CredentialId? credentialId)
    {
        Kind = kind;
        VaultId = vaultId;
        CredentialId = credentialId;
    }

    public CredentialReferenceKind Kind { get; }

    public VaultId? VaultId { get; }

    public CredentialId? CredentialId { get; }

    public static ConnectionCredentialReference Inherited { get; } =
        new(CredentialReferenceKind.Inherited, null, null);

    public static ConnectionCredentialReference None { get; } =
        new(CredentialReferenceKind.None, null, null);

    public static ConnectionCredentialReference IdentityCard(
        VaultId vaultId,
        CredentialId credentialId) =>
        new(CredentialReferenceKind.IdentityCard, vaultId, credentialId);
}

public enum CredentialReferenceKind
{
    None,
    Inherited,
    IdentityCard,
}

public readonly record struct VaultId(Guid Value);

public readonly record struct CredentialId(Guid Value);
