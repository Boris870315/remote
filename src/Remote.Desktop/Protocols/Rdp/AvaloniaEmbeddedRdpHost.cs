using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Remote.Infrastructure.Protocols.Rdp;

namespace Remote.Desktop.Protocols.Rdp;

/// <summary>Hosts Microsoft's RDP ActiveX client as a real child of the Avalonia session workspace.</summary>
public sealed class AvaloniaEmbeddedRdpHost : NativeControlHost
{
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipSiblings = 0x04000000;
    private nint _window;
    private object? _rdpClient;
    private readonly TaskCompletionSource _hostReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task ConnectAsync(RdpExternalLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Embedded Microsoft RDP is available on Windows only.");
        }
        await _hostReady.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var clientObject = _rdpClient
            ?? throw new InvalidOperationException("The embedded RDP surface did not initialize correctly.");
        dynamic client = clientObject;
        try
        {
            try { client.Disconnect(); } catch (COMException) { }
            client.Server = request.Endpoint.Host;
            client.UserName = ParseUsername(request.Username).Username;
            var domain = ParseUsername(request.Username).Domain;
            if (!string.IsNullOrWhiteSpace(domain))
            {
                client.Domain = domain;
            }
            client.DesktopWidth = Math.Max(640, (int)Bounds.Width);
            client.DesktopHeight = Math.Max(480, (int)Bounds.Height);
            dynamic advanced = client.AdvancedSettings9;
            advanced.RDPPort = request.Endpoint.IsDefaultPort ? 3389 : request.Endpoint.Port;
            advanced.EnableCredSspSupport = true;
            advanced.SmartSizing = true;
            advanced.ConnectToServerConsole = request.Settings.ConnectAsAdministrator;
            advanced.RedirectClipboard = request.Settings.RedirectClipboard;
            advanced.RedirectPrinters = request.Settings.RedirectPrinters;
            advanced.RedirectDrives = request.Settings.RedirectDrives;
            advanced.AudioRedirectionMode = (uint)request.Settings.AudioMode;
            if (!string.IsNullOrWhiteSpace(request.Settings.GatewayHost))
            {
                dynamic transport = client.TransportSettings4;
                transport.GatewayHostname = request.Settings.GatewayHost;
                transport.GatewayUsageMethod = 1u;
                transport.GatewayProfileUsageMethod = 1u;
            }
            if (request.PasswordUtf8 is { Length: > 0 })
            {
                advanced.ClearTextPassword = System.Text.Encoding.UTF8.GetString(request.PasswordUtf8.Span);
            }
            client.Connect();
            EnableWindow(_window, request.AccessMode is not Remote.Protocols.SessionAccessMode.ViewOnly);
            return;
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException($"內嵌 RDP 啟動失敗：{exception.Message}", exception);
        }
    }

    public Task DisconnectAsync()
    {
        if (_rdpClient is not null)
        {
            try { ((dynamic)_rdpClient).Disconnect(); } catch (COMException) { }
        }
        return Task.CompletedTask;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return base.CreateNativeControlCore(parent);
        }

        _ = OleInitialize(nint.Zero);
        if (!AtlAxWinInit())
        {
            throw new InvalidOperationException("Windows ATL ActiveX host initialization failed.");
        }

        _window = CreateWindowExW(
            0,
            "AtlAxWin",
            "MsRdpClient11NotSafeForScripting",
            WsChild | WsVisible | WsClipSiblings,
            0,
            0,
            1,
            1,
            parent.Handle,
            nint.Zero,
            nint.Zero,
            nint.Zero);
        if (_window == nint.Zero)
        {
            throw new InvalidOperationException($"Windows could not create the embedded RDP host ({Marshal.GetLastWin32Error()}).");
        }

        var result = AtlAxGetControl(_window, out var unknown);
        if (result < 0 || unknown == nint.Zero)
        {
            DestroyWindow(_window);
            _window = nint.Zero;
            Marshal.ThrowExceptionForHR(result);
        }

        try
        {
            _rdpClient = Marshal.GetObjectForIUnknown(unknown);
            _hostReady.TrySetResult();
        }
        finally
        {
            Marshal.Release(unknown);
        }
        return new PlatformHandle(_window, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (OperatingSystem.IsWindows() && _window != nint.Zero)
        {
            if (_rdpClient is not null)
            {
                try { ((dynamic)_rdpClient).Disconnect(); } catch (COMException) { }
                if (Marshal.IsComObject(_rdpClient))
                {
                    Marshal.FinalReleaseComObject(_rdpClient);
                }
                _rdpClient = null;
            }
            DestroyWindow(_window);
            _window = nint.Zero;
            OleUninitialize();
            return;
        }
        base.DestroyNativeControlCore(control);
    }

    private static (string? Domain, string? Username) ParseUsername(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }
        var separator = value.IndexOf('\\');
        return separator > 0
            ? (value[..separator], value[(separator + 1)..])
            : (null, value);
    }

    [DllImport("atl.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AtlAxWinInit();

    [DllImport("atl.dll", ExactSpelling = true)]
    private static extern int AtlAxGetControl(nint window, out nint unknown);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(nint reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();
}
