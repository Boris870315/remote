namespace Remote.Protocols;

/// <summary>Describes a protocol adapter without coupling it to a UI framework.</summary>
public interface IRemoteProtocol
{
    ProtocolDescriptor Descriptor { get; }

    bool CanHandle(Uri endpoint);
}

/// <summary>The access level requested for a remote session.</summary>
public enum SessionAccessMode
{
    Interactive,
    ViewOnly,
}

/// <summary>Capabilities implemented by a protocol adapter.</summary>
[Flags]
public enum ProtocolCapabilities
{
    None = 0,
    ViewOnly = 1 << 0,
    DynamicResolution = 1 << 1,
    SingleMonitor = 1 << 2,
    AllMonitors = 1 << 3,
    GraphicalSession = 1 << 4,
}

/// <summary>Stable metadata used to discover and present a protocol adapter.</summary>
public sealed record ProtocolDescriptor(
    string Id,
    string DisplayName,
    ProtocolCapabilities Capabilities);
