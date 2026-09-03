using System.Runtime.InteropServices;
using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
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
    private DispatcherTimer? _connectionMonitor;
    private bool _hasConnected;
    private bool _disconnectRequested;
    private int _connectingTicks;
    private DispatcherTimer? _resizeTimer;
    private bool _useMultimon;

    public event Action<string>? UnexpectedlyDisconnected;

    public AvaloniaEmbeddedRdpHost()
    {
        PropertyChanged += (_, args) =>
        {
            if (args.Property == BoundsProperty) ScheduleDisplayResize();
        };
    }

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
        var stage = "initialize";
        try
        {
            _disconnectRequested = false;
            _hasConnected = false;
            _connectingTicks = 0;
            TryDisconnect(clientObject);
            stage = "set-endpoint";
            SetComProperty(clientObject, "Server", request.Endpoint.Host);
            SetComProperty(clientObject, "UserName", ParseUsername(request.Username).Username ?? string.Empty);
            var domain = ParseUsername(request.Username).Domain;
            if (!string.IsNullOrWhiteSpace(domain))
            {
                SetComProperty(clientObject, "Domain", domain);
            }
            stage = "set-display";
            SetComProperty(clientObject, "DesktopWidth", Math.Max(640, (int)Bounds.Width));
            SetComProperty(clientObject, "DesktopHeight", Math.Max(480, (int)Bounds.Height));
            _useMultimon = request.Display.MonitorSelection is Remote.Application.Connections.MonitorSelection.All;
            SetComProperty(clientObject, "UseMultimon", _useMultimon);
            SetComProperty(clientObject, "FullScreen", false);
            stage = "open-advanced-settings";
            var advanced = GetComProperty(clientObject, "AdvancedSettings9");
            var permissions = RdpSessionPermissionPolicy.Resolve(request.AccessMode, request.Settings);
            stage = "set-security-and-redirection";
            SetComProperty(advanced, "RDPPort", request.Endpoint.IsDefaultPort ? 3389 : request.Endpoint.Port);
            SetComProperty(advanced, "EnableCredSspSupport", true);
            SetComProperty(advanced, "SmartSizing", true);
            SetComProperty(advanced, "ConnectToServerConsole", request.Settings.ConnectAsAdministrator);
            SetComProperty(advanced, "RedirectClipboard", permissions.RedirectClipboard);
            SetComProperty(advanced, "RedirectPrinters", permissions.RedirectPrinters);
            SetComProperty(advanced, "RedirectDrives", permissions.RedirectDrives);
            SetComProperty(advanced, "AudioRedirectionMode", (uint)request.Settings.AudioMode);
            if (!string.IsNullOrWhiteSpace(request.Settings.GatewayHost))
            {
                stage = "set-gateway";
                var transport = GetComProperty(clientObject, "TransportSettings4");
                SetComProperty(transport, "GatewayHostname", request.Settings.GatewayHost);
                SetComProperty(transport, "GatewayUsageMethod", 1u);
                SetComProperty(transport, "GatewayProfileUsageMethod", 1u);
            }
            if (request.PasswordUtf8 is { Length: > 0 })
            {
                stage = "set-credential";
                SetComProperty(advanced, "ClearTextPassword", System.Text.Encoding.UTF8.GetString(request.PasswordUtf8.Span));
            }
            stage = "connect";
            InvokeComMethod(clientObject, "Connect");
            StartConnectionMonitor();
            EnableWindow(_window, permissions.AcceptsInput);
            return;
        }
        catch (Exception exception) when (IsComInvocationException(exception))
        {
            var root = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            var wrapped = new InvalidOperationException(
                $"內嵌 RDP 啟動失敗（{stage}）：{root.Message}", exception);
            wrapped.Data["SafeDiagnostic"] = root is COMException com
                ? $"Embedded RDP failed at {stage}; HRESULT=0x{com.HResult:X8}."
                : $"Embedded RDP late-bound invocation failed at {stage} ({root.GetType().Name}).";
            throw wrapped;
        }
    }

    public Task DisconnectAsync()
    {
        _disconnectRequested = true;
        _connectionMonitor?.Stop();
        _resizeTimer?.Stop();
        if (_rdpClient is not null)
        {
            TryDisconnect(_rdpClient);
        }
        return Task.CompletedTask;
    }

    private void StartConnectionMonitor()
    {
        _connectionMonitor?.Stop();
        _connectionMonitor = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (_disconnectRequested || _rdpClient is null) return;
            try
            {
                var connected = Convert.ToInt16(GetComProperty(_rdpClient, "Connected"), CultureInfo.InvariantCulture);
                if (connected != 0)
                {
                    _hasConnected = true;
                    return;
                }
                if (!_hasConnected)
                {
                    _connectingTicks++;
                    if (_connectingTicks < 30) return;
                    _connectionMonitor?.Stop();
                    UnexpectedlyDisconnected?.Invoke("RDP 連線逾時，請檢查主機、防火牆與帳號密碼");
                    return;
                }
                _connectionMonitor?.Stop();
                string reason;
                try
                {
                    var code = Convert.ToInt32(GetComProperty(_rdpClient, "ExtendedDisconnectReason"), CultureInfo.InvariantCulture);
                    reason = $"RDP 連線已中斷（原因代碼 {code}）";
                }
                catch (Exception exception) when (IsComInvocationException(exception))
                {
                    reason = "RDP 連線非預期中斷";
                }
                UnexpectedlyDisconnected?.Invoke(reason);
            }
            catch (Exception exception) when (IsComInvocationException(exception))
            {
                _connectionMonitor?.Stop();
                UnexpectedlyDisconnected?.Invoke($"無法讀取 RDP 連線狀態：{exception.Message}");
            }
        });
        _connectionMonitor.Start();
    }

    private void ScheduleDisplayResize()
    {
        if (!_hasConnected || _useMultimon || _disconnectRequested) return;
        _resizeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) =>
        {
            _resizeTimer?.Stop();
            if (_rdpClient is null || !_hasConnected || _disconnectRequested) return;
            var width = (uint)Math.Clamp((int)Bounds.Width, 200, 8192);
            var height = (uint)Math.Clamp((int)Bounds.Height, 200, 8192);
            try { InvokeComMethod(_rdpClient, "Reconnect", width, height); }
            catch (Exception exception) when (IsComInvocationException(exception))
            {
                /* SmartSizing remains the safe fallback. */
            }
        });
        _resizeTimer.Stop();
        _resizeTimer.Start();
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
            _disconnectRequested = true;
            _connectionMonitor?.Stop();
            _resizeTimer?.Stop();
            if (_rdpClient is not null)
            {
                TryDisconnect(_rdpClient);
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

    private static void TryDisconnect(object client)
    {
        try
        {
            InvokeComMethod(client, "Disconnect");
        }
        catch (Exception exception) when (IsComInvocationException(exception))
        {
            // Some installed RDP ActiveX revisions do not expose Disconnect through
            // IDispatch until a connection has been initialized. Cleanup is best effort.
        }
    }

    private static void SetComProperty(object target, string name, object? value) =>
        target.GetType().InvokeMember(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.SetProperty,
            null,
            target,
            [value],
            CultureInfo.InvariantCulture);

    private static object GetComProperty(object target, string name) =>
        target.GetType().InvokeMember(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.GetProperty,
            null,
            target,
            null,
            CultureInfo.InvariantCulture)
        ?? throw new InvalidOperationException($"RDP ActiveX property '{name}' returned null.");

    private static object? InvokeComMethod(object target, string name, params object[] arguments) =>
        target.GetType().InvokeMember(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.InvokeMethod,
            null,
            target,
            arguments,
            CultureInfo.InvariantCulture);

    private static bool IsComInvocationException(Exception exception) =>
        exception is COMException or MissingMethodException or TargetException or TargetParameterCountException or ArgumentException ||
        exception is TargetInvocationException { InnerException: COMException };

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
