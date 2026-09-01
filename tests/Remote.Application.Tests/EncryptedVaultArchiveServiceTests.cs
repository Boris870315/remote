using System.Text;
using Remote.Application.Connections;
using Remote.Application.Vaults;
using Remote.Infrastructure.Security;
using Remote.Infrastructure.Vaults;

namespace Remote.Application.Tests;

public sealed class EncryptedVaultArchiveServiceTests
{
    [Fact]
    public void ExportAndImport_RoundTripsCredentialWithoutPlaintextLeakage()
    {
        using var source = new CredentialVault();
        var id = new CredentialId(Guid.NewGuid());
        var definition = new CredentialDefinition
        {
            Id = id,
            VaultId = new VaultId(Guid.NewGuid()),
            Name = "Production operator",
            Kind = CredentialKind.UsernamePassword,
            ProtocolScope = "ssh2",
            Username = "operator",
        };
        source.Add(definition, "super-secret"u8);
        var service = new EncryptedVaultArchiveService(new WorkspaceCryptography(
            new WorkspaceEncryptionOptions { Pbkdf2Iterations = 10_000 }));

        var archive = service.Export(source, "master password");
        using var restored = service.Import(archive, "master password");

        Assert.DoesNotContain("super-secret", Encoding.UTF8.GetString(archive), StringComparison.Ordinal);
        Assert.Equal("super-secret"u8.ToArray(), restored.Reveal(id));
        Assert.Equal("operator", Assert.Single(restored.Credentials).Username);
    }

    [Fact]
    public void Import_WithWrongPassword_DoesNotReturnPartialVault()
    {
        using var source = new CredentialVault();
        source.Add(new CredentialDefinition
        {
            Id = new CredentialId(Guid.NewGuid()),
            VaultId = new VaultId(Guid.NewGuid()),
            Name = "Card",
            Kind = CredentialKind.UsernamePassword,
            ProtocolScope = "rdp",
        }, "secret"u8);
        var service = new EncryptedVaultArchiveService(new WorkspaceCryptography(
            new WorkspaceEncryptionOptions { Pbkdf2Iterations = 10_000 }));
        var archive = service.Export(source, "correct");

        Assert.Throws<WorkspaceUnlockException>(() => service.Import(archive, "wrong"));
    }
}
