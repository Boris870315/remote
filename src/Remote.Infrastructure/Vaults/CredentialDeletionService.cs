using Remote.Application.Connections;
using Remote.Application.Vaults;

namespace Remote.Infrastructure.Vaults;

/// <summary>Permanently deletes a Credential and nulls every Connection reference.</summary>
public sealed class CredentialDeletionService(
    CredentialVault vault,
    IConnectionRepository connections)
{
    public async Task<CredentialDeletionResult> DeleteAsync(
        CredentialId credentialId,
        CancellationToken cancellationToken = default)
    {
        var allConnections = await connections.GetConnectionsAsync(cancellationToken).ConfigureAwait(false);
        var affected = allConnections
            .Where(connection => connection.Credential.CredentialId == credentialId)
            .ToArray();

        var deleted = vault.Delete(credentialId);
        if (!deleted)
        {
            return new(credentialId, 0, false);
        }

        foreach (var connection in affected)
        {
            await connections.SaveConnectionAsync(
                connection with { Credential = ConnectionCredentialReference.None },
                cancellationToken).ConfigureAwait(false);
        }

        return new(credentialId, affected.Length, true);
    }
}
