using Remote.Application.Connections;
using Remote.Infrastructure.Import;
using Remote.Infrastructure.Protocols.Rdp;

namespace Remote.Application.Tests;

public sealed class MRemoteNgXmlImporterTests
{
    private const string EncryptedValue =
        "e/T6ajrPtNNlHreSeD4QBqToTuiqtNACKiPJv7vU+l6TWCu9JNsmL+Y8lJ4aTl5YVcstXpQjxsZ9i8+YV4Gs";

    [Fact]
    public void Import_PreservesFolderInheritanceAndDeduplicatesIdentityCards()
    {
        var xml = $$"""
            <Connections KdfIterations="1000" Protected="{{EncryptedValue}}">
              <Node Type="Container" Name="Production" Username="admin" Domain="CORP"
                    Password="{{EncryptedValue}}" Protocol="RDP" Port="3389">
                <Node Type="Connection" Name="Server 1" Hostname="10.0.0.1"
                      InheritUsername="true" InheritDomain="true" InheritPassword="true"
                      InheritProtocol="true" InheritPort="true" />
                <Node Type="Connection" Name="Server 2" Hostname="10.0.0.2"
                      InheritUsername="true" InheritDomain="true" InheritPassword="true"
                      InheritProtocol="true" InheritPort="true" />
              </Node>
            </Connections>
            """;
        var vaultId = new VaultId(Guid.NewGuid());

        using var result = new MRemoteNgXmlImporter().Import(xml, "Password", vaultId);

        var folder = Assert.Single(result.Folders);
        Assert.Equal(2, result.Connections.Count);
        var card = Assert.Single(result.IdentityCards);
        Assert.Equal("admin", card.Definition.Username);
        Assert.Equal("CORP", card.Definition.Domain);
        Assert.Equal("ThisIsProtected", System.Text.Encoding.UTF8.GetString(card.Secret));
        Assert.All(result.Connections, connection =>
        {
            Assert.Equal("rdp", connection.ProtocolId);
            Assert.Equal(CredentialReferenceKind.Inherited, connection.Credential.Kind);
        });
        Assert.Equal(card.Definition.Id, folder.GetCredential("rdp").CredentialId);
    }

    [Fact]
    public void Import_WarnsAndSkipsUnsupportedProtocols()
    {
        var xml = $$"""
            <Connections KdfIterations="1000" Protected="{{EncryptedValue}}">
              <Node Type="Connection" Name="Raw socket" Hostname="10.0.0.3" Protocol="RAW" />
            </Connections>
            """;

        using var result = new MRemoteNgXmlImporter().Import(
            xml,
            "Password",
            new VaultId(Guid.NewGuid()));

        Assert.Empty(result.Connections);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Import_MapsCommonRdpGatewayAndRedirectionSettings()
    {
        var xml = $$"""
            <Connections KdfIterations="1000" Protected="{{EncryptedValue}}">
              <Node Type="Connection" Name="Admin RDP" Hostname="10.0.0.4" Protocol="RDP" Port="3389"
                    UseConsoleSession="true" RDGatewayHostname="gateway.example"
                    RedirectClipboard="false" RedirectPrinters="true" RedirectDiskDrives="All" />
            </Connections>
            """;

        using var result = new MRemoteNgXmlImporter().Import(
            xml,
            "Password",
            new VaultId(Guid.NewGuid()));

        var connection = Assert.Single(result.Connections);
        var settings = RdpConnectionSettings.FromProtocolSettings(connection.ProtocolSettings);
        Assert.True(settings.ConnectAsAdministrator);
        Assert.Equal("gateway.example", settings.GatewayHost);
        Assert.False(settings.RedirectClipboard);
        Assert.True(settings.RedirectPrinters);
        Assert.True(settings.RedirectDrives);
    }
}
