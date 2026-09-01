using Remote.Application.Connections;
using Remote.Infrastructure.Processes;

namespace Remote.Infrastructure.Protocols.Rdp;

/// <summary>Builds a shell-free mstsc launch using only non-secret arguments.</summary>
public sealed class WindowsRdpLaunchSpecFactory
{
    public ExternalLaunchSpec Create(RdpExternalLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RdpExternalLaunchGuard.EnsureInteractive(request);
        request.Settings.Validate();

        var arguments = new List<string>
        {
            $"/v:{RdpEndpoint.FormatAuthority(request.Endpoint)}",
            "/public",
            "/prompt",
        };

        if (request.StartFullScreen)
        {
            arguments.Add("/f");
        }

        if (request.Display.MonitorSelection is MonitorSelection.All)
        {
            arguments.Add("/multimon");
        }

        if (!string.IsNullOrWhiteSpace(request.Settings.GatewayHost))
        {
            arguments.Add($"/g:{request.Settings.GatewayHost}");
        }

        if (request.Settings.ConnectAsAdministrator)
        {
            arguments.Add("/admin");
        }

        if (request.Settings.UseRemoteGuard)
        {
            arguments.Add("/remoteGuard");
        }

        return new("mstsc.exe", arguments, false);
    }
}
