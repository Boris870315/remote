using Remote.Application.Connections;
using Remote.Infrastructure.Processes;

namespace Remote.Infrastructure.Protocols.Rdp;

/// <summary>Builds Microsoft's documented macOS rdp URI without placing a password in it.</summary>
public sealed class MacOsRdpLaunchSpecFactory
{
    public ExternalLaunchSpec Create(RdpExternalLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RdpExternalLaunchGuard.EnsureInteractive(request);
        request.Settings.Validate();

        var attributes = new List<string>
        {
            $"full%20address=s:{Uri.EscapeDataString(RdpEndpoint.FormatAuthority(request.Endpoint))}",
            "prompt%20for%20credentials%20on%20client=i:1",
            $"use%20multimon=i:{(request.Display.MonitorSelection is MonitorSelection.All ? 1 : 0)}",
            $"screen%20mode%20id=i:{(request.StartFullScreen ? 2 : 1)}",
            $"audiomode=i:{(int)request.Settings.AudioMode}",
            $"redirectprinters=i:{(request.Settings.RedirectPrinters ? 1 : 0)}",
        };

        if (request.Settings.RedirectDrives)
        {
            attributes.Add("drivestoredirect=s:*");
        }

        if (!string.IsNullOrWhiteSpace(request.Settings.GatewayHost))
        {
            attributes.Add($"gatewayhostname=s:{Uri.EscapeDataString(request.Settings.GatewayHost)}");
            attributes.Add("gatewayusagemethod=i:1");
        }

        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            attributes.Add($"username=s:{Uri.EscapeDataString(request.Username)}");
        }

        return new($"rdp://{string.Join('&', attributes)}", [], true);
    }
}
