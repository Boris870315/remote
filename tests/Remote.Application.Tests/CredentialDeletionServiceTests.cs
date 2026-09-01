using Remote.Application.Connections;
using Remote.Application.Vaults;
using Remote.Infrastructure.Vaults;

namespace Remote.Application.Tests;

public sealed class CredentialDeletionServiceTests
{
    [Fact]
    public async Task DeleteAsync_PermanentlyDeletesCredentialAndClearsReferences()
    {
        using var vault = new CredentialVault();
        var repository = new InMemoryConnectionRepository();
        var definition = new CredentialDefinition
        {
            Id = new CredentialId(Guid.NewGuid()),
            VaultId = new VaultId(Guid.NewGuid()),
            Name = "Windows Admin",
            Kind = CredentialKind.UsernamePassword,
            ProtocolScope = "rdp",
            Username = "operator",
        };
        var connection = new ConnectionProfile
        {
            Id = ConnectionId.New(),
            Name = "Windows Prod",
            Endpoint = new Uri("rdp://10.20.0.24"),
            ProtocolId = "rdp",
            Credential = ConnectionCredentialReference.IdentityCard(definition.VaultId, definition.Id),
        };
        vault.Add(definition, "secret"u8);
        await repository.SaveConnectionAsync(connection);

        var result = await new CredentialDeletionService(vault, repository).DeleteAsync(definition.Id);

        Assert.True(result.WasDeleted);
        Assert.Equal(1, result.ClearedConnectionReferences);
        var saved = Assert.Single(await repository.GetConnectionsAsync());
        Assert.Equal(CredentialReferenceKind.None, saved.Credential.Kind);
        Assert.Throws<KeyNotFoundException>(() => vault.Reveal(definition.Id));
    }
}
