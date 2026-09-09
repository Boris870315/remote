using Remote.Application.Connections;
using Remote.Infrastructure.Processes;

namespace Remote.Infrastructure.Protocols.Rdp;

/// <summary>Builds a non-secret .rdp document for Microsoft Remote Desktop on macOS.</summary>
public sealed class MacOsRdpLaunchSpecFactory(Func<string>? temporaryPathFactory = null)
{
    private readonly Func<string> _temporaryPathFactory = temporaryPathFactory ?? (() =>
        Path.Combine(Path.GetTempPath(), $"remote-{Guid.NewGuid():N}.rdp"));

    public ExternalLaunchSpec Create(RdpExternalLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RdpExternalLaunchGuard.EnsureInteractive(request);
        request.Settings.Validate();

        var attributes = new List<string>
        {
            $"full address:s:{RdpEndpoint.FormatAuthority(request.Endpoint)}",
            "prompt for credentials on client:i:1",
            $"use multimon:i:{(request.Display.MonitorSelection is MonitorSelection.All ? 1 : 0)}",
            $"screen mode id:i:{(request.StartFullScreen ? 2 : 1)}",
            $"audiomode:i:{(int)request.Settings.AudioMode}",
            $"redirectclipboard:i:{(request.Settings.RedirectClipboard ? 1 : 0)}",
            $"redirectprinters:i:{(request.Settings.RedirectPrinters ? 1 : 0)}",
        };

        if (request.Settings.RedirectDrives)
        {
            attributes.Add("drivestoredirect:s:*");
        }

        if (!string.IsNullOrWhiteSpace(request.Settings.GatewayHost))
        {
            attributes.Add($"gatewayhostname:s:{request.Settings.GatewayHost}");
            attributes.Add("gatewayusagemethod:i:1");
        }

        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            attributes.Add($"username:s:{request.Username}");
        }

        if (!string.IsNullOrEmpty(request.Domain)) attributes.Add($"domain:s:{request.Domain}");

        var path = _temporaryPathFactory();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Path.GetTempPath());
        File.WriteAllLines(path, attributes, System.Text.Encoding.Unicode);
        return new("/usr/bin/open", ["-a", "Microsoft Remote Desktop", path], false);
    }
}
