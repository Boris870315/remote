using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using Avalonia;
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
    private const string MsRdpClient10NotSafeForScripting = "{A0C63C30-F08D-4AB4-907C-34905D770C7D}";
    private nint _window;
    private object? _rdpClient;
    private readonly TaskCompletionSource _hostReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DispatcherTimer? _connectionMonitor;
    private TaskCompletionSource? _connectionReady;
    private bool _hasConnected;
    private bool _disconnectRequested;
    private int _connectingTicks;
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
        var stage = "query-client-interface";
        try
        {
            var client = (IMsTscAxDispatch)clientObject;
            _disconnectRequested = false;
            _hasConnected = false;
            _connectingTicks = 0;
            _connectionReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TryDisconnect(clientObject);
            stage = "set-endpoint";
            client.Server = request.Endpoint.Host;
            client.UserName = request.Username ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(request.Domain))
            {
                client.Domain = request.Domain;
            }
            stage = "set-display";
            client.DesktopWidth = Math.Max(640, (int)Bounds.Width);
            client.DesktopHeight = Math.Max(480, (int)Bounds.Height);
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
            var advanced = client.AdvancedSettings;
            var permissions = RdpSessionPermissionPolicy.Resolve(request.AccessMode, request.Settings);
            stage = "set-security-and-redirection";
            SetComProperty(advanced, "RDPPort", request.Endpoint.IsDefaultPort ? 3389 : request.Endpoint.Port);
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
            client.Connect();
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
                var client = (IMsTscAxDispatch)_rdpClient;
                var connected = client.Connected;
                if (connected != 0)
                {
                    _hasConnected = true;
                    _connectionReady?.TrySetResult();
                    return;
                }
                if (!_hasConnected)
                {
                    _connectingTicks++;
                    var disconnectReason = TryGetExtendedDisconnectReason(_rdpClient);
                    if (disconnectReason > 2)
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
        ResizeNativeSurface(Bounds.Size);
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

        _window = CreateWindowExW(
            0,
            "AtlAxWin",
            MsRdpClient10NotSafeForScripting,
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

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        ResizeNativeSurface(finalSize);
        return arranged;
    }

    private void ResizeNativeSurface(Size size)
    {
        if (!OperatingSystem.IsWindows() || _window == nint.Zero) return;
        // Avalonia layout uses device-independent pixels while SetWindowPos
        // requires physical pixels. Without this conversion, a 200% Windows
        // display makes the RDP HWND exactly half the intended width/height.
        var renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var width = Math.Max(1, (int)Math.Ceiling(size.Width * renderScaling));
        var height = Math.Max(1, (int)Math.Ceiling(size.Height * renderScaling));
        _ = SetWindowPos(
            _window,
            nint.Zero,
            0,
            0,
            width,
            height,
            SwpNoZOrder | SwpNoActivate | SwpShowWindow);
        _ = InvalidateRect(_window, nint.Zero, false);
        _ = UpdateWindow(_window);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (OperatingSystem.IsWindows() && _window != nint.Zero)
        {
            _disconnectRequested = true;
            _connectionMonitor?.Stop();
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

    private static bool IsComInvocationException(Exception exception) =>
        exception is COMException or InvalidCastException or MissingMethodException or TargetException or TargetParameterCountException or ArgumentException ||
        exception is TargetInvocationException { InnerException: COMException };

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

        public static void SetSelectedMonitors(object client, int monitorIndex)
        {
            var extendedSettings = (IMsRdpExtendedSettings)client;
            object value = monitorIndex.ToString(CultureInfo.InvariantCulture);
            Marshal.ThrowExceptionForHR(extendedSettings.SetProperty("SelectedMonitors", ref value));
        }

        public static void SetUseMultimon(object client, bool enabled)
        {
            var typeLibraryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "mstscax.dll");
            var loadResult = LoadTypeLibEx(typeLibraryPath, RegkindNone, out var typeLibrary);
            Marshal.ThrowExceptionForHR(loadResult);

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

        private static int FindPropertySetterOffset(ITypeInfo typeInfo, string propertyName)
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
                        if (function.invkind is not INVOKEKIND.INVOKE_PROPERTYPUT) continue;
                        var names = new string[1];
                        typeInfo.GetNames(function.memid, names, names.Length, out var nameCount);
                        if (nameCount == 1 && string.Equals(names[0], propertyName, StringComparison.Ordinal))
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
                "The installed Microsoft RDP client does not publish the UseMultimon setting.");
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PutVariantBool(nint self, short enabled);

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
