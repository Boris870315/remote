using Remote.Application.Connections;
using Remote.Application.Credentials;

namespace Remote.Application.Tests;

public sealed class CredentialReferenceCleanerTests
{
    [Fact]
    public void Clear_NullsOnlyMatchingConnectionAndFolderReferences()
    {
        var vaultId = new VaultId(Guid.NewGuid());
        var credentialId = new CredentialId(Guid.NewGuid());
        var otherCredentialId = new CredentialId(Guid.NewGuid());
        var folder = new ConnectionFolder
        {
            Id = FolderId.New(),
            Name = "Production",
            Credential = ConnectionCredentialReference.IdentityCard(vaultId, credentialId),
        };
        var direct = CreateConnection(
            "Direct",
            ConnectionCredentialReference.IdentityCard(vaultId, credentialId));
        var other = CreateConnection(
            "Other",
            ConnectionCredentialReference.IdentityCard(vaultId, otherCredentialId));

        var result = new CredentialReferenceCleaner().Clear(
            vaultId,
            credentialId,
            [direct, other],
            [folder]);

        Assert.Equal(1, result.ClearedConnectionReferences);
        Assert.Equal(1, result.ClearedFolderReferences);
        Assert.Equal(CredentialReferenceKind.None, result.Connections[0].Credential.Kind);
        Assert.Equal(otherCredentialId, result.Connections[1].Credential.CredentialId);
        Assert.Equal(CredentialReferenceKind.None, result.Folders[0].Credential.Kind);
    }

    private static ConnectionProfile CreateConnection(
        string name,
        ConnectionCredentialReference credential) => new()
        {
            Id = ConnectionId.New(),
            Name = name,
            Endpoint = new Uri("rdp://host"),
            ProtocolId = "rdp",
            Credential = credential,
        };
}
