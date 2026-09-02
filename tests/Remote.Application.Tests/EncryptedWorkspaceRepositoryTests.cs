using Remote.Application.Connections;
using Remote.Application.Credentials;
using Remote.Infrastructure.Security;
using Remote.Infrastructure.Storage;
using Remote.Protocols;
using System.Text;

namespace Remote.Application.Tests;

public sealed class EncryptedWorkspaceRepositoryTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsConnectionTreeAndIdentityReferences()
    {
        var path = Path.Combine(Path.GetTempPath(), $"remote-{Guid.NewGuid():N}.workspace");
        try
        {
            var vaultId = new VaultId(Guid.NewGuid());
            var credentialId = new CredentialId(Guid.NewGuid());
            var folderId = FolderId.New();
            var repository = new EncryptedWorkspaceRepository(
                new EncryptedWorkspaceFile(new WorkspaceCryptography(
                    new WorkspaceEncryptionOptions { Pbkdf2Iterations = 10_000 })));
            var document = new WorkspaceDocument
            {
                Folders =
                [
                    new ConnectionFolder
                    {
                        Id = folderId,
                        Name = "Production",
                        ProtocolCredentials = new Dictionary<string, ConnectionCredentialReference>
                        {
                            ["ssh2"] = ConnectionCredentialReference.IdentityCard(vaultId, credentialId),
                        },
                    },
                ],
                Connections =
                [
                    new ConnectionProfile
                    {
                        Id = ConnectionId.New(),
                        Name = "Shell",
                        Endpoint = new Uri("ssh://server.example:22"),
                        ProtocolId = "ssh2",
                        FolderId = folderId,
                        DefaultAccessMode = SessionAccessMode.ViewOnly,
                        ProtocolSettings = new ProtocolSettings().Set("keepAliveSeconds", "30"),
                    },
                ],
                IdentityCards =
                [
                    new IdentityCard
                    {
                        VaultId = vaultId,
                        CredentialId = credentialId,
                        Name = "SSH operator",
                        ProtocolId = "ssh2",
                        Username = "operator",
                    },
                ],
            };

            await repository.SaveAsync(path, document, "correct horse battery staple");
            var loaded = await repository.LoadAsync(path, "correct horse battery staple");

            var loadedFolder = Assert.Single(loaded.Folders);
            Assert.Equal("Production", loadedFolder.Name);
            Assert.Equal(credentialId, loadedFolder.GetCredential("ssh2").CredentialId);
            var connection = Assert.Single(loaded.Connections);
            Assert.Equal(folderId, connection.FolderId);
            Assert.Equal(SessionAccessMode.ViewOnly, connection.DefaultAccessMode);
            Assert.Equal("30", connection.ProtocolSettings.Get("keepAliveSeconds"));
            Assert.Equal("operator", Assert.Single(loaded.IdentityCards).Username);
            Assert.DoesNotContain("operator", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
