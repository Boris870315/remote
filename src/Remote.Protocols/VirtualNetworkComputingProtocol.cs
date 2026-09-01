namespace Remote.Protocols;

/// <summary>Metadata for the first-party VNC/RFB adapter.</summary>
public sealed class VirtualNetworkComputingProtocol : IRemoteProtocol
{
    public ProtocolDescriptor Descriptor { get; } = new(
        "vnc",
        "VNC",
        ProtocolCapabilities.ViewOnly |
        ProtocolCapabilities.SingleMonitor |
        ProtocolCapabilities.AllMonitors |
        ProtocolCapabilities.GraphicalSession);

    public bool CanHandle(Uri endpoint) =>
        string.Equals(endpoint.Scheme, "vnc", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(endpoint.Scheme, "rfb", StringComparison.OrdinalIgnoreCase);
}
