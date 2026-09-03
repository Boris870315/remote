using Remote.Infrastructure.Protocols.Rdp;

namespace Remote.Application.Tests;

public sealed class RdpConnectionSettingsTests
{
    [Fact]
    public void ProtocolSettings_RoundTripsNonSecretRdpOptions()
    {
        var expected = new RdpConnectionSettings
        {
            GatewayHost = "gateway.example",
            ConnectAsAdministrator = true,
            UseRemoteGuard = true,
            RedirectClipboard = false,
            RedirectPrinters = true,
            RedirectDrives = true,
            RedirectMicrophone = true,
            RedirectCamera = true,
            AudioMode = RdpAudioMode.DoNotPlay,
            CertificatePolicy = RdpCertificatePolicy.PromptOnUntrusted,
        };

        var restored = RdpConnectionSettings.FromProtocolSettings(expected.ToProtocolSettings());

        Assert.Equal(expected, restored);
    }

    [Fact]
    public void Validate_WhenGatewayIsInvalid_RejectsSettings()
    {
        var settings = new RdpConnectionSettings { GatewayHost = "not a host/with/path" };

        Assert.Throws<ArgumentException>(() => settings.Validate());
    }

    [Theory]
    [InlineData("operator", "CONTOSO", "CONTOSO\\operator")]
    [InlineData("CONTOSO\\operator", "CONTOSO", "CONTOSO\\operator")]
    [InlineData("operator@example.com", "CONTOSO", "operator@example.com")]
    [InlineData(" operator ", " ", "operator")]
    public void RdpLoginName_DoesNotDuplicateAnExistingDomain(
        string username,
        string domain,
        string expected)
    {
        Assert.Equal(expected, RdpLoginName.Format(username, domain));
    }
}
