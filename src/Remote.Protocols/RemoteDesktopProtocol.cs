namespace Remote.Protocols;

/// <summary>Metadata for the first-party RDP adapter.</summary>
public sealed class RemoteDesktopProtocol : IRemoteProtocol
{
    public ProtocolDescriptor Descriptor { get; } = new(
        "rdp",
        "RDP",
        ProtocolCapabilities.ViewOnly |
        ProtocolCapabilities.DynamicResolution |
        ProtocolCapabilities.SingleMonitor |
        ProtocolCapabilities.AllMonitors |
        ProtocolCapabilities.GraphicalSession);

    public bool CanHandle(Uri endpoint) =>
        string.Equals(endpoint.Scheme, "rdp", StringComparison.OrdinalIgnoreCase);
}
