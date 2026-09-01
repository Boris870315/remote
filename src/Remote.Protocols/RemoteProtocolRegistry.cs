namespace Remote.Protocols;

/// <summary>Discovers adapters through registration instead of central protocol switches.</summary>
public sealed class RemoteProtocolRegistry
{
    private readonly IReadOnlyList<IRemoteProtocol> _protocols;

    public RemoteProtocolRegistry(IEnumerable<IRemoteProtocol> protocols)
    {
        ArgumentNullException.ThrowIfNull(protocols);
        _protocols = protocols.ToArray();

        var duplicate = _protocols
            .GroupBy(protocol => protocol.Descriptor.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new ArgumentException($"Protocol id '{duplicate.Key}' is registered more than once.", nameof(protocols));
        }
    }

    public IReadOnlyList<IRemoteProtocol> Protocols => _protocols;

    public IRemoteProtocol Resolve(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var matches = _protocols.Where(protocol => protocol.CanHandle(endpoint)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new NotSupportedException($"No protocol adapter can handle '{endpoint.Scheme}'."),
            _ => throw new InvalidOperationException($"More than one protocol adapter can handle '{endpoint}'."),
        };
    }
}
