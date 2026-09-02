using Remote.Application.Connections;
using Remote.Infrastructure.Processes;
using Remote.Infrastructure.Protocols.Rdp;
using Remote.Protocols;

namespace Remote.Application.Tests;

public sealed class RdpExternalLaunchSpecTests
{
    [Fact]
    public void Windows_WithAllMonitors_BuildsPromptedMstscLaunchWhenNoIdentityCardExists()
    {
        var request = CreateRequest() with
        {
            StartFullScreen = true,
            Display = new DisplayPreferences { MonitorSelection = MonitorSelection.All },
            Settings = new RdpConnectionSettings
            {
                GatewayHost = "gateway.example",
                ConnectAsAdministrator = true,
                UseRemoteGuard = true,
            },
        };

        var specification = new WindowsRdpLaunchSpecFactory().Create(request);

        Assert.Equal("mstsc.exe", specification.FileName);
        Assert.False(specification.UseShellExecute);
        Assert.Contains("/v:server.example:3390", specification.Arguments);
        Assert.Contains("/prompt", specification.Arguments);
        Assert.Contains("/multimon", specification.Arguments);
        Assert.Contains("/f", specification.Arguments);
        Assert.Contains("/g:gateway.example", specification.Arguments);
        Assert.Contains("/admin", specification.Arguments);
        Assert.Contains("/remoteGuard", specification.Arguments);
    }

    [Fact]
    public void Windows_WithIdentityCard_DoesNotPromptOrExposeSecretInArguments()
    {
        var request = CreateRequest() with
        {
            Username = "CORP\\operator",
            PasswordUtf8 = "top-secret"u8.ToArray(),
        };

        var specification = new WindowsRdpLaunchSpecFactory().Create(request);

        Assert.DoesNotContain("/prompt", specification.Arguments);
        Assert.DoesNotContain("/public", specification.Arguments);
        Assert.DoesNotContain(specification.Arguments, value => value.Contains("top-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Launcher_ProvisionsWindowsCredentialBeforeStartingClient()
    {
        var processLauncher = new RecordingProcessLauncher();
        var credentialStore = new RecordingCredentialStore();
        var launcher = new RdpExternalSessionLauncher(
            processLauncher,
            new WindowsRdpLaunchSpecFactory(),
            new MacOsRdpLaunchSpecFactory(),
            () => RdpHostPlatform.Windows,
            credentialStore);
        var request = CreateRequest() with
        {
            Username = "CORP\\operator",
            PasswordUtf8 = "secret"u8.ToArray(),
        };

        await launcher.LaunchAsync(request);

        Assert.Equal("server.example", credentialStore.Endpoint?.Host);
        Assert.Equal("CORP\\operator", credentialStore.Username);
        Assert.Equal("secret", System.Text.Encoding.UTF8.GetString(credentialStore.Password));
        Assert.NotNull(processLauncher.Specification);
    }

    [Fact]
    public void MacOs_BuildsDocumentedRdpUriWithoutPassword()
    {
        var request = CreateRequest() with
        {
            Username = "CORP\\operator",
            Settings = new RdpConnectionSettings
            {
                GatewayHost = "gateway.example",
                RedirectPrinters = true,
                RedirectDrives = true,
                AudioMode = RdpAudioMode.DoNotPlay,
            },
        };

        var specification = new MacOsRdpLaunchSpecFactory().Create(request);

        Assert.True(specification.UseShellExecute);
        Assert.StartsWith("rdp://", specification.FileName, StringComparison.Ordinal);
        Assert.Contains("full%20address=s:server.example%3A3390", specification.FileName, StringComparison.Ordinal);
        Assert.Contains("username=s:CORP%5Coperator", specification.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("password", specification.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gatewayhostname=s:gateway.example", specification.FileName, StringComparison.Ordinal);
        Assert.Contains("redirectprinters=i:1", specification.FileName, StringComparison.Ordinal);
        Assert.Contains("drivestoredirect=s:*", specification.FileName, StringComparison.Ordinal);
        Assert.Contains("audiomode=i:2", specification.FileName, StringComparison.Ordinal);
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

    private sealed class RecordingCredentialStore : IRdpCredentialStore
    {
        public Uri? Endpoint { get; private set; }
        public string? Username { get; private set; }
        public byte[] Password { get; private set; } = [];

        public Task StoreAsync(
            Uri endpoint,
            string username,
            ReadOnlyMemory<byte> passwordUtf8,
            CancellationToken cancellationToken = default)
        {
            Endpoint = endpoint;
            Username = username;
            Password = passwordUtf8.ToArray();
            return Task.CompletedTask;
        }
    }
}
