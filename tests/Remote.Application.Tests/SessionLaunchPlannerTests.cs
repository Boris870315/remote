using Remote.Application.Connections;
using Remote.Application.Sessions;
using Remote.Protocols;

namespace Remote.Application.Tests;

public sealed class SessionLaunchPlannerTests
{
    [Fact]
    public void Plan_RdpViewOnlyAcrossAllMonitors_ReturnsValidatedPlan()
    {
        var planner = CreatePlanner();
        var connection = new ConnectionProfile
        {
            Id = ConnectionId.New(),
            Name = "Windows Prod",
            Endpoint = new Uri("rdp://10.20.0.24"),
            ProtocolId = "rdp",
            DefaultAccessMode = SessionAccessMode.ViewOnly,
            Display = new DisplayPreferences { MonitorSelection = MonitorSelection.All },
        };

        var plan = planner.Plan(connection);

        Assert.Equal(SessionAccessMode.ViewOnly, plan.AccessMode);
        Assert.Equal("rdp", plan.Protocol.Id);
    }

    [Fact]
    public void Plan_WhenAdapterCannotSelectRequestedMonitors_RejectsRequest()
    {
        var planner = CreatePlanner();
        var connection = new ConnectionProfile
        {
            Id = ConnectionId.New(),
            Name = "Limited Display",
            Endpoint = new Uri("limited://10.20.0.31"),
            ProtocolId = "limited",
            Display = new DisplayPreferences { MonitorSelection = MonitorSelection.All },
        };

        var exception = Assert.Throws<InvalidOperationException>(() => planner.Plan(connection));

        Assert.Contains("monitor selection", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SessionLaunchPlanner CreatePlanner() => new(
        new RemoteProtocolRegistry(
        [
            new RemoteDesktopProtocol(),
            new VirtualNetworkComputingProtocol(),
            new SecureShellProtocol(),
            new LimitedDisplayProtocol(),
        ]),
        new SessionLaunchPolicy());

    private sealed class LimitedDisplayProtocol : IRemoteProtocol
    {
        public ProtocolDescriptor Descriptor { get; } = new(
            "limited",
            "Limited Display",
            ProtocolCapabilities.ViewOnly |
            ProtocolCapabilities.SingleMonitor |
            ProtocolCapabilities.GraphicalSession);

        public bool CanHandle(Uri endpoint) => endpoint.Scheme == "limited";
    }
}
