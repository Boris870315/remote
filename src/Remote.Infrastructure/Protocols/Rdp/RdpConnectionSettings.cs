using Remote.Application.Connections;

namespace Remote.Infrastructure.Protocols.Rdp;

/// <summary>Validated RDP settings shared by native and future embedded hosts.</summary>
public sealed record RdpConnectionSettings
{
    public string? GatewayHost { get; init; }

    public bool ConnectAsAdministrator { get; init; }

    public bool UseRemoteGuard { get; init; }

    public bool RedirectClipboard { get; init; } = true;

    public bool RedirectPrinters { get; init; }

    public bool RedirectDrives { get; init; }

    public bool RedirectMicrophone { get; init; }

    public bool RedirectCamera { get; init; }

    public RdpAudioMode AudioMode { get; init; } = RdpAudioMode.PlayLocally;

    public RdpCertificatePolicy CertificatePolicy { get; init; } = RdpCertificatePolicy.PromptOnUntrusted;

    public void Validate()
    {
        if (GatewayHost is not null &&
            (!Uri.CheckHostName(GatewayHost).Equals(UriHostNameType.Dns) &&
             !Uri.CheckHostName(GatewayHost).Equals(UriHostNameType.IPv4) &&
             !Uri.CheckHostName(GatewayHost).Equals(UriHostNameType.IPv6)))
        {
            throw new ArgumentException("RDP Gateway must be a valid host name or IP address.", nameof(GatewayHost));
        }
    }

    public ProtocolSettings ToProtocolSettings()
    {
        Validate();
        var settings = new ProtocolSettings()
            .Set(Keys.ConnectAsAdministrator, ConnectAsAdministrator.ToString())
            .Set(Keys.UseRemoteGuard, UseRemoteGuard.ToString())
            .Set(Keys.RedirectClipboard, RedirectClipboard.ToString())
            .Set(Keys.RedirectPrinters, RedirectPrinters.ToString())
            .Set(Keys.RedirectDrives, RedirectDrives.ToString())
            .Set(Keys.RedirectMicrophone, RedirectMicrophone.ToString())
            .Set(Keys.RedirectCamera, RedirectCamera.ToString())
            .Set(Keys.AudioMode, ((int)AudioMode).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Set(Keys.CertificatePolicy, CertificatePolicy.ToString());
        return GatewayHost is null ? settings : settings.Set(Keys.GatewayHost, GatewayHost);
    }

    public static RdpConnectionSettings FromProtocolSettings(ProtocolSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new RdpConnectionSettings
        {
            GatewayHost = settings.Get(Keys.GatewayHost),
            ConnectAsAdministrator = settings.GetBoolean(Keys.ConnectAsAdministrator),
            UseRemoteGuard = settings.GetBoolean(Keys.UseRemoteGuard),
            RedirectClipboard = settings.GetBoolean(Keys.RedirectClipboard, true),
            RedirectPrinters = settings.GetBoolean(Keys.RedirectPrinters),
            RedirectDrives = settings.GetBoolean(Keys.RedirectDrives),
            RedirectMicrophone = settings.GetBoolean(Keys.RedirectMicrophone),
            RedirectCamera = settings.GetBoolean(Keys.RedirectCamera),
            AudioMode = (RdpAudioMode)settings.GetInteger(Keys.AudioMode, (int)RdpAudioMode.PlayLocally),
            CertificatePolicy = Enum.TryParse<RdpCertificatePolicy>(settings.Get(Keys.CertificatePolicy), out var policy)
                ? policy
                : RdpCertificatePolicy.PromptOnUntrusted,
        };
    }

    private static class Keys
    {
        public const string GatewayHost = "rdp.gateway.host";
        public const string ConnectAsAdministrator = "rdp.admin";
        public const string UseRemoteGuard = "rdp.remote-guard";
        public const string RedirectClipboard = "rdp.redirect.clipboard";
        public const string RedirectPrinters = "rdp.redirect.printers";
        public const string RedirectDrives = "rdp.redirect.drives";
        public const string RedirectMicrophone = "rdp.redirect.microphone";
        public const string RedirectCamera = "rdp.redirect.camera";
        public const string AudioMode = "rdp.audio.mode";
        public const string CertificatePolicy = "rdp.certificate.policy";
    }
}

public enum RdpAudioMode
{
    PlayLocally = 0,
    PlayRemotely = 1,
    DoNotPlay = 2,
}

public enum RdpCertificatePolicy
{
    RequireTrusted,
    PromptOnUntrusted,
}
