using Remote.Protocols;

namespace Remote.Infrastructure.Protocols.Rdp;

internal static class RdpExternalLaunchGuard
{
    public static void EnsureInteractive(RdpExternalLaunchRequest request)
    {
        if (request.AccessMode is SessionAccessMode.ViewOnly)
        {
            throw new NotSupportedException(
                "The platform-native RDP fallback cannot reliably enforce View Only. Use an embedded host that implements input suppression.");
        }
    }
}

internal static class RdpEndpoint
{
    public static string FormatAuthority(Uri endpoint)
    {
        if (!string.Equals(endpoint.Scheme, "rdp", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An rdp endpoint is required.", nameof(endpoint));
        }

        var host = endpoint.HostNameType is UriHostNameType.IPv6
            ? $"[{endpoint.Host}]"
            : endpoint.Host;
        var port = endpoint.IsDefaultPort ? 3389 : endpoint.Port;
        return $"{host}:{port}";
    }
}
