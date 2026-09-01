using Remote.Protocols;

namespace Remote.Application.Tests;

public sealed class RemoteProtocolRegistryTests
{
    [Theory]
    [InlineData("rdp://host", typeof(RemoteDesktopProtocol))]
    [InlineData("vnc://host", typeof(VirtualNetworkComputingProtocol))]
    [InlineData("rfb://host", typeof(VirtualNetworkComputingProtocol))]
    [InlineData("ssh://host", typeof(SecureShellProtocol))]
    public void Resolve_WhenAdapterMatches_ReturnsRegisteredAdapter(string endpoint, Type expectedType)
    {
        var registry = CreateRegistry();

        var protocol = registry.Resolve(new Uri(endpoint));

        Assert.IsType(expectedType, protocol);
    }

    [Fact]
    public void RegisteredFirstPartyAdapters_AllImplementViewOnly()
    {
        var registry = CreateRegistry();

        Assert.All(
            registry.Protocols,
            protocol => Assert.True(
                protocol.Descriptor.Capabilities.HasFlag(ProtocolCapabilities.ViewOnly),
                $"{protocol.Descriptor.DisplayName} must implement View Only."));
    }

    private static RemoteProtocolRegistry CreateRegistry() => new(
        [
            new RemoteDesktopProtocol(),
            new VirtualNetworkComputingProtocol(),
            new SecureShellProtocol(),
        ]);
}
