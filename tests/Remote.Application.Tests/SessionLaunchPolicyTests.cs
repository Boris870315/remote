using Remote.Protocols;

namespace Remote.Application.Tests;

public sealed class SessionLaunchPolicyTests
{
    [Fact]
    public void Validate_WhenViewOnlyIsSupported_AllowsLaunch()
    {
        var policy = new SessionLaunchPolicy();

        var result = policy.Validate(new RemoteDesktopProtocol(), SessionAccessMode.ViewOnly);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_WhenViewOnlyCannotBeEnforced_BlocksLaunch()
    {
        var policy = new SessionLaunchPolicy();

        var result = policy.Validate(new InteractiveOnlyProtocol(), SessionAccessMode.ViewOnly);

        Assert.False(result.IsAllowed);
        Assert.Contains("View Only", result.Detail, StringComparison.Ordinal);
    }

    private sealed class InteractiveOnlyProtocol : IRemoteProtocol
    {
        public ProtocolDescriptor Descriptor { get; } = new(
            "test-interactive",
            "Test Interactive",
            ProtocolCapabilities.None);

        public bool CanHandle(Uri endpoint) => true;
    }
}
