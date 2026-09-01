using Remote.Application.Connections;
using Remote.Infrastructure.Processes;
using Remote.Infrastructure.Protocols.Rdp;
using Remote.Protocols;

namespace Remote.Application.Tests;

public sealed class RdpExternalLaunchSpecTests
{
    [Fact]
    public void Windows_WithAllMonitors_BuildsPublicPromptedMstscLaunch()
    {
        var request = CreateRequest() with
        {
            StartFullScreen = true,
            Display = new DisplayPreferences { MonitorSelection = MonitorSelection.All },
        };

        var specification = new WindowsRdpLaunchSpecFactory().Create(request);

        Assert.Equal("mstsc.exe", specification.FileName);
        Assert.False(specification.UseShellExecute);
        Assert.Contains("/v:server.example:3390", specification.Arguments);
        Assert.Contains("/public", specification.Arguments);
        Assert.Contains("/prompt", specification.Arguments);
        Assert.Contains("/multimon", specification.Arguments);
        Assert.Contains("/f", specification.Arguments);
    }

    [Fact]
    public void MacOs_BuildsDocumentedRdpUriWithoutPassword()
    {
        var request = CreateRequest() with { Username = "CORP\\operator" };

        var specification = new MacOsRdpLaunchSpecFactory().Create(request);

        Assert.True(specification.UseShellExecute);
        Assert.StartsWith("rdp://", specification.FileName, StringComparison.Ordinal);
        Assert.Contains("full%20address=s:server.example%3A3390", specification.FileName, StringComparison.Ordinal);
        Assert.Contains("username=s:CORP%5Coperator", specification.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("password", specification.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RdpHostPlatform.Windows)]
    [InlineData(RdpHostPlatform.MacOs)]
    public async Task ExternalFallback_WhenViewOnlyRequested_RefusesInteractiveDowngrade(
        RdpHostPlatform platform)
    {
        var processLauncher = new RecordingProcessLauncher();
        var launcher = new RdpExternalSessionLauncher(
            processLauncher,
            new WindowsRdpLaunchSpecFactory(),
            new MacOsRdpLaunchSpecFactory(),
            () => platform);
        var request = CreateRequest() with { AccessMode = SessionAccessMode.ViewOnly };

        await Assert.ThrowsAsync<NotSupportedException>(() => launcher.LaunchAsync(request));
        Assert.Null(processLauncher.Specification);
    }

    [Fact]
    public async Task Launcher_UsesSelectedPlatformFactory()
    {
        var processLauncher = new RecordingProcessLauncher();
        var launcher = new RdpExternalSessionLauncher(
            processLauncher,
            new WindowsRdpLaunchSpecFactory(),
            new MacOsRdpLaunchSpecFactory(),
            () => RdpHostPlatform.Windows);

        await launcher.LaunchAsync(CreateRequest());

        Assert.Equal("mstsc.exe", processLauncher.Specification?.FileName);
    }

    private static RdpExternalLaunchRequest CreateRequest() => new()
    {
        Endpoint = new Uri("rdp://server.example:3390"),
    };

    private sealed class RecordingProcessLauncher : IProcessLauncher
    {
        public ExternalLaunchSpec? Specification { get; private set; }

        public Task<LaunchedProcess> LaunchAsync(
            ExternalLaunchSpec specification,
            CancellationToken cancellationToken = default)
        {
            Specification = specification;
            return Task.FromResult(new LaunchedProcess(42));
        }
    }
}
