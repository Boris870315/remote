using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Remote.Infrastructure.Protocols.Rdp;

namespace Remote.Desktop.Protocols.Rdp;

/// <summary>Managed owner for one in-process FreeRDP session on macOS.</summary>
internal sealed class MacOsFreeRdpSession : IAsyncDisposable
{
    private const string LibraryName = "remote-freerdp";
    private readonly FrameCallback _frameCallback;
    private readonly StateCallback _stateCallback;
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _session;
    private Task<uint>? _connectionTask;
    private bool _disposing;

    public event Action<byte[], int, int, int, int, int, int>? FrameReceived;
    public event Action<uint, uint, string>? StateChanged;

    public bool SendMouse(ushort x, ushort y, byte buttonMask) =>
        _session != nint.Zero && SessionSendMouse(_session, x, y, buttonMask) == 0;

    public bool SendWheel(ushort x, ushort y, short delta) =>
        _session != nint.Zero && SessionSendWheel(_session, x, y, delta) == 0;

    public bool SendKey(uint virtualKey, bool down) =>
        _session != nint.Zero && SessionSendKey(_session, virtualKey, down ? (byte)1 : (byte)0) == 0;

    public bool Resize(int width, int height) => _session != nint.Zero &&
        SessionResize(_session, checked((uint)width), checked((uint)height)) == 0;

    public MacOsFreeRdpSession()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        _frameCallback = HandleFrame;
        _stateCallback = HandleState;
        try
        {
            if (GetCapabilities(out var capabilities) != 0 || capabilities.AbiVersion != 3 ||
                capabilities.SupportsFramebuffer == 0)
                throw new InvalidOperationException("內嵌 FreeRDP bridge 版本不相容，請重新建置應用程式。");
            _session = SessionNew();
        }
        catch (DllNotFoundException exception)
        {
            throw new InvalidOperationException(
                "找不到 macOS FreeRDP runtime。請安裝 FreeRDP 或使用包含 native runtime 的 Remote 套件。", exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new InvalidOperationException("內嵌 FreeRDP bridge 不完整，請重新建置應用程式。", exception);
        }
        if (_session == nint.Zero) throw new InvalidOperationException("無法建立 FreeRDP 工作階段。");
    }

