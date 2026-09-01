namespace Remote.Protocols;

public sealed class SecureShellProtocol : IRemoteProtocol
{
    public ProtocolDescriptor Descriptor { get; } = new(
        "ssh2",
        "SSH2",
        ProtocolCapabilities.ViewOnly);

    public bool CanHandle(Uri endpoint) =>
        string.Equals(endpoint.Scheme, "ssh", StringComparison.OrdinalIgnoreCase);
}
