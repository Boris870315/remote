using Remote.Protocols;

namespace Remote.Application;

/// <summary>Validates protocol-independent rules before a session starts.</summary>
public sealed class SessionLaunchPolicy
{
    public SessionLaunchResult Validate(
        IRemoteProtocol protocol,
        SessionAccessMode accessMode)
    {
        ArgumentNullException.ThrowIfNull(protocol);

        if (accessMode is SessionAccessMode.ViewOnly &&
            !protocol.Descriptor.Capabilities.HasFlag(ProtocolCapabilities.ViewOnly))
        {
            return new(false, $"{protocol.Descriptor.DisplayName} cannot reliably enforce View Only.");
        }

        return new(true, "Ready");
    }
}

public sealed record SessionLaunchResult(bool IsAllowed, string Detail);
