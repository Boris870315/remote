using Remote.Application.Connections;

namespace Remote.Application.Credentials;

public sealed class ConnectionCredentialResolver
{
    public IdentityCard? ResolveIdentityCard(
        ConnectionProfile connection,
        IEnumerable<ConnectionFolder> folders,
        IEnumerable<IdentityCard> identityCards)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(identityCards);
        var reference = connection.Credential;
        if (reference.Kind is CredentialReferenceKind.Inherited)
        {
            reference = ResolveInheritedReference(connection.FolderId, connection.ProtocolId, folders.ToArray());
        }

        if (reference.Kind is not CredentialReferenceKind.IdentityCard ||
            reference.VaultId is not { } vaultId || reference.CredentialId is not { } credentialId)
        {
            return null;
        }

        var card = identityCards.FirstOrDefault(candidate =>
            candidate.VaultId == vaultId && candidate.CredentialId == credentialId);
        if (card is null)
        {
            throw new InvalidOperationException("The Connection references an Identity Card that no longer exists.");
        }

        if (!card.CanBeUsedBy(connection))
        {
            throw new InvalidOperationException(
                $"Identity Card '{card.Name}' is scoped to {card.ProtocolId.ToUpperInvariant()}, not {connection.ProtocolId.ToUpperInvariant()}.");
        }

        return card;
    }

    private static ConnectionCredentialReference ResolveInheritedReference(
        FolderId? folderId,
        string protocolId,
        IReadOnlyList<ConnectionFolder> folders)
    {
        var visited = new HashSet<FolderId>();
        while (folderId is { } currentId)
        {
            if (!visited.Add(currentId))
            {
                throw new InvalidOperationException("The Connection folder hierarchy contains a cycle.");
            }

            var folder = folders.FirstOrDefault(candidate => candidate.Id == currentId)
                ?? throw new InvalidOperationException("The Connection references a missing Folder.");
            var credential = folder.GetCredential(protocolId);
            if (credential.Kind is CredentialReferenceKind.IdentityCard)
            {
                return credential;
            }

            folderId = folder.ParentId;
        }

        return ConnectionCredentialReference.None;
    }
}
