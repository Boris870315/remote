using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Remote.Application.Connections;
using Remote.Infrastructure.Protocols.Rdp;

namespace Remote.Desktop.Protocols.Rdp;

/// <summary>Hosts Microsoft's RDP ActiveX client as a real child of the Avalonia session workspace.</summary>
public sealed class AvaloniaEmbeddedRdpHost : NativeControlHost
{
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipSiblings = 0x04000000;
    private static readonly Guid DispatchInterfaceId = new("00020400-0000-0000-C000-000000000046");
    private static readonly Guid MsTscAxInterfaceId = new("8C11EFAE-92C3-11D1-BC1E-00C04FA31489");
    private static readonly Guid MsTscNonScriptableInterfaceId = new("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E");
    private static readonly string[] RdpClientClassIds =
    [
        "{3F859AA3-C2D4-4FAA-B0E4-FD0C9C4E5E3A}", // MsRdpClient12NotSafeForScripting
        "{1DF7C823-B2D4-4B54-975A-F2AC5D7CF8B8}", // MsRdpClient11NotSafeForScripting
        "{A0C63C30-F08D-4AB4-907C-34905D770C7D}", // MsRdpClient10NotSafeForScripting
    ];
    private nint _window;
    private object? _rdpClient;
    private readonly TaskCompletionSource _hostReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DispatcherTimer? _connectionMonitor;
    private DispatcherTimer? _displayResizeTimer;
    private TaskCompletionSource? _connectionReady;
    private bool _hasConnected;
    private bool _disconnectRequested;
    private int _connectingTicks;
    private bool _useMultimon;
    private DisplayScaleMode _displayScaleMode = DisplayScaleMode.Fit;
    private PixelSize _lastSessionDisplaySize;
    private string? _selectedClassId;
    private int _lastConnectedState = -1;
    private int _lastDisconnectReason = -1;
    private readonly Stopwatch _connectionElapsed = new();
    private IConnectionPoint? _eventConnectionPoint;
    private int _eventCookie;
    private RdpEventSink? _eventSink;

    public event Action<string>? UnexpectedlyDisconnected;
    public event Action<RdpHostDiagnostic>? Diagnostic;

    public void SetViewOnly(bool viewOnly)
    {
        if (!OperatingSystem.IsWindows() || _window == nint.Zero) return;
        EnableWindow(_window, !viewOnly);
        EmitDiagnostic("input-mode", $"viewOnly={viewOnly}; classId={_selectedClassId ?? "unknown"}");
    }

    public void SetDisplayScaleMode(DisplayScaleMode scaleMode)
    {
        _displayScaleMode = scaleMode;
        if (!OperatingSystem.IsWindows() || _rdpClient is null) return;
        var advanced = ((IMsTscAxDispatch)_rdpClient).AdvancedSettings;
        TrySetComProperty(advanced, "SmartSizing", UsesSmartSizing(scaleMode));
        ResizeNativeSurface();
        EmitDiagnostic("scale-mode", $"mode={scaleMode}; smartSizing={UsesSmartSizing(scaleMode)}");
    }

    internal static bool UsesSmartSizing(DisplayScaleMode scaleMode) =>
        scaleMode is DisplayScaleMode.Fit or DisplayScaleMode.Fill;

    public void SetSessionVisible(bool visible)
    {
        IsVisible = visible;
        if (!OperatingSystem.IsWindows()) return;
        if (_rdpClient is not null)
        {
            // Ask the native RDP control to stop requesting display updates while
            // its tab is in the background. The session and redirected channels
            // remain connected, and output resumes when the tab becomes visible.
            TrySetComProperty(_rdpClient, "SuppressOutput", !visible);
        }
        if (_window == nint.Zero) return;
        _ = ShowWindow(_window, visible ? SwShow : SwHide);
        EmitDiagnostic("surface-visibility", $"visible={visible}; {DescribeNativeSurface()}");
        if (visible)
        {
            ResizeNativeSurface();
            _ = BringWindowToTop(_window);
            Dispatcher.UIThread.Post(() =>
            {
                ResizeNativeSurface();
                ScheduleDisplayResize();
            }, DispatcherPriority.Render);
        }
    }

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
        var stage = "query-client-interface";
        void MoveToStage(string next)
        {
            stage = next;
            EmitDiagnostic("connect-stage", $"stage={stage}; elapsedMs={_connectionElapsed.ElapsedMilliseconds}");
        }

