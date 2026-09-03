using Remote.Protocols;

namespace Remote.Infrastructure.Protocols.Rdp;

public static class RdpSessionPermissionPolicy
{
    public static RdpEffectiveRedirections Resolve(SessionAccessMode accessMode, RdpConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var interactive = accessMode is SessionAccessMode.Interactive;
        return new RdpEffectiveRedirections(
            interactive,
            interactive && settings.RedirectClipboard,
            interactive && settings.RedirectPrinters,
            interactive && settings.RedirectDrives);
    }
}

public sealed record RdpEffectiveRedirections(
    bool AcceptsInput,
    bool RedirectClipboard,
    bool RedirectPrinters,
    bool RedirectDrives);
