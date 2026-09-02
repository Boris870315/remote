using Remote.Infrastructure.Processes;

namespace Remote.Infrastructure.Protocols.Rdp;

/// <summary>Launches the supported platform-native RDP fallback.</summary>
public sealed class RdpExternalSessionLauncher(
    IProcessLauncher processLauncher,
    WindowsRdpLaunchSpecFactory windowsFactory,
    MacOsRdpLaunchSpecFactory macOsFactory,
    Func<RdpHostPlatform>? platformProvider = null,
    IRdpCredentialStore? credentialStore = null)
{
    private readonly Func<RdpHostPlatform> _platformProvider = platformProvider ?? DetectPlatform;
    private readonly IRdpCredentialStore _credentialStore = credentialStore ?? new NullRdpCredentialStore();

    public async Task<LaunchedProcess> LaunchAsync(
        RdpExternalLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        var platform = _platformProvider();
        if (platform is RdpHostPlatform.Windows &&
            !string.IsNullOrWhiteSpace(request.Username) &&
            !request.PasswordUtf8.IsEmpty)
        {
            await _credentialStore.StoreAsync(
                request.Endpoint,
                request.Username,
                request.PasswordUtf8,
                cancellationToken).ConfigureAwait(false);
        }

        var specification = platform switch
        {
            RdpHostPlatform.Windows => windowsFactory.Create(request),
            RdpHostPlatform.MacOs => macOsFactory.Create(request),
            _ => throw new PlatformNotSupportedException("The native RDP fallback supports Windows and macOS only."),
        };
        return await processLauncher.LaunchAsync(specification, cancellationToken).ConfigureAwait(false);
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
