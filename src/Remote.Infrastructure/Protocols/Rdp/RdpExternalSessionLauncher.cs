using Remote.Infrastructure.Processes;

namespace Remote.Infrastructure.Protocols.Rdp;

/// <summary>Launches the supported platform-native RDP fallback.</summary>
public sealed class RdpExternalSessionLauncher(
    IProcessLauncher processLauncher,
    WindowsRdpLaunchSpecFactory windowsFactory,
    MacOsRdpLaunchSpecFactory macOsFactory,
    Func<RdpHostPlatform>? platformProvider = null)
{
    private readonly Func<RdpHostPlatform> _platformProvider = platformProvider ?? DetectPlatform;

    public Task<LaunchedProcess> LaunchAsync(
        RdpExternalLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        var specification = _platformProvider() switch
        {
            RdpHostPlatform.Windows => windowsFactory.Create(request),
            RdpHostPlatform.MacOs => macOsFactory.Create(request),
            _ => throw new PlatformNotSupportedException("The native RDP fallback supports Windows and macOS only."),
        };
        return processLauncher.LaunchAsync(specification, cancellationToken);
    }

    private static RdpHostPlatform DetectPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return RdpHostPlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return RdpHostPlatform.MacOs;
        }

        return RdpHostPlatform.Unsupported;
    }
}

public enum RdpHostPlatform
{
    Unsupported,
    Windows,
    MacOs,
}
