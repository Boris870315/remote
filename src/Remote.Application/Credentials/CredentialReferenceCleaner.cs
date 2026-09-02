using Remote.Application.Connections;

namespace Remote.Application.Credentials;

public sealed class CredentialReferenceCleaner
{
    public CredentialReferenceCleanupResult Clear(
        VaultId vaultId,
        CredentialId credentialId,
        IEnumerable<ConnectionProfile> connections,
        IEnumerable<ConnectionFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(folders);

        var clearedConnections = 0;
        var updatedConnections = connections.Select(connection =>
        {
            if (!Matches(connection.Credential, vaultId, credentialId))
            {
                return connection;
            }

            clearedConnections++;
            return connection with { Credential = ConnectionCredentialReference.None };
        }).ToArray();

        var clearedFolders = 0;
        var updatedFolders = folders.Select(folder =>
        {
            if (!Matches(folder.Credential, vaultId, credentialId))
            {
                return folder;
            }

            clearedFolders++;
            return folder with { Credential = ConnectionCredentialReference.None };
        }).ToArray();

        return new CredentialReferenceCleanupResult(
            updatedConnections,
            updatedFolders,
            clearedConnections,
            clearedFolders);
    }

    private static bool Matches(
        ConnectionCredentialReference reference,
        VaultId vaultId,
        CredentialId credentialId) =>
        reference.Kind is CredentialReferenceKind.IdentityCard &&
        reference.VaultId == vaultId &&
        reference.CredentialId == credentialId;
}

public sealed record CredentialReferenceCleanupResult(
    IReadOnlyList<ConnectionProfile> Connections,
    IReadOnlyList<ConnectionFolder> Folders,
    int ClearedConnectionReferences,
    int ClearedFolderReferences);
