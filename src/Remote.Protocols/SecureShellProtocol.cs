namespace Remote.Protocols;

public sealed class SecureShellProtocol : IRemoteProtocol
{
    public string Name => "SSH";

    public bool CanHandle(Uri endpoint) =>
        string.Equals(endpoint.Scheme, "ssh", StringComparison.OrdinalIgnoreCase);
}