        _connectionElapsed.Restart();
        _lastConnectedState = -1;
        _lastDisconnectReason = -1;
        EmitDiagnostic(
            "connect-start",
            $"hostHash={HashEndpointHost(request.Endpoint.Host)}; port={(request.Endpoint.IsDefaultPort ? 3389 : request.Endpoint.Port)}; " +
            $"classId={_selectedClassId ?? "unknown"}; processArch={RuntimeInformation.ProcessArchitecture}; osArch={RuntimeInformation.OSArchitecture}; " +
            $"os={Environment.OSVersion.Version}; clr={Environment.Version}; apartment={Thread.CurrentThread.GetApartmentState()}; " +
            $"usernamePresent={!string.IsNullOrEmpty(request.Username)}; domainPresent={!string.IsNullOrEmpty(request.Domain)}; " +
            $"passwordPresent={request.PasswordUtf8 is { Length: > 0 }}; accessMode={request.AccessMode}; " +
            $"scaleMode={request.Display.ScaleMode}; monitorSelection={request.Display.MonitorSelection}; monitorIndex={request.Display.MonitorIndex?.ToString(CultureInfo.InvariantCulture) ?? "none"}; " +
            $"remoteGuard={request.Settings.UseRemoteGuard}; admin={request.Settings.ConnectAsAdministrator}; gatewayPresent={!string.IsNullOrWhiteSpace(request.Settings.GatewayHost)}; " +
            DescribeNativeSurface());
        try
        {
            // Native host creation rejects registered controls that do not expose
            // this interface, so the RCW cast is safe for the selected fallback.
            var client = (IMsTscAxDispatch)clientObject;
            _disconnectRequested = false;
            _hasConnected = false;
            _connectingTicks = 0;
            _connectionReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TryDisconnect(clientObject);
            MoveToStage("set-endpoint");
            client.Server = request.Endpoint.Host;
            client.UserName = request.Username ?? string.Empty;
            // Explicitly clear the ActiveX domain when the operator did not
            // provide one. Leaving it untouched lets some Windows revisions
            // infer the destination computer name and display HOST\\username.
            client.Domain = request.Domain ?? string.Empty;
            MoveToStage("set-display");
            _displayScaleMode = request.Display.ScaleMode;
            var initialPixelSize = GetNativeClientPixelSize();
            client.DesktopWidth = Math.Max(640, initialPixelSize.Width);
            client.DesktopHeight = Math.Max(480, initialPixelSize.Height);
            var selectedMonitorIndex = Math.Max(0, request.Display.MonitorIndex ?? 0);
            _useMultimon = request.Display.MonitorSelection is Remote.Application.Connections.MonitorSelection.All ||
                selectedMonitorIndex > 0;
            if (_useMultimon)
            {
                MoveToStage("enable-multiple-monitors");
                if (request.Display.MonitorSelection is Remote.Application.Connections.MonitorSelection.Single)
                {
                    RdpActiveXNativeSettings.SetSelectedMonitors(clientObject, selectedMonitorIndex);
                }
                RdpActiveXNativeSettings.SetUseMultimon(clientObject, true);
            }
            MoveToStage("open-advanced-settings");
            var advanced = client.AdvancedSettings;
            var permissions = RdpSessionPermissionPolicy.Resolve(request.AccessMode, request.Settings);
            MoveToStage("set-security-and-redirection");
            SetComProperty(advanced, "RDPPort", request.Endpoint.IsDefaultPort ? 3389 : request.Endpoint.Port);
            SetComProperty(advanced, "SmartSizing", UsesSmartSizing(_displayScaleMode));
            if (!TrySetComProperty(advanced, "ConnectToAdministerServer", request.Settings.ConnectAsAdministrator))
                SetComProperty(advanced, "ConnectToServerConsole", request.Settings.ConnectAsAdministrator);
            SetComProperty(advanced, "RedirectClipboard", permissions.RedirectClipboard);
            SetComProperty(advanced, "RedirectPrinters", permissions.RedirectPrinters);
            SetComProperty(advanced, "RedirectDrives", permissions.RedirectDrives);
            SetComProperty(advanced, "AudioCaptureRedirectionMode", permissions.RedirectMicrophone);
            if (permissions.RedirectCamera && !RdpActiveXNativeSettings.TryRedirectAllCameras(clientObject))
            {
                throw new NotSupportedException(
                    "此 Windows RDP 控制項不支援相機重新導向；請更新 Windows Remote Desktop 元件。");
            }
            SetComProperty(advanced, "AudioRedirectionMode", (uint)request.Settings.AudioMode);
            TrySetComProperty(
                advanced,
                "AuthenticationLevel",
                GetAuthenticationLevel(request.Settings.CertificatePolicy));
            if (request.Settings.UseRemoteGuard)
            {
                MoveToStage("enable-remote-credential-guard");
                RdpActiveXNativeSettings.SetExtendedBoolean(
                    clientObject, "RedirectedAuthentication", true);
            }
            if (!string.IsNullOrWhiteSpace(request.Settings.GatewayHost))
            {
                MoveToStage("set-gateway");
                var transport = GetComProperty(clientObject, "TransportSettings4");
                SetComProperty(transport, "GatewayHostname", request.Settings.GatewayHost);
                SetComProperty(transport, "GatewayUsageMethod", 1u);
                SetComProperty(transport, "GatewayProfileUsageMethod", 1u);
            }
            if (!request.Settings.UseRemoteGuard && request.PasswordUtf8 is { Length: > 0 })
            {
                MoveToStage("set-credential");
                var clearTextPassword = System.Text.Encoding.UTF8.GetString(request.PasswordUtf8.Span);
                var credentialSettings = TryGetComProperty(clientObject, "AdvancedSettings2") ?? advanced;
                if (!TrySetComProperty(credentialSettings, "ClearTextPassword", clearTextPassword))
                {
                    var passwordProvider = (IMsTscNonScriptable)clientObject;
                    var passwordResult = passwordProvider.SetClearTextPassword(clearTextPassword);
                    Marshal.ThrowExceptionForHR(passwordResult);
                }
            }
            MoveToStage("tcp-preflight");
            await ProbeTcpEndpointAsync(
                request.Endpoint.Host,
                request.Endpoint.IsDefaultPort ? 3389 : request.Endpoint.Port);
            MoveToStage("connect");
            client.Connect();
            StartConnectionMonitor();
            EnableWindow(_window, permissions.AcceptsInput);
            MoveToStage("wait-for-connected-state");
            await _connectionReady.Task;
            _connectionElapsed.Stop();
            EmitDiagnostic("connect-complete", $"elapsedMs={_connectionElapsed.ElapsedMilliseconds}; {DescribeNativeSurface()}");
            return;
        }
        catch (Exception exception) when (IsComInvocationException(exception))
        {
            _connectionElapsed.Stop();
            var root = GetRootException(exception);
            var wrapped = new InvalidOperationException(
                $"內嵌 RDP 啟動失敗（{stage}）：{root.Message}", exception);
            var diagnostic =
                $"stage={stage}; exception={root.GetType().Name}; hresult=0x{root.HResult:X8}; " +
                $"classId={_selectedClassId ?? "unknown"}; elapsedMs={_connectionElapsed.ElapsedMilliseconds}; {DescribeNativeSurface()}";
            EmitDiagnostic("connect-failed", diagnostic);
            wrapped.Data["SafeDiagnostic"] = $"Embedded RDP failed; {diagnostic}";
            throw wrapped;
        }
    }

    // AuthenticationLevel 2 still displays Microsoft's native "cannot verify"
    // confirmation. That prompt can be detached from an embedded ActiveX host and
    // leaves the in-app session blocked. PromptOnUntrusted is the existing persisted
    // opt-in used by the FreeRDP host to accept a self-signed certificate, so use the
    // equivalent non-blocking ActiveX level on Windows as well.
    internal static uint GetAuthenticationLevel(RdpCertificatePolicy certificatePolicy) =>
        certificatePolicy is RdpCertificatePolicy.RequireTrusted ? 1u : 0u;

    public Task DisconnectAsync()
    {
        _disconnectRequested = true;
        EmitDiagnostic("disconnect-requested", $"connected={_hasConnected}; {DescribeNativeSurface()}");
        _connectionMonitor?.Stop();
        _connectionReady?.TrySetCanceled();
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
                var connected = ((IMsTscAxDispatch)_rdpClient).Connected;
                var disconnectReason = connected == 1 ? 0 : TryGetExtendedDisconnectReason(_rdpClient);
                if (connected != _lastConnectedState || disconnectReason != _lastDisconnectReason)
                {
                    EmitDiagnostic(
                        "connection-state",
                        $"connected={connected}; extendedDisconnectReason={disconnectReason}; " +
                        $"elapsedMs={_connectionElapsed.ElapsedMilliseconds}; {DescribeNativeSurface()}");
                    _lastConnectedState = connected;
                    _lastDisconnectReason = disconnectReason;
                }
                // IMsTscAx.Connected: 0 = disconnected, 1 = connected,
                // 2 = still connecting. A pending handshake is not success.
                if (connected == 1)
                {
                    _hasConnected = true;
                    _connectionReady?.TrySetResult();
                    ScheduleDisplayResize();
                    return;
                }
                if (!_hasConnected)
                {
                    _connectingTicks++;
                    if (connected == 0 && disconnectReason > 2)
                    {
                        _connectionMonitor?.Stop();
                        var rejected = new InvalidOperationException(
                            RdpDisconnectReasonFormatter.Format(disconnectReason));
                        rejected.Data["SafeDiagnostic"] =
                            $"Embedded RDP connection rejected; ExtendedDisconnectReason={disconnectReason}; " +
                            $"classId={_selectedClassId ?? "unknown"}; elapsedMs={_connectionElapsed.ElapsedMilliseconds}.";
                        EmitDiagnostic("connection-rejected", rejected.Data["SafeDiagnostic"]!.ToString()!);
                        _connectionReady?.TrySetException(rejected);
                        return;
                    }
                    if (_connectingTicks < 30) return;
                    _connectionMonitor?.Stop();
                    var timeout = new TimeoutException("RDP 連線逾時，請檢查主機、防火牆與帳號密碼");
                    timeout.Data["SafeDiagnostic"] =
                        $"Embedded RDP did not reach Connected state within 30 seconds; connected={connected}; " +
                        $"ExtendedDisconnectReason={disconnectReason}; classId={_selectedClassId ?? "unknown"}; {DescribeNativeSurface()}";
                    EmitDiagnostic("connection-timeout", timeout.Data["SafeDiagnostic"]!.ToString()!);
                    _connectionReady?.TrySetException(timeout);
                    return;
                }
                _connectionMonitor?.Stop();
                var reason = TryGetExtendedDisconnectReason(_rdpClient);
                EmitDiagnostic("connection-lost", $"extendedDisconnectReason={reason}; {DescribeNativeSurface()}");
                UnexpectedlyDisconnected?.Invoke(RdpDisconnectReasonFormatter.Format(reason));
            }
            catch (Exception exception) when (IsComInvocationException(exception))
            {
                _connectionMonitor?.Stop();
                var root = GetRootException(exception);
                EmitDiagnostic(
                    "monitor-failed",
                    $"exception={root.GetType().Name}; hresult=0x{root.HResult:X8}; connectedPreviously={_hasConnected}; {DescribeNativeSurface()}");
                if (!_hasConnected)
                {
                    _connectionReady?.TrySetException(new InvalidOperationException(
                        $"無法讀取 RDP 連線狀態：{exception.Message}", exception));
                }
                else
                {
                    UnexpectedlyDisconnected?.Invoke($"無法讀取 RDP 連線狀態：{exception.Message}");
                }
            }
        });
        _connectionMonitor.Start();
    }

    private void ScheduleDisplayResize()
    {
        // AtlAxWin resizes the ActiveX surface with its native child window.
        // Do not call the RDP Reconnect method here: several installed client
        // revisions recreate the desktop as a blank surface during Avalonia
        // full-screen and layout transitions. SmartSizing keeps the existing
        // session visible while the native host follows the new bounds.
        ResizeNativeSurface();
        if (!_hasConnected || _useMultimon || _rdpClient is null) return;

        // Coalesce Avalonia's intermediate layout sizes. Newer RDP controls can
        // change the server-side session resolution without disconnecting; older
        // controls simply keep SmartSizing as the safe fallback.
        _displayResizeTimer?.Stop();
        _displayResizeTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _displayResizeTimer?.Stop();
                if (!OperatingSystem.IsWindows() || !_hasConnected || _useMultimon || _rdpClient is null) return;
                var pixelSize = GetNativeClientPixelSize();
                if (pixelSize == _lastSessionDisplaySize || pixelSize.Width < 200 || pixelSize.Height < 200) return;
                var updated = RdpActiveXNativeSettings.TryUpdateSessionDisplaySettings(_rdpClient, pixelSize);
                EmitDiagnostic(
                    "display-resize",
                    $"requested={pixelSize.Width}x{pixelSize.Height}; dynamicUpdate={updated}; scaleMode={_displayScaleMode}; {DescribeNativeSurface()}");
                if (updated)
                {
                    _lastSessionDisplaySize = pixelSize;
                }
            });
        _displayResizeTimer.Start();
    }

    private static int TryGetExtendedDisconnectReason(object client)
    {
        try
        {
            var value = GetComProperty(client, "ExtendedDisconnectReason");
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (IsComInvocationException(exception))
        {
            return 0;
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return base.CreateNativeControlCore(parent);
        }

        var oleResult = OleInitialize(nint.Zero);
        EmitDiagnostic(
            "native-host-start",
            $"oleHresult=0x{oleResult:X8}; parentHwnd=0x{parent.Handle:X}; processArch={RuntimeInformation.ProcessArchitecture}; " +
            $"osArch={RuntimeInformation.OSArchitecture}; os={Environment.OSVersion.Version}; clr={Environment.Version}; apartment={Thread.CurrentThread.GetApartmentState()}");
        if (!AtlAxWinInit())
        {
            EmitDiagnostic("atl-init-failed", $"win32Error={Marshal.GetLastWin32Error()}");
            throw new InvalidOperationException("Windows ATL ActiveX host initialization failed.");
        }

        nint unknown = nint.Zero;
        var result = unchecked((int)0x80004005);
        var scriptableInterfaceResult = unchecked((int)0x80004002);
        var nonScriptableInterfaceResult = unchecked((int)0x80004002);
        foreach (var classId in RdpClientClassIds)
        {
            scriptableInterfaceResult = unchecked((int)0x80004002);
            nonScriptableInterfaceResult = unchecked((int)0x80004002);
            _window = CreateWindowExW(
                0,
                "AtlAxWin",
                classId,
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
                EmitDiagnostic("activex-create-attempt", $"classId={classId}; hwnd=0; win32Error={Marshal.GetLastWin32Error()}");
                continue;
            }
            result = AtlAxGetControl(_window, out unknown);
            if (result >= 0 && unknown != nint.Zero)
            {
                scriptableInterfaceResult = QueryInterfaceResult(unknown, MsTscAxInterfaceId);
                nonScriptableInterfaceResult = QueryInterfaceResult(unknown, MsTscNonScriptableInterfaceId);
            }
            EmitDiagnostic(
                "activex-create-attempt",
                $"classId={classId}; hwnd=0x{_window:X}; atlHresult=0x{result:X8}; controlPointerPresent={unknown != nint.Zero}; " +
                $"iMsTscAx=0x{scriptableInterfaceResult:X8}; iMsTscNonScriptable=0x{nonScriptableInterfaceResult:X8}");
            if (result >= 0 && unknown != nint.Zero &&
                scriptableInterfaceResult >= 0 && nonScriptableInterfaceResult >= 0)
            {
                _selectedClassId = classId;
                break;
            }
            if (unknown != nint.Zero)
            {
                Marshal.Release(unknown);
                unknown = nint.Zero;
            }
            DestroyWindow(_window);
            _window = nint.Zero;
        }

        if (_window == nint.Zero || unknown == nint.Zero)
        {
            EmitDiagnostic(
                "native-host-failed",
                $"lastHresult=0x{result:X8}; iMsTscAx=0x{scriptableInterfaceResult:X8}; " +
                $"iMsTscNonScriptable=0x{nonScriptableInterfaceResult:X8}; lastWin32Error={Marshal.GetLastWin32Error()}");
            throw new InvalidOperationException(
                $"Windows could not create an embedded RDP host with the required COM interfaces " +
                $"(HRESULT=0x{result:X8}, interface HRESULT=0x{scriptableInterfaceResult:X8}).");
        }

        try
        {
            _rdpClient = Marshal.GetObjectForIUnknown(unknown);
            AttachEventSink(_rdpClient);
            EmitDiagnostic(
                "native-host-ready",
                $"classId={_selectedClassId}; rcwType={_rdpClient.GetType().FullName ?? _rdpClient.GetType().Name}; " +
                $"iDispatch={ProbeComInterface(_rdpClient, DispatchInterfaceId)}; " +
                $"iMsTscAx={ProbeComInterface(_rdpClient, MsTscAxInterfaceId)}; " +
                $"iMsTscNonScriptable={ProbeComInterface(_rdpClient, MsTscNonScriptableInterfaceId)}; {DescribeNativeSurface()}");
            _hostReady.TrySetResult();
            Dispatcher.UIThread.Post(ResizeNativeSurface, DispatcherPriority.Render);
        }
        finally
        {
            Marshal.Release(unknown);
        }
        return new PlatformHandle(_window, "HWND");
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        // NativeControlHost owns the outer HWND geometry. Only size the ActiveX
        // child after Avalonia has arranged that HWND; resizing the outer HWND a
        // second time applies DPI scaling twice and duplicates/crops the desktop.
        Dispatcher.UIThread.Post(ResizeNativeSurface, DispatcherPriority.Render);
        return arranged;
    }

    private void ResizeNativeSurface()
    {
        if (!OperatingSystem.IsWindows() || _window == nint.Zero) return;
        if (!GetClientRect(_window, out var clientRect)) return;
        var width = clientRect.Right - clientRect.Left;
        var height = clientRect.Bottom - clientRect.Top;
        if (width < 1 || height < 1) return;

        // AtlAxWin does not consistently propagate its client rectangle to the
        // hosted RDP window, especially on first display and DPI transitions.
        var activeXWindow = GetWindow(_window, GwChild);
        if (activeXWindow != nint.Zero)
        {
            _ = SetWindowPos(
                activeXWindow,
                nint.Zero,
                0,
                0,
                width,
                height,
                SwpNoZOrder | SwpNoActivate | SwpShowWindow);
        }
        _ = InvalidateRect(_window, nint.Zero, false);
        _ = UpdateWindow(_window);
    }

    private PixelSize GetNativeClientPixelSize()
    {
        if (_window != nint.Zero && GetClientRect(_window, out var clientRect))
        {
            var width = clientRect.Right - clientRect.Left;
            var height = clientRect.Bottom - clientRect.Top;
            if (width > 0 && height > 0) return new PixelSize(width, height);
        }
        return GetPhysicalPixelSize(Bounds.Size);
    }

    private PixelSize GetPhysicalPixelSize(Size size)
    {
        var renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        return new PixelSize(
            Math.Max(1, (int)Math.Ceiling(size.Width * renderScaling)),
            Math.Max(1, (int)Math.Ceiling(size.Height * renderScaling)));
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (OperatingSystem.IsWindows() && _window != nint.Zero)
        {
            _disconnectRequested = true;
            EmitDiagnostic("native-host-destroy", $"connected={_hasConnected}; classId={_selectedClassId ?? "unknown"}; {DescribeNativeSurface()}");
            _connectionMonitor?.Stop();
            _displayResizeTimer?.Stop();
            DetachEventSink();
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

    private static void TryDisconnect(object client)
    {
        try
        {
            ((IMsTscAxDispatch)client).Disconnect();
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

    private static bool TrySetComProperty(object target, string name, object? value)
    {
        try
        {
            SetComProperty(target, name, value);
            return true;
        }
        catch (Exception exception) when (IsComInvocationException(exception))
        {
            return false;
        }
    }

    private static object GetComProperty(object target, string name) =>
        target.GetType().InvokeMember(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.GetProperty,
            null,
            target,
            null,
            CultureInfo.InvariantCulture)
        ?? throw new InvalidOperationException($"RDP ActiveX property '{name}' returned null.");

    private static object? TryGetComProperty(object target, string name)
    {
        try
        {
            return GetComProperty(target, name);
        }
        catch (Exception exception) when (IsComInvocationException(exception))
        {
            return null;
        }
    }

    private static object? InvokeComMethod(object target, string name, params object[] arguments) =>
        target.GetType().InvokeMember(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.InvokeMethod,
            null,
            target,
            arguments,
            CultureInfo.InvariantCulture);

    private async Task ProbeTcpEndpointAsync(string host, int port)
    {
        var elapsed = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            EmitDiagnostic("tcp-preflight", $"result=connected; port={port}; elapsedMs={elapsed.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            EmitDiagnostic("tcp-preflight", $"result=timeout; port={port}; elapsedMs={elapsed.ElapsedMilliseconds}");
        }
        catch (SocketException exception)
        {
            EmitDiagnostic(
                "tcp-preflight",
                $"result=socket-error; port={port}; socketError={exception.SocketErrorCode}; nativeError={exception.NativeErrorCode}; elapsedMs={elapsed.ElapsedMilliseconds}");
        }
        catch (Exception exception)
        {
            EmitDiagnostic(
                "tcp-preflight",
                $"result=failed; port={port}; exception={exception.GetType().Name}; hresult=0x{exception.HResult:X8}; elapsedMs={elapsed.ElapsedMilliseconds}");
        }
    }

    [SupportedOSPlatform("windows")]
    private void AttachEventSink(object client)
    {
        try
        {
            var container = (IConnectionPointContainer)client;
            var eventsInterfaceId = new Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6");
            container.FindConnectionPoint(ref eventsInterfaceId, out var connectionPoint);
            if (connectionPoint is null) throw new COMException("RDP ActiveX returned no event connection point.");
            _eventConnectionPoint = connectionPoint;
            var eventSink = new RdpEventSink(this);
            _eventSink = eventSink;
            connectionPoint.Advise(eventSink, out _eventCookie);
            EmitDiagnostic("event-sink", $"result=attached; cookie={_eventCookie}");
        }
        catch (Exception exception) when (IsComInvocationException(exception))
        {
            var root = GetRootException(exception);
            EmitDiagnostic("event-sink", $"result=failed; exception={root.GetType().Name}; hresult=0x{root.HResult:X8}");
            DetachEventSink();
        }
    }

    [SupportedOSPlatform("windows")]
    private void DetachEventSink()
    {
        if (_eventConnectionPoint is not null && _eventCookie != 0)
        {
            try
            {
                _eventConnectionPoint.Unadvise(_eventCookie);
            }
            catch (COMException)
            {
            }
        }
        _eventCookie = 0;
        _eventSink = null;
        if (_eventConnectionPoint is not null && Marshal.IsComObject(_eventConnectionPoint))
        {
            Marshal.FinalReleaseComObject(_eventConnectionPoint);
        }
        _eventConnectionPoint = null;
    }

    private void HandleNativeDisconnected(int reason)
    {
        EmitDiagnostic(
            "event-disconnected",
            $"reason={reason}; extendedDisconnectReason={(_rdpClient is null ? 0 : TryGetExtendedDisconnectReason(_rdpClient))}; " +
            $"disconnectRequested={_disconnectRequested}; elapsedMs={_connectionElapsed.ElapsedMilliseconds}");
        if (_disconnectRequested) return;

        if (!_hasConnected)
        {
            var exception = new InvalidOperationException(RdpDisconnectReasonFormatter.Format(reason));
            exception.Data["SafeDiagnostic"] =
                $"Embedded RDP disconnected while connecting; reason={reason}; classId={_selectedClassId ?? "unknown"}.";
            _connectionReady?.TrySetException(exception);
            return;
        }
        UnexpectedlyDisconnected?.Invoke(RdpDisconnectReasonFormatter.Format(reason));
    }

    private void HandleNativeFatalError(int errorCode)
    {
        EmitDiagnostic("event-fatal-error", $"errorCode={errorCode}; elapsedMs={_connectionElapsed.ElapsedMilliseconds}");
        if (_disconnectRequested || _hasConnected) return;
        var exception = new InvalidOperationException($"RDP ActiveX fatal error {errorCode}.");
        exception.Data["SafeDiagnostic"] =
            $"Embedded RDP fatal error; errorCode={errorCode}; classId={_selectedClassId ?? "unknown"}.";
        _connectionReady?.TrySetException(exception);
    }

    private void EmitDiagnostic(string code, string message)
    {
        try
        {
            Diagnostic?.Invoke(new RdpHostDiagnostic(code, message));
        }
        catch
        {
            // Diagnostic observers must not affect the native host.
        }
    }

    private string DescribeNativeSurface()
    {
        var hostSize = TryGetWindowClientSize(_window);
        var activeXWindow = _window == nint.Zero ? nint.Zero : GetWindow(_window, GwChild);
        var activeXSize = TryGetWindowClientSize(activeXWindow);
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"avaloniaBounds={Bounds.Width:0.##}x{Bounds.Height:0.##}; renderScaling={scaling:0.###}; " +
            $"hostClient={hostSize}; activeXClient={activeXSize}; hostHwndPresent={_window != nint.Zero}; activeXHwndPresent={activeXWindow != nint.Zero}");
    }

    private static string TryGetWindowClientSize(nint window)
    {
        if (window == nint.Zero || !GetClientRect(window, out var rectangle)) return "unavailable";
        return $"{Math.Max(0, rectangle.Right - rectangle.Left)}x{Math.Max(0, rectangle.Bottom - rectangle.Top)}";
    }

    private static string HashEndpointHost(string host)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(host.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 6));
    }

    [SupportedOSPlatform("windows")]
    private static string ProbeComInterface(object client, Guid interfaceId)
    {
        nint unknown = nint.Zero;
        nint interfacePointer = nint.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(client);
            var result = Marshal.QueryInterface(unknown, in interfaceId, out interfacePointer);
            return $"0x{result:X8}";
        }
        catch (Exception exception) when (IsComInvocationException(exception))
        {
            return $"exception:{GetRootException(exception).GetType().Name}";
        }
        finally
        {
            if (interfacePointer != nint.Zero) Marshal.Release(interfacePointer);
            if (unknown != nint.Zero) Marshal.Release(unknown);
        }
    }

    private static int QueryInterfaceResult(nint unknown, Guid interfaceId)
    {
        nint interfacePointer = nint.Zero;
        try
        {
            return Marshal.QueryInterface(unknown, in interfaceId, out interfacePointer);
        }
        finally
        {
            if (interfacePointer != nint.Zero) Marshal.Release(interfacePointer);
        }
    }

    private static Exception GetRootException(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: { } inner })
        {
            exception = inner;
        }
        return exception;
    }

    private static bool IsComInvocationException(Exception exception) =>
        exception is COMException or InvalidCastException or MissingMethodException or TargetException or TargetParameterCountException or ArgumentException ||
        exception is TargetInvocationException { InnerException: { } inner } && IsComInvocationException(inner);

    [ComVisible(true)]
    [Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IMsTscAxEventsSink
    {
        [DispId(1)] void OnConnecting();
        [DispId(2)] void OnConnected();
        [DispId(3)] void OnLoginComplete();
        [DispId(4)] void OnDisconnected(int reason);
        [DispId(10)] void OnFatalError(int errorCode);
        [DispId(11)] void OnWarning(int warningCode);
        [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);
        [DispId(18)] void OnAuthenticationWarningDisplayed();
        [DispId(19)] void OnAuthenticationWarningDismissed();
        [DispId(22)] void OnLogonError(int errorCode);
        [DispId(27)] void OnStatusInfo(uint statusCode);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class RdpEventSink(AvaloniaEmbeddedRdpHost host) : IMsTscAxEventsSink
    {
        public void OnConnecting() => host.EmitDiagnostic(
            "event-connecting", $"elapsedMs={host._connectionElapsed.ElapsedMilliseconds}");

        public void OnConnected() => host.EmitDiagnostic(
            "event-connected", $"elapsedMs={host._connectionElapsed.ElapsedMilliseconds}");

        public void OnLoginComplete() => host.EmitDiagnostic(
            "event-login-complete", $"elapsedMs={host._connectionElapsed.ElapsedMilliseconds}");

        public void OnDisconnected(int reason) => host.HandleNativeDisconnected(reason);

        public void OnFatalError(int errorCode) => host.HandleNativeFatalError(errorCode);

        public void OnWarning(int warningCode) => host.EmitDiagnostic(
            "event-warning", $"warningCode={warningCode}; elapsedMs={host._connectionElapsed.ElapsedMilliseconds}");

        public void OnRemoteDesktopSizeChange(int width, int height) => host.EmitDiagnostic(
            "event-desktop-size", $"remote={width}x{height}; {host.DescribeNativeSurface()}");

        public void OnAuthenticationWarningDisplayed() => host.EmitDiagnostic(
            "event-authentication-warning", $"displayed=True; elapsedMs={host._connectionElapsed.ElapsedMilliseconds}");

        public void OnAuthenticationWarningDismissed() => host.EmitDiagnostic(
            "event-authentication-warning", $"displayed=False; elapsedMs={host._connectionElapsed.ElapsedMilliseconds}");

        public void OnLogonError(int errorCode) => host.EmitDiagnostic(
            "event-logon-error", $"errorCode={errorCode}; hex=0x{errorCode:X8}; elapsedMs={host._connectionElapsed.ElapsedMilliseconds}");

        public void OnStatusInfo(uint statusCode) => host.EmitDiagnostic(
            "event-status", $"statusCode={statusCode}; hex=0x{statusCode:X8}; elapsedMs={host._connectionElapsed.ElapsedMilliseconds}");
    }

    [ComImport]
    [Guid("8C11EFAE-92C3-11D1-BC1E-00C04FA31489")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IMsTscAxDispatch
    {
        [DispId(1)] string Server { get; set; }
        [DispId(2)] string Domain { get; set; }
        [DispId(3)] string UserName { get; set; }
        [DispId(6)] short Connected { get; }
        [DispId(12)] int DesktopWidth { get; set; }
        [DispId(13)] int DesktopHeight { get; set; }
        [DispId(98)]
        object AdvancedSettings
        {
            [return: MarshalAs(UnmanagedType.IDispatch)]
            get;
        }
        [DispId(30)] void Connect();
        [DispId(31)] void Disconnect();
    }

    [ComImport]
    [Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMsTscNonScriptable
    {
        [PreserveSig]
        int SetClearTextPassword([MarshalAs(UnmanagedType.BStr)] string password);
    }

    [DllImport("atl.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AtlAxWinInit();

    [DllImport("atl.dll", ExactSpelling = true)]
    private static extern int AtlAxGetControl(nint window, out nint unknown);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint GwChild = 5;
    private const int SwHide = 0;
    private const int SwShow = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(nint window, nint rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(nint window);

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

    /// <summary>
    /// Invokes the installed RDP client's non-scriptable interface using the
    /// vtable offset published by its own type library. This avoids hard-coding
    /// the long inherited IMsRdpClientNonScriptable5 vtable layout.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static class RdpActiveXNativeSettings
    {
        private const int RegkindNone = 2;
        private static readonly Guid NonScriptable5Id = new("4F6996D5-D7B1-412C-B0FF-063718566907");
        private static readonly Guid NonScriptable7Id = new("71B4A60A-FE21-46D8-A39B-8E32BA0C5ECC");
        private static readonly Guid CameraCollectionId = new("AE45252B-AAAB-4504-B681-649D6073A37A");
        private static readonly Guid CameraConfigId = new("09750604-D625-47C1-9FCD-F09F735705D7");
        private static readonly Guid RdpClient9Id = new("28904001-04B6-436C-A55B-0AF1A0883DC9");

        public static bool TryUpdateSessionDisplaySettings(object client, PixelSize size)
        {
            nint unknown = nint.Zero;
            nint interfacePointer = nint.Zero;
            ITypeLib? typeLibrary = null;
            try
            {
                typeLibrary = LoadRdpTypeLibrary();
                var interfaceId = RdpClient9Id;
                typeLibrary.GetTypeInfoOfGuid(ref interfaceId, out var typeInfo);
                var vtableOffset = FindMethodOffset(typeInfo, "UpdateSessionDisplaySettings");

                unknown = Marshal.GetIUnknownForObject(client);
                if (Marshal.QueryInterface(unknown, in interfaceId, out interfacePointer) < 0) return false;
                var vtable = Marshal.ReadIntPtr(interfacePointer);
                var functionPointer = Marshal.ReadIntPtr(vtable, vtableOffset);
                var update = Marshal.GetDelegateForFunctionPointer<UpdateSessionDisplaySettings>(functionPointer);
                var width = (uint)size.Width;
                var height = (uint)size.Height;
                return update(interfacePointer, width, height, width, height, 0, 100, 100) >= 0;
            }
            catch (Exception exception) when (IsComInvocationException(exception) || exception is TypeLoadException)
            {
                return false;
            }
            finally
            {
                if (interfacePointer != nint.Zero) Marshal.Release(interfacePointer);
                if (unknown != nint.Zero) Marshal.Release(unknown);
                if (typeLibrary is not null && Marshal.IsComObject(typeLibrary)) Marshal.FinalReleaseComObject(typeLibrary);
            }
        }

        public static void SetSelectedMonitors(object client, int monitorIndex)
        {
            var extendedSettings = (IMsRdpExtendedSettings)client;
            object value = monitorIndex.ToString(CultureInfo.InvariantCulture);
            Marshal.ThrowExceptionForHR(extendedSettings.SetProperty("SelectedMonitors", ref value));
        }

        public static void SetExtendedBoolean(object client, string propertyName, bool enabled)
        {
            var extendedSettings = (IMsRdpExtendedSettings)client;
            object value = enabled;
            Marshal.ThrowExceptionForHR(extendedSettings.SetProperty(propertyName, ref value));
        }

        public static void SetUseMultimon(object client, bool enabled)
        {
            var typeLibrary = LoadRdpTypeLibrary();

            nint unknown = nint.Zero;
            nint interfacePointer = nint.Zero;
            try
            {
                var interfaceId = NonScriptable5Id;
                typeLibrary.GetTypeInfoOfGuid(ref interfaceId, out var typeInfo);
                var vtableOffset = FindPropertySetterOffset(typeInfo, "UseMultimon");

                unknown = Marshal.GetIUnknownForObject(client);
                var queryResult = Marshal.QueryInterface(unknown, in interfaceId, out interfacePointer);
                Marshal.ThrowExceptionForHR(queryResult);

                var vtable = Marshal.ReadIntPtr(interfacePointer);
                var functionPointer = Marshal.ReadIntPtr(vtable, vtableOffset);
                var setter = Marshal.GetDelegateForFunctionPointer<PutVariantBool>(functionPointer);
                Marshal.ThrowExceptionForHR(setter(interfacePointer, enabled ? (short)-1 : (short)0));
            }
            finally
            {
                if (interfacePointer != nint.Zero) Marshal.Release(interfacePointer);
                if (unknown != nint.Zero) Marshal.Release(unknown);
                if (Marshal.IsComObject(typeLibrary)) Marshal.FinalReleaseComObject(typeLibrary);
            }
        }

        public static bool TryRedirectAllCameras(object client)
        {
            nint unknown = nint.Zero;
            nint nonScriptable = nint.Zero;
            nint collection = nint.Zero;
            ITypeLib? typeLibrary = null;
            try
            {
                typeLibrary = LoadRdpTypeLibrary();
                unknown = Marshal.GetIUnknownForObject(client);
                if (Marshal.QueryInterface(unknown, in NonScriptable7Id, out nonScriptable) < 0)
                    return false;

                var nonScriptableInfo = GetTypeInfo(typeLibrary, NonScriptable7Id);
                var getCollectionOffset = FindPropertyGetterOffset(
                    nonScriptableInfo, "CameraRedirConfigCollection");
                var getCollection = GetVtableDelegate<GetInterface>(nonScriptable, getCollectionOffset);
                if (getCollection(nonScriptable, out collection) < 0 || collection == nint.Zero)
                    return false;

                var collectionInfo = GetTypeInfo(typeLibrary, CameraCollectionId);
                Marshal.ThrowExceptionForHR(GetVtableDelegate<InvokeNoArgs>(collection,
                    FindMethodOffset(collectionInfo, "Rescan"))(collection));

                collectionInfo = GetTypeInfo(typeLibrary, CameraCollectionId);
                Marshal.ThrowExceptionForHR(GetVtableDelegate<PutVariantBool>(collection,
                    FindPropertySetterOffset(collectionInfo, "RedirectByDefault"))(collection, -1));

                collectionInfo = GetTypeInfo(typeLibrary, CameraCollectionId);
                var getCount = GetVtableDelegate<GetUInt32>(collection,
                    FindPropertyGetterOffset(collectionInfo, "Count"));
                Marshal.ThrowExceptionForHR(getCount(collection, out var count));

                for (uint index = 0; index < count; index++)
                {
                    collectionInfo = GetTypeInfo(typeLibrary, CameraCollectionId);
                    var getCamera = GetVtableDelegate<GetInterfaceByIndex>(collection,
                        FindPropertyGetterOffset(collectionInfo, "ByIndex"));
                    nint camera = nint.Zero;
                    try
                    {
                        Marshal.ThrowExceptionForHR(getCamera(collection, index, out camera));
                        if (camera == nint.Zero) continue;
                        var cameraInfo = GetTypeInfo(typeLibrary, CameraConfigId);
                        var setRedirected = GetVtableDelegate<PutVariantBool>(camera,
                            FindPropertySetterOffset(cameraInfo, "Redirected"));
                        Marshal.ThrowExceptionForHR(setRedirected(camera, -1));
                    }
                    finally
                    {
                        if (camera != nint.Zero) Marshal.Release(camera);
                    }
                }
                return true;
            }
            catch (Exception exception) when (IsComInvocationException(exception) || exception is TypeLoadException)
            {
                return false;
            }
            finally
            {
                if (collection != nint.Zero) Marshal.Release(collection);
                if (nonScriptable != nint.Zero) Marshal.Release(nonScriptable);
                if (unknown != nint.Zero) Marshal.Release(unknown);
                if (typeLibrary is not null && Marshal.IsComObject(typeLibrary))
                    Marshal.FinalReleaseComObject(typeLibrary);
            }
        }

        private static TDelegate GetVtableDelegate<TDelegate>(nint instance, int offset)
            where TDelegate : Delegate
        {
            var vtable = Marshal.ReadIntPtr(instance);
            var functionPointer = Marshal.ReadIntPtr(vtable, offset);
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(functionPointer);
        }

        private static ITypeLib LoadRdpTypeLibrary()
        {
            var typeLibraryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "mstscax.dll");
            Marshal.ThrowExceptionForHR(LoadTypeLibEx(typeLibraryPath, RegkindNone, out var typeLibrary));
            return typeLibrary;
        }

        private static ITypeInfo GetTypeInfo(ITypeLib typeLibrary, Guid interfaceId)
        {
            typeLibrary.GetTypeInfoOfGuid(ref interfaceId, out var typeInfo);
            return typeInfo;
        }

        private static int FindMethodOffset(ITypeInfo typeInfo, string methodName) =>
            FindFunctionOffset(typeInfo, methodName, INVOKEKIND.INVOKE_FUNC);

        private static int FindPropertySetterOffset(ITypeInfo typeInfo, string propertyName)
            => FindFunctionOffset(typeInfo, propertyName, INVOKEKIND.INVOKE_PROPERTYPUT);

        private static int FindPropertyGetterOffset(ITypeInfo typeInfo, string propertyName)
            => FindFunctionOffset(typeInfo, propertyName, INVOKEKIND.INVOKE_PROPERTYGET);

        private static int FindFunctionOffset(ITypeInfo typeInfo, string functionName, INVOKEKIND invokeKind)
        {
            typeInfo.GetTypeAttr(out var typeAttributePointer);
            try
            {
                var typeAttribute = Marshal.PtrToStructure<TYPEATTR>(typeAttributePointer);
                for (var index = 0; index < typeAttribute.cFuncs; index++)
                {
                    typeInfo.GetFuncDesc(index, out var functionPointer);
                    try
                    {
                        var function = Marshal.PtrToStructure<FUNCDESC>(functionPointer);
                        if (function.invkind != invokeKind) continue;
                        var names = new string[1];
                        typeInfo.GetNames(function.memid, names, names.Length, out var nameCount);
                        if (nameCount == 1 && string.Equals(names[0], functionName, StringComparison.Ordinal))
                        {
                            return function.oVft;
                        }
                    }
                    finally
                    {
                        typeInfo.ReleaseFuncDesc(functionPointer);
                    }
                }
            }
            finally
            {
                typeInfo.ReleaseTypeAttr(typeAttributePointer);
                if (Marshal.IsComObject(typeInfo)) Marshal.FinalReleaseComObject(typeInfo);
            }

            throw new MissingMethodException(
                $"The installed Microsoft RDP client does not publish {functionName}.");
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PutVariantBool(nint self, short enabled);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetInterface(nint self, out nint value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetInterfaceByIndex(nint self, uint index, out nint value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetUInt32(nint self, out uint value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InvokeNoArgs(nint self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int UpdateSessionDisplaySettings(
            nint self,
            uint desktopWidth,
            uint desktopHeight,
            uint physicalWidth,
            uint physicalHeight,
            uint orientation,
            uint desktopScaleFactor,
            uint deviceScaleFactor);

        [ComImport]
        [Guid("302D8188-0052-4807-806A-362B628F9AC5")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMsRdpExtendedSettings
        {
            [PreserveSig]
            int SetProperty(
                [MarshalAs(UnmanagedType.BStr)] string propertyName,
                [In, MarshalAs(UnmanagedType.Struct)] ref object value);

            [PreserveSig]
            int GetProperty(
                [MarshalAs(UnmanagedType.BStr)] string propertyName,
                [MarshalAs(UnmanagedType.Struct)] out object value);
        }

        [DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
        private static extern int LoadTypeLibEx(
            string fileName,
            int registrationKind,
            [MarshalAs(UnmanagedType.Interface)] out ITypeLib typeLibrary);
    }
}

public sealed record RdpHostDiagnostic(string Code, string Message);
