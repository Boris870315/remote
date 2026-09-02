using Remote.Application.Connections;
using Remote.Application.Credentials;

namespace Remote.Application.Tests;

public sealed class ConnectionCredentialResolverTests
{
    [Fact]
    public void Resolve_Inherited_UsesNearestAncestorIdentityCard()
    {
        var vaultId = new VaultId(Guid.NewGuid());
        var rootCredential = new CredentialId(Guid.NewGuid());
        var childCredential = new CredentialId(Guid.NewGuid());
        var root = new ConnectionFolder
        {
            Id = FolderId.New(),
            Name = "Root",
            Credential = ConnectionCredentialReference.IdentityCard(vaultId, rootCredential),
        };
        var child = new ConnectionFolder
        {
            Id = FolderId.New(),
            ParentId = root.Id,
            Name = "Child",
            Credential = ConnectionCredentialReference.IdentityCard(vaultId, childCredential),
        };
        var connection = CreateConnection("ssh2", child.Id);
        var cards = new[]
        {
            CreateCard(vaultId, rootCredential, "Root"),
            CreateCard(vaultId, childCredential, "Child"),
        };

        var resolved = new ConnectionCredentialResolver().ResolveIdentityCard(connection, [root, child], cards);

        Assert.Equal("Child", resolved?.Name);
    }

    [Fact]
    public void Resolve_RejectsProtocolScopeMismatch()
    {
        var vaultId = new VaultId(Guid.NewGuid());
        var credentialId = new CredentialId(Guid.NewGuid());
        var connection = CreateConnection("vnc", null) with
        {
            Credential = ConnectionCredentialReference.IdentityCard(vaultId, credentialId),
        };

        Assert.Throws<InvalidOperationException>(() => new ConnectionCredentialResolver().ResolveIdentityCard(
            connection,
            [],
            [CreateCard(vaultId, credentialId, "SSH")])) ;
    }

    [Fact]
    public void Resolve_Inherited_SelectsFolderIdentityForConnectionProtocol()
    {
        var vaultId = new VaultId(Guid.NewGuid());
        var rdpCredential = new CredentialId(Guid.NewGuid());
        var sshCredential = new CredentialId(Guid.NewGuid());
        var folder = new ConnectionFolder
        {
            Id = FolderId.New(),
            Name = "Mixed",
            ProtocolCredentials = new Dictionary<string, ConnectionCredentialReference>(StringComparer.OrdinalIgnoreCase)
            {
                ["rdp"] = ConnectionCredentialReference.IdentityCard(vaultId, rdpCredential),
                ["ssh2"] = ConnectionCredentialReference.IdentityCard(vaultId, sshCredential),
            },
        };

        var resolved = new ConnectionCredentialResolver().ResolveIdentityCard(
            CreateConnection("ssh2", folder.Id),
            [folder],
            [
                CreateCard(vaultId, rdpCredential, "RDP", "rdp"),
                CreateCard(vaultId, sshCredential, "SSH", "ssh2"),
            ]);

        Assert.Equal("SSH", resolved?.Name);
    }

    private static ConnectionProfile CreateConnection(string protocol, FolderId? folderId) => new()
    {
        Id = ConnectionId.New(),
        Name = "Connection",
        Endpoint = new Uri(protocol == "vnc" ? "vnc://host" : "ssh://host"),
        ProtocolId = protocol,
        FolderId = folderId,
    };

    private static IdentityCard CreateCard(
        VaultId vaultId,
        CredentialId credentialId,
        string name,
        string protocol = "ssh2") => new()
    {
        VaultId = vaultId,
        CredentialId = credentialId,
        Name = name,
        ProtocolId = protocol,
        Username = "operator",
    };
}
