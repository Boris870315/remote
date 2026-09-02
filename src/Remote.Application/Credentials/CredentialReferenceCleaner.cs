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
            var legacyMatches = Matches(folder.Credential, vaultId, credentialId);
            var updatedProtocolCredentials = folder.ProtocolCredentials
                .Where(pair => !Matches(pair.Value, vaultId, credentialId))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var removedProtocolReferences = folder.ProtocolCredentials.Count - updatedProtocolCredentials.Count;
            if (!legacyMatches && removedProtocolReferences == 0)
            {
                return folder;
            }

            clearedFolders += (legacyMatches ? 1 : 0) + removedProtocolReferences;
            return folder with
            {
                Credential = legacyMatches ? ConnectionCredentialReference.None : folder.Credential,
                ProtocolCredentials = updatedProtocolCredentials,
            };
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
