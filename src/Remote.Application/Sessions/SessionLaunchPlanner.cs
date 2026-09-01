using Remote.Application.Connections;
using Remote.Protocols;

namespace Remote.Application.Sessions;

/// <summary>Builds a validated launch plan from a saved Connection.</summary>
public sealed class SessionLaunchPlanner(
    RemoteProtocolRegistry registry,
    SessionLaunchPolicy accessPolicy)
{
    public SessionLaunchPlan Plan(ConnectionProfile connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var protocol = registry.Resolve(connection.Endpoint);
        if (!string.Equals(protocol.Descriptor.Id, connection.ProtocolId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Connection protocol '{connection.ProtocolId}' does not match endpoint '{connection.Endpoint.Scheme}'.");
        }

        var accessResult = accessPolicy.Validate(protocol, connection.DefaultAccessMode);
        if (!accessResult.IsAllowed)
        {
            throw new InvalidOperationException(accessResult.Detail);
        }

        ValidateMonitorSelection(connection.Display, protocol.Descriptor);

        return new(connection, protocol.Descriptor, connection.DefaultAccessMode);
    }

    private static void ValidateMonitorSelection(
        DisplayPreferences display,
        ProtocolDescriptor protocol)
    {
        if (!protocol.Capabilities.HasFlag(ProtocolCapabilities.GraphicalSession))
        {
            return;
        }

        var requiredCapability = display.MonitorSelection is MonitorSelection.All
            ? ProtocolCapabilities.AllMonitors
            : ProtocolCapabilities.SingleMonitor;

        if (!protocol.Capabilities.HasFlag(requiredCapability))
        {
            throw new InvalidOperationException(
                $"{protocol.DisplayName} does not support the requested monitor selection.");
        }
    }
}

public sealed record SessionLaunchPlan(
    ConnectionProfile Connection,
    ProtocolDescriptor Protocol,
    SessionAccessMode AccessMode);
