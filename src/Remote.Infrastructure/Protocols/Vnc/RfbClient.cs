using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Remote.Protocols;

namespace Remote.Infrastructure.Protocols.Vnc;

/// <summary>A managed RFB 3.3/3.7/3.8 client with an input gate for View Only sessions.</summary>
public sealed class RfbClient(
    IRfbTransportFactory transportFactory,
    IRfbFrameSink frameSink) : IAsyncDisposable
{
    private const int RawEncoding = 0;
    private const int CopyRectEncoding = 1;
    private const int DesktopSizeEncoding = -223;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private RfbTransport? _transport;
    private SessionAccessMode _accessMode;
    private ushort _width;
    private ushort _height;

    public bool IsConnected => _transport is not null;

    public async Task<RfbServerInfo> ConnectAsync(
        RfbConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_transport is not null)
        {
            throw new InvalidOperationException("The VNC client is already connected.");
        }

        ValidateEndpoint(options.Endpoint);
        var port = options.Endpoint.IsDefaultPort ? 5900 : options.Endpoint.Port;
        var transport = await transportFactory
            .ConnectAsync(options.Endpoint.Host, port, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var serverInfo = await PerformHandshakeAsync(transport.Stream, options, cancellationToken)
                .ConfigureAwait(false);
            _transport = transport;
            _accessMode = options.AccessMode;
            _width = serverInfo.Width;
            _height = serverInfo.Height;
            await ConfigureFramebufferAsync(cancellationToken).ConfigureAwait(false);
            await frameSink.DesktopSizeChangedAsync(_width, _height, cancellationToken).ConfigureAwait(false);
            return serverInfo;
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var stream = GetStream();
        await RequestFramebufferUpdateAsync(false, cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            await ProcessServerMessageAsync(stream, cancellationToken).ConfigureAwait(false);
            await RequestFramebufferUpdateAsync(true, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ReceiveNextServerMessageAsync(CancellationToken cancellationToken = default) =>
        ProcessServerMessageAsync(GetStream(), cancellationToken);

    public async Task<bool> SendKeyAsync(
        uint keySym,
        bool isDown,
        CancellationToken cancellationToken = default)
    {
        if (_accessMode is SessionAccessMode.ViewOnly)
        {
            return false;
        }

        var message = new byte[8];
        message[0] = 4;
        message[1] = isDown ? (byte)1 : (byte)0;
        RfbBinary.WriteUInt32(message.AsSpan(4), keySym);
        await WriteAsync(message, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SendPointerAsync(
        byte buttonMask,
        ushort x,
        ushort y,
        CancellationToken cancellationToken = default)
    {
        if (_accessMode is SessionAccessMode.ViewOnly)
        {
            return false;
        }

        var message = new byte[6];
        message[0] = 5;
        message[1] = buttonMask;
        RfbBinary.WriteUInt16(message.AsSpan(2), x);
        RfbBinary.WriteUInt16(message.AsSpan(4), y);
        await WriteAsync(message, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
        }

        _writeLock.Dispose();
    }

    private static async Task<RfbServerInfo> PerformHandshakeAsync(
        Stream stream,
        RfbConnectionOptions options,
        CancellationToken cancellationToken)
    {
        var versionBytes = new byte[12];
        await RfbBinary.ReadExactlyAsync(stream, versionBytes, cancellationToken).ConfigureAwait(false);
        var serverVersion = ParseVersion(versionBytes);
        var negotiatedMinor = serverVersion.Minor >= 8 ? 8 : serverVersion.Minor >= 7 ? 7 : 3;
        var clientVersion = $"RFB 003.{negotiatedMinor:000}\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(clientVersion), cancellationToken).ConfigureAwait(false);

        byte requestedSecurityType;
        if (negotiatedMinor == 3)
        {
            var serverSecurityType = await RfbBinary.ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
            if (serverSecurityType == 0)
            {
                throw new RfbConnectionException(await ReadFailureReasonAsync(stream, cancellationToken).ConfigureAwait(false));
            }

            if (serverSecurityType > byte.MaxValue)
            {
                throw new RfbAuthenticationException($"Unsupported RFB 3.3 security type {serverSecurityType}.");
            }

            requestedSecurityType = SelectSecurityType([(byte)serverSecurityType], options.Password.IsEmpty);
        }
        else
        {
            var securityTypeCount = await RfbBinary.ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
            if (securityTypeCount == 0)
            {
                throw new RfbConnectionException(await ReadFailureReasonAsync(stream, cancellationToken).ConfigureAwait(false));
            }

            var securityTypes = new byte[securityTypeCount];
            await RfbBinary.ReadExactlyAsync(stream, securityTypes, cancellationToken).ConfigureAwait(false);
            requestedSecurityType = SelectSecurityType(securityTypes, options.Password.IsEmpty);
            await stream.WriteAsync(new byte[] { requestedSecurityType }, cancellationToken).ConfigureAwait(false);
        }

        if (requestedSecurityType == 2)
        {
            var challenge = new byte[16];
            await RfbBinary.ReadExactlyAsync(stream, challenge, cancellationToken).ConfigureAwait(false);
            var response = VncAuthentication.EncryptChallenge(challenge, options.Password.Span);
            try
            {
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(challenge);
                CryptographicOperations.ZeroMemory(response);
            }
        }

        // RFB 3.3 and 3.7 omit SecurityResult when the selected type is None.
        if (requestedSecurityType != 1 || negotiatedMinor >= 8)
        {
            var securityResult = await RfbBinary.ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
            if (securityResult != 0)
            {
                var reason = negotiatedMinor >= 8
                    ? await ReadFailureReasonAsync(stream, cancellationToken).ConfigureAwait(false)
                    : "VNC authentication failed.";
                throw new RfbAuthenticationException(reason);
            }
        }

        await stream.WriteAsync(new byte[] { options.SharedSession ? (byte)1 : (byte)0 }, cancellationToken)
            .ConfigureAwait(false);
        var width = await RfbBinary.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
        var height = await RfbBinary.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
        var pixelFormat = new byte[16];
        await RfbBinary.ReadExactlyAsync(stream, pixelFormat, cancellationToken).ConfigureAwait(false);
        var nameLength = await RfbBinary.ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (nameLength > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("The VNC desktop name is unreasonably large.");
        }

        var nameBytes = new byte[checked((int)nameLength)];
        await RfbBinary.ReadExactlyAsync(stream, nameBytes, cancellationToken).ConfigureAwait(false);
        return new(width, height, Encoding.UTF8.GetString(nameBytes));
    }

    private async Task ConfigureFramebufferAsync(CancellationToken cancellationToken)
    {
        var pixelFormat = new byte[20];
        pixelFormat[0] = 0;
        pixelFormat[4] = 32;
        pixelFormat[5] = 24;
        pixelFormat[6] = 0;
        pixelFormat[7] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(pixelFormat.AsSpan(8), 255);
        BinaryPrimitives.WriteUInt16BigEndian(pixelFormat.AsSpan(10), 255);
        BinaryPrimitives.WriteUInt16BigEndian(pixelFormat.AsSpan(12), 255);
        pixelFormat[14] = 16;
        pixelFormat[15] = 8;
        pixelFormat[16] = 0;
        await WriteAsync(pixelFormat, cancellationToken).ConfigureAwait(false);

        var encodings = new byte[16];
        encodings[0] = 2;
        RfbBinary.WriteUInt16(encodings.AsSpan(2), 3);
        RfbBinary.WriteUInt32(encodings.AsSpan(4), unchecked((uint)RawEncoding));
        RfbBinary.WriteUInt32(encodings.AsSpan(8), unchecked((uint)CopyRectEncoding));
        RfbBinary.WriteUInt32(encodings.AsSpan(12), unchecked((uint)DesktopSizeEncoding));
        await WriteAsync(encodings, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessServerMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var messageType = await RfbBinary.ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
        switch (messageType)
        {
            case 0:
                await ProcessFramebufferUpdateAsync(stream, cancellationToken).ConfigureAwait(false);
                break;
            case 1:
                await SkipColorMapAsync(stream, cancellationToken).ConfigureAwait(false);
                break;
            case 2:
                break;
            case 3:
                await SkipServerCutTextAsync(stream, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException($"Unsupported RFB server message type {messageType}.");
        }
    }

    private async Task ProcessFramebufferUpdateAsync(Stream stream, CancellationToken cancellationToken)
    {
        _ = await RfbBinary.ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
        var rectangleCount = await RfbBinary.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < rectangleCount; index++)
        {
            var x = await RfbBinary.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
            var y = await RfbBinary.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
            var width = await RfbBinary.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
            var height = await RfbBinary.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
            var encoding = await RfbBinary.ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
            if (encoding == DesktopSizeEncoding)
            {
                _width = width;
                _height = height;
                await frameSink.DesktopSizeChangedAsync(width, height, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (encoding == CopyRectEncoding)
            {
                var sourceX = await RfbBinary.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
                var sourceY = await RfbBinary.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
                await frameSink.RectangleCopiedAsync(
                    new RfbCopyRectangle(x, y, width, height, sourceX, sourceY),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (encoding != RawEncoding)
            {
                throw new NotSupportedException($"RFB encoding {encoding} is not supported yet.");
            }

            var byteCount = checked(width * height * 4);
            var pixels = new byte[byteCount];
            await RfbBinary.ReadExactlyAsync(stream, pixels, cancellationToken).ConfigureAwait(false);
            for (var pixel = 3; pixel < pixels.Length; pixel += 4)
            {
                pixels[pixel] = 255;
            }

            await frameSink.RectangleUpdatedAsync(
                new RfbRectangle(x, y, width, height, pixels),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
    {
        var message = new byte[10];
        message[0] = 3;
        message[1] = incremental ? (byte)1 : (byte)0;
        RfbBinary.WriteUInt16(message.AsSpan(6), _width);
        RfbBinary.WriteUInt16(message.AsSpan(8), _height);
        await WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await GetStream().WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private Stream GetStream() => _transport?.Stream
        ?? throw new InvalidOperationException("The VNC client is not connected.");

    private static byte SelectSecurityType(ReadOnlySpan<byte> available, bool passwordIsEmpty)
    {
        if (!passwordIsEmpty && available.Contains((byte)2))
        {
            return 2;
        }

        if (passwordIsEmpty && available.Contains((byte)1))
        {
            return 1;
        }

        throw new RfbAuthenticationException(
            passwordIsEmpty
                ? "The VNC server requires authentication."
                : "The VNC server does not offer classic VNC Authentication; refusing to downgrade to no authentication.");
    }

    private static (int Major, int Minor) ParseVersion(ReadOnlySpan<byte> versionBytes)
    {
        var version = Encoding.ASCII.GetString(versionBytes);
        if (versionBytes.Length != 12 ||
            !version.StartsWith("RFB ", StringComparison.Ordinal) ||
            version[7] != '.' || version[11] != '\n' ||
            !int.TryParse(version.AsSpan(4, 3), out var major) ||
            !int.TryParse(version.AsSpan(8, 3), out var minor) ||
            major != 3 || minor < 3)
        {
            throw new NotSupportedException($"Unsupported RFB protocol version '{version.Trim()}'.");
        }

        return (major, minor);
    }

    private static async Task<string> ReadFailureReasonAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = await RfbBinary.ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (length > 1024 * 1024)
        {
            throw new InvalidDataException("The VNC failure reason is unreasonably large.");
        }

        var bytes = new byte[length];
        await RfbBinary.ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task SkipColorMapAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        await RfbBinary.ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var colorCount = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3));
        var colors = new byte[checked(colorCount * 6)];
        await RfbBinary.ReadExactlyAsync(stream, colors, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SkipServerCutTextAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[7];
        await RfbBinary.ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(3));
        if (length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("The VNC clipboard payload is unreasonably large.");
        }

        var payload = new byte[checked((int)length)];
        await RfbBinary.ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (!string.Equals(endpoint.Scheme, "vnc", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(endpoint.Scheme, "rfb", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A vnc or rfb endpoint is required.", nameof(endpoint));
        }
    }
}

public class RfbConnectionException(string message) : IOException(message);

public sealed class RfbAuthenticationException(string message) : RfbConnectionException(message);
