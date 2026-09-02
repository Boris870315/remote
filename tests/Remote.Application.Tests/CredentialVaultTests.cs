using Remote.Application.Connections;
using Remote.Application.Vaults;
using Remote.Infrastructure.Vaults;
using System.Text;

namespace Remote.Application.Tests;

public sealed class CredentialVaultTests
{
    [Fact]
    public void Update_RetainsAtMostFiveVersionsIncludingCurrent()
    {
        using var vault = new CredentialVault();
        var definition = CreateDefinition();
        vault.Add(definition, "version-0"u8);

        for (var version = 1; version <= 8; version++)
        {
            vault.Update(definition, Encoding.UTF8.GetBytes($"version-{version}"));
        }

        Assert.Equal(CredentialVault.MaximumRetainedVersions, vault.GetVersionCount(definition.Id));
        Assert.Equal("version-8"u8.ToArray(), vault.Reveal(definition.Id));
    }

    [Fact]
    public void Lock_ClearsEntriesAndRejectsFurtherAccess()
    {
        using var vault = new CredentialVault();
        var definition = CreateDefinition();
        vault.Add(definition, "secret"u8);

        vault.Lock();

        Assert.True(vault.IsLocked);
        Assert.Throws<VaultLockedException>(() => vault.Reveal(definition.Id));
    }

    [Fact]
    public void UpdateDefinition_PreservesSecretAndVersionHistory()
    {
        using var vault = new CredentialVault();
        var definition = CreateDefinition();
        vault.Add(definition, "secret"u8);

        vault.UpdateDefinition(definition with { Name = "Renamed Admin", Domain = "CORP" });

        var updated = Assert.Single(vault.Credentials);
        Assert.Equal("Renamed Admin", updated.Name);
        Assert.Equal("CORP", updated.Domain);
        Assert.Equal("secret"u8.ToArray(), vault.Reveal(definition.Id));
        Assert.Equal(1, vault.GetVersionCount(definition.Id));
    }

    private static CredentialDefinition CreateDefinition() => new()
    {
        Id = new CredentialId(Guid.NewGuid()),
        VaultId = new VaultId(Guid.NewGuid()),
        Name = "Windows Admin",
        Kind = CredentialKind.UsernamePassword,
        ProtocolScope = "rdp",
        Username = "operator",
    };
}
