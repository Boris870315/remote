using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
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

    public event Action<string>? UnexpectedlyDisconnected;

    public void SetViewOnly(bool viewOnly)
    {
        if (!OperatingSystem.IsWindows() || _window == nint.Zero) return;
        EnableWindow(_window, !viewOnly);
    }

    public void SetDisplayScaleMode(DisplayScaleMode scaleMode)
    {
        _displayScaleMode = scaleMode;
        if (!OperatingSystem.IsWindows() || _rdpClient is null) return;
        var advanced = GetComProperty(_rdpClient, "AdvancedSettings");
        TrySetComProperty(advanced, "SmartSizing", UsesSmartSizing(scaleMode));
        ResizeNativeSurface();
    }

    internal static bool UsesSmartSizing(DisplayScaleMode scaleMode) =>
        scaleMode is DisplayScaleMode.Fit or DisplayScaleMode.Fill;

    public void SetSessionVisible(bool visible)
    {
        IsVisible = visible;
        if (!OperatingSystem.IsWindows() || _window == nint.Zero) return;
        _ = ShowWindow(_window, visible ? SwShow : SwHide);
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
        try
        {
            // Invoke the scriptable IMsTscAx surface through IDispatch instead of
            // casting the RCW to a hand-written COM interface. Some Windows RDP
            // control revisions expose the correct automation members but .NET
            // cannot cast their canonical RCW to that private interface.
            _ = GetComProperty(clientObject, "Connected");
            _disconnectRequested = false;
            _hasConnected = false;
            _connectingTicks = 0;
            _connectionReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TryDisconnect(clientObject);
            stage = "set-endpoint";
            SetComProperty(clientObject, "Server", request.Endpoint.Host);
            SetComProperty(clientObject, "UserName", request.Username ?? string.Empty);
            // Explicitly clear the ActiveX domain when the operator did not
            // provide one. Leaving it untouched lets some Windows revisions
            // infer the destination computer name and display HOST\\username.
            SetComProperty(clientObject, "Domain", request.Domain ?? string.Empty);
            stage = "set-display";
            _displayScaleMode = request.Display.ScaleMode;
            var initialPixelSize = GetNativeClientPixelSize();
            SetComProperty(clientObject, "DesktopWidth", Math.Max(640, initialPixelSize.Width));
            SetComProperty(clientObject, "DesktopHeight", Math.Max(480, initialPixelSize.Height));
            var selectedMonitorIndex = Math.Max(0, request.Display.MonitorIndex ?? 0);
            _useMultimon = request.Display.MonitorSelection is Remote.Application.Connections.MonitorSelection.All ||
                selectedMonitorIndex > 0;
            if (_useMultimon)
            {
                stage = "enable-multiple-monitors";
                if (request.Display.MonitorSelection is Remote.Application.Connections.MonitorSelection.Single)
                {
                    RdpActiveXNativeSettings.SetSelectedMonitors(clientObject, selectedMonitorIndex);
                }
                RdpActiveXNativeSettings.SetUseMultimon(clientObject, true);
            }
            stage = "open-advanced-settings";
            var advanced = GetComProperty(clientObject, "AdvancedSettings");
            var permissions = RdpSessionPermissionPolicy.Resolve(request.AccessMode, request.Settings);
            stage = "set-security-and-redirection";
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
                stage = "enable-remote-credential-guard";
                RdpActiveXNativeSettings.SetExtendedBoolean(
                    clientObject, "RedirectedAuthentication", true);
            }
            if (!string.IsNullOrWhiteSpace(request.Settings.GatewayHost))
            {
                stage = "set-gateway";
                var transport = GetComProperty(clientObject, "TransportSettings4");
                SetComProperty(transport, "GatewayHostname", request.Settings.GatewayHost);
                SetComProperty(transport, "GatewayUsageMethod", 1u);
                SetComProperty(transport, "GatewayProfileUsageMethod", 1u);
            }
            if (!request.Settings.UseRemoteGuard && request.PasswordUtf8 is { Length: > 0 })
            {
                stage = "set-credential";
                var clearTextPassword = System.Text.Encoding.UTF8.GetString(request.PasswordUtf8.Span);
                var credentialSettings = TryGetComProperty(clientObject, "AdvancedSettings2") ?? advanced;
                if (!TrySetComProperty(credentialSettings, "ClearTextPassword", clearTextPassword))
                {
                    var passwordProvider = (IMsTscNonScriptable)clientObject;
                    var passwordResult = passwordProvider.SetClearTextPassword(clearTextPassword);
                    Marshal.ThrowExceptionForHR(passwordResult);
                }
            }
            stage = "connect";
            InvokeComMethod(clientObject, "Connect");
            StartConnectionMonitor();
            EnableWindow(_window, permissions.AcceptsInput);
            stage = "wait-for-connected-state";
            await _connectionReady.Task;
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
                var connected = Convert.ToInt16(
                    GetComProperty(_rdpClient, "Connected"),
                    CultureInfo.InvariantCulture);
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
                    var disconnectReason = TryGetExtendedDisconnectReason(_rdpClient);
                    if (connected == 0 && disconnectReason > 2)
                    {
                        _connectionMonitor?.Stop();
                        var rejected = new InvalidOperationException(
                            RdpDisconnectReasonFormatter.Format(disconnectReason));
                        rejected.Data["SafeDiagnostic"] =
                            $"Embedded RDP connection rejected; ExtendedDisconnectReason={disconnectReason}.";
                        _connectionReady?.TrySetException(rejected);
                        return;
                    }
                    if (_connectingTicks < 30) return;
                    _connectionMonitor?.Stop();
                    var timeout = new TimeoutException("RDP 連線逾時，請檢查主機、防火牆與帳號密碼");
                    timeout.Data["SafeDiagnostic"] = "Embedded RDP did not reach Connected state within 30 seconds.";
                    _connectionReady?.TrySetException(timeout);
                    return;
                }
                _connectionMonitor?.Stop();
                UnexpectedlyDisconnected?.Invoke("RDP 連線已中斷");
            }
            catch (Exception exception) when (IsComInvocationException(exception))
            {
                _connectionMonitor?.Stop();
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
                if (RdpActiveXNativeSettings.TryUpdateSessionDisplaySettings(_rdpClient, pixelSize))
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

        _ = OleInitialize(nint.Zero);
        if (!AtlAxWinInit())
        {
            throw new InvalidOperationException("Windows ATL ActiveX host initialization failed.");
        }

        nint unknown = nint.Zero;
        var result = unchecked((int)0x80004005);
        foreach (var classId in RdpClientClassIds)
        {
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
            if (_window == nint.Zero) continue;
            result = AtlAxGetControl(_window, out unknown);
            if (result >= 0 && unknown != nint.Zero) break;
            if (unknown != nint.Zero)
            {
                Marshal.Release(unknown);
                unknown = nint.Zero;
            }
            DestroyWindow(_window);
            _window = nint.Zero;
        }

        if (_window == nint.Zero || unknown == nint.Zero)
            throw new InvalidOperationException(
                $"Windows could not create a supported embedded RDP host (HRESULT=0x{result:X8}).");

        try
        {
            _rdpClient = Marshal.GetObjectForIUnknown(unknown);
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
            _connectionMonitor?.Stop();
            _displayResizeTimer?.Stop();
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

    private static bool IsComInvocationException(Exception exception) =>
        exception is COMException or InvalidCastException or MissingMethodException or TargetException or TargetParameterCountException or ArgumentException ||
        exception is TargetInvocationException { InnerException: { } inner } && IsComInvocationException(inner);

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