    public Task ConnectAsync(RdpExternalLaunchRequest request, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_connectionTask is not null) throw new InvalidOperationException("FreeRDP 工作階段已啟動。");
        var config = new NativeConfig(request, width, height, _frameCallback, _stateCallback);
        _connectionTask = Task.Run(() =>
        {
            using (config)
            {
                return SessionConnect(_session, in config.Value);
            }
        });
        return AwaitConnectionAsync(_connectionTask, _connected.Task,
            request.Settings.CertificatePolicy);
    }

    private async Task AwaitConnectionAsync(Task<uint> connection, Task connected,
        RdpCertificatePolicy certificatePolicy)
    {
        if (await Task.WhenAny(connection, connected).ConfigureAwait(false) == connected)
        {
            await connected.ConfigureAwait(false);
            return;
        }

        var result = await connection.ConfigureAwait(false);
        var nativeMessage = Marshal.PtrToStringUTF8(SessionLastError(_session));
        if (result == 0x00020008 && certificatePolicy is RdpCertificatePolicy.RequireTrusted)
        {
            throw new InvalidOperationException(
                "TLS 憑證不受信任。此 Windows 主機通常使用自簽 RDP 憑證；請編輯連線，將憑證政策改為 PromptOnUntrusted，僅在本次工作階段接受後再連線。");
        }
        throw new InvalidOperationException(DescribeError(result, nativeMessage));
    }

    internal static string DescribeError(uint errorCode, string? nativeMessage)
    {
        var stage = nativeMessage switch
        {
            { } text when text.Contains("event loop has no event handles", StringComparison.OrdinalIgnoreCase) =>
                "RDP 事件迴圈沒有可用的事件控制代碼",
            { } text when text.Contains("event wait failed", StringComparison.OrdinalIgnoreCase) =>
                "RDP 事件等待失敗",
            { } text when text.Contains("event processing failed", StringComparison.OrdinalIgnoreCase) =>
                "RDP 事件處理失敗",
            { } text when text.Contains("server requested disconnect", StringComparison.OrdinalIgnoreCase) =>
                "遠端主機要求中斷 RDP 工作階段",
            _ => null
        };

        var reason = stage ?? errorCode switch
        {
            0x00020004 or 0x00020005 => "無法解析遠端主機名稱",
            0x00020006 => "無法建立 RDP 連線",
            0x00020008 => "TLS 連線失敗",
            0x0002000C => "安全性協商失敗",
            0x0002000D => "傳輸層連線失敗",
            0x0002000E => "使用者密碼已過期",
            0x0002000F or 0x00020013 => "使用者必須變更密碼後才能登入",
            0x00020012 => "使用者帳號已停用",
            0x00020014 => "登入失敗，請檢查帳號、密碼與網域",
            0x00020015 => "密碼錯誤",
            0x00020018 => "使用者帳號已鎖定",
            0x0002001B => "缺少登入帳號或密碼",
            0x0002001C => "等待遠端桌面啟用逾時",
            _ => "RDP 工作階段失敗"
        };

        var details = Regex.Match(nativeMessage ?? string.Empty,
            @"serverError=0x(?<server>[0-9a-fA-F]{8}); ultimatum=(?<ultimatum>-?\d+)");
        return details.Success
            ? $"{reason}（HRESULT=0x{errorCode:X8}；伺服器錯誤=0x{details.Groups["server"].Value}；中斷原因={details.Groups["ultimatum"].Value}）"
            : $"{reason}（HRESULT=0x{errorCode:X8}）";
    }

    private void HandleFrame(nint state, nint pixels, uint width, uint height, uint stride,
        uint dirtyX, uint dirtyY, uint dirtyWidth, uint dirtyHeight)
    {
        if (pixels == nint.Zero || width == 0 || height == 0 || stride == 0 ||
            dirtyWidth == 0 || dirtyHeight == 0) return;
        var handler = FrameReceived;
        if (handler is null) return;
        var packedStride = checked((int)dirtyWidth * 4);
        var length = checked(packedStride * (int)dirtyHeight);
        var copy = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            for (var row = 0; row < dirtyHeight; row++)
            {
                var source = IntPtr.Add(pixels,
                    checked((int)(((dirtyY + row) * stride) + (dirtyX * 4))));
                Marshal.Copy(source, copy, checked((int)row * packedStride), packedStride);
            }
            handler(copy, checked((int)width), checked((int)height),
                checked((int)dirtyX), checked((int)dirtyY), packedStride,
                checked((int)dirtyHeight));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(copy);
            throw;
        }
    }

    private void HandleState(nint state, uint stateCode, uint errorCode, nint message)
    {
        var text = Marshal.PtrToStringUTF8(message) ?? string.Empty;
        if (stateCode == 1) _connected.TrySetResult();
        if (!_disposing || stateCode != 2) StateChanged?.Invoke(stateCode, errorCode, text);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session == nint.Zero) return;
        _disposing = true;
        SessionDisconnect(_session);
        if (_connectionTask is not null)
        {
            try { await _connectionTask.ConfigureAwait(false); } catch (InvalidOperationException) { }
        }
        SessionFree(_session);
        _session = nint.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FrameCallback(nint state, nint pixels, uint width, uint height, uint stride,
        uint dirtyX, uint dirtyY, uint dirtyWidth, uint dirtyHeight);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StateCallback(nint state, uint stateCode, uint errorCode, nint message);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeConfigValue
    {
        public nint Hostname, Username, Password, Domain;
        public ushort Port;
        public uint Width, Height;
        public byte ViewOnly, AllowUntrustedCertificate, UseAllMonitors;
        public byte RedirectClipboard, RedirectPrinters, RedirectDrives;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public byte[] Reserved;
        public nint CallbackState;
        public FrameCallback Frame;
        public StateCallback State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCapabilities
    {
        public uint AbiVersion, FreeRdpMajor, FreeRdpMinor, FreeRdpRevision;
        public byte SupportsFramebuffer, SupportsDynamicResolution;
        private byte Reserved0, Reserved1, Reserved2, Reserved3, Reserved4, Reserved5;
    }

    private sealed class NativeConfig : IDisposable
    {
        public NativeConfigValue Value;
        public NativeConfig(RdpExternalLaunchRequest request, int width, int height,
            FrameCallback frame, StateCallback state)
        {
            Value = new NativeConfigValue
            {
                Hostname = Marshal.StringToCoTaskMemUTF8(request.Endpoint.Host),
                Port = (ushort)(request.Endpoint.IsDefaultPort ? 3389 : request.Endpoint.Port),
                Username = Marshal.StringToCoTaskMemUTF8(request.Username ?? string.Empty),
                Password = CopySecret(request.PasswordUtf8.Span),
                Domain = Marshal.StringToCoTaskMemUTF8(request.Domain ?? string.Empty),
                Width = (uint)Math.Max(640, width), Height = (uint)Math.Max(480, height),
                ViewOnly = request.AccessMode is Remote.Protocols.SessionAccessMode.ViewOnly ? (byte)1 : (byte)0,
                AllowUntrustedCertificate = request.Settings.CertificatePolicy is RdpCertificatePolicy.PromptOnUntrusted ? (byte)1 : (byte)0,
                UseAllMonitors = request.Display.MonitorSelection is Remote.Application.Connections.MonitorSelection.All ? (byte)1 : (byte)0,
                RedirectClipboard = request.Settings.RedirectClipboard ? (byte)1 : (byte)0,
                RedirectPrinters = request.Settings.RedirectPrinters ? (byte)1 : (byte)0,
                RedirectDrives = request.Settings.RedirectDrives ? (byte)1 : (byte)0,
                Reserved = new byte[2], Frame = frame, State = state,
            };
        }
        public void Dispose()
        {
            Marshal.ZeroFreeCoTaskMemUTF8(Value.Password);
            Marshal.FreeCoTaskMem(Value.Hostname); Marshal.FreeCoTaskMem(Value.Username); Marshal.FreeCoTaskMem(Value.Domain);
        }


        private static nint CopySecret(ReadOnlySpan<byte> secret)
        {
            var pointer = Marshal.AllocCoTaskMem(secret.Length + 1);
            var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, secret.Length));
            try
            {
                secret.CopyTo(rented);
                if (!secret.IsEmpty) Marshal.Copy(rented, 0, pointer, secret.Length);
                Marshal.WriteByte(pointer, secret.Length, 0);
                return pointer;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(rented);
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    [DllImport(LibraryName, EntryPoint = "remote_rdp_get_capabilities")] private static extern uint GetCapabilities(out NativeCapabilities capabilities);
    [DllImport(LibraryName, EntryPoint = "remote_rdp_session_new")] private static extern nint SessionNew();
    [DllImport(LibraryName, EntryPoint = "remote_rdp_session_free")] private static extern void SessionFree(nint session);
    [DllImport(LibraryName, EntryPoint = "remote_rdp_session_last_error")] private static extern nint SessionLastError(nint session);
    [DllImport(LibraryName, EntryPoint = "remote_rdp_session_connect")] private static extern uint SessionConnect(nint session, in NativeConfigValue config);
    [DllImport(LibraryName, EntryPoint = "remote_rdp_session_disconnect")] private static extern void SessionDisconnect(nint session);
    [DllImport(LibraryName, EntryPoint = "remote_rdp_session_send_mouse")] private static extern uint SessionSendMouse(nint session, ushort x, ushort y, byte buttonMask);
    [DllImport(LibraryName, EntryPoint = "remote_rdp_session_send_wheel")] private static extern uint SessionSendWheel(nint session, ushort x, ushort y, short delta);
    [DllImport(LibraryName, EntryPoint = "remote_rdp_session_send_key")] private static extern uint SessionSendKey(nint session, uint virtualKey, byte down);
    [DllImport(LibraryName, EntryPoint = "remote_rdp_session_resize")] private static extern uint SessionResize(nint session, uint width, uint height);
}
