using System.Buffers.Binary;
using System.Text;
using Remote.Infrastructure.Protocols.Vnc;
using Remote.Protocols;

namespace Remote.Application.Tests;

public sealed class RfbClientTests
{
    [Fact]
    public async Task Connect_WhenTransportDoesNotRespond_TimesOut()
    {
        await using var client = new RfbClient(new NeverConnectingTransportFactory(), new RecordingFrameSink());

        await Assert.ThrowsAsync<TimeoutException>(() => client.ConnectAsync(new RfbConnectionOptions
        {
            Endpoint = new Uri("vnc://server.example"),
            ConnectTimeout = TimeSpan.FromMilliseconds(20),
        }));
    }

    [Fact]
    public async Task Connect_Rfb38WithoutAuthentication_ConfiguresRawFramebuffer()
    {
        var stream = new ScriptedDuplexStream(BuildServerScript());
        var sink = new RecordingFrameSink();
        await using var client = new RfbClient(new ScriptedTransportFactory(stream), sink);

        var server = await client.ConnectAsync(new RfbConnectionOptions
        {
            Endpoint = new Uri("vnc://server.example:5901"),
            AccessMode = SessionAccessMode.ViewOnly,
        });

        Assert.Equal((ushort)800, server.Width);
        Assert.Equal((ushort)600, server.Height);
        Assert.Equal("Test Desktop", server.Name);
        Assert.Equal((ushort)800, sink.Width);
        Assert.Equal("RFB 003.008\n", Encoding.ASCII.GetString(stream.Written[..12]));
        Assert.Equal(1, stream.Written[12]);
        Assert.Equal(1, stream.Written[13]);
        Assert.Equal(0, stream.Written[14]);
        Assert.Equal(32, stream.Written[18]);
    }

    [Fact]
    public async Task ViewOnly_BlocksKeyboardPointerAndClipboardBeforeTransportWrite()
    {
        var stream = new ScriptedDuplexStream(BuildServerScript());
        await using var client = new RfbClient(new ScriptedTransportFactory(stream), new RecordingFrameSink());
        await client.ConnectAsync(new RfbConnectionOptions
        {
            Endpoint = new Uri("vnc://server.example"),
            AccessMode = SessionAccessMode.ViewOnly,
        });
        var bytesBeforeInput = stream.Written.Length;

        var keySent = await client.SendKeyAsync(0x41, true);
        var pointerSent = await client.SendPointerAsync(1, 20, 30);
        var clipboardSent = await client.SendClipboardTextAsync("secret");

        Assert.False(keySent);
        Assert.False(pointerSent);
        Assert.False(clipboardSent);
        Assert.Equal(bytesBeforeInput, stream.Written.Length);
    }

    [Fact]
    public async Task ReceiveRawRectangle_ConvertsUnusedByteToOpaqueAlpha()
    {
        var update = new byte[]
        {
            0, 0, 0, 1,
            0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0,
            10, 20, 30, 0,
        };
        var stream = new ScriptedDuplexStream(BuildServerScript(update));
        var sink = new RecordingFrameSink();
        await using var client = new RfbClient(new ScriptedTransportFactory(stream), sink);
        await client.ConnectAsync(new RfbConnectionOptions { Endpoint = new Uri("vnc://server.example") });

        await client.ReceiveNextServerMessageAsync();

        var rectangle = Assert.Single(sink.Rectangles);
        Assert.Equal([10, 20, 30, 255], rectangle.BgraPixels);
    }

    [Fact]
    public async Task ReceiveCopyRect_ForwardsOverlappingFramebufferCopy()
    {
        var update = new byte[]
        {
            0, 0, 0, 1,
            0, 10, 0, 20, 0, 30, 0, 40, 0, 0, 0, 1,
            0, 2, 0, 3,
        };
        var stream = new ScriptedDuplexStream(BuildServerScript(update));
        var sink = new RecordingFrameSink();
        await using var client = new RfbClient(new ScriptedTransportFactory(stream), sink);
        await client.ConnectAsync(new RfbConnectionOptions { Endpoint = new Uri("vnc://server.example") });

        await client.ReceiveNextServerMessageAsync();

        var copy = Assert.Single(sink.Copies);
        Assert.Equal(new RfbCopyRectangle(10, 20, 30, 40, 2, 3), copy);
    }

    [Fact]
    public async Task ReceiveHextile_DecodesBackgroundAndColoredSubrectangle()
    {
        var update = new byte[]
        {
            0, 0, 0, 1,
            0, 0, 0, 0, 0, 4, 0, 3, 0, 0, 0, 5,
            2 | 8 | 16,
            10, 20, 30, 0,
            1,
            40, 50, 60, 0,
            0x11,
            0x10,
        };
        var stream = new ScriptedDuplexStream(BuildServerScript(update));
        var sink = new RecordingFrameSink();
        await using var client = new RfbClient(new ScriptedTransportFactory(stream), sink);
        await client.ConnectAsync(new RfbConnectionOptions { Endpoint = new Uri("vnc://server.example") });

        await client.ReceiveNextServerMessageAsync();

        var rectangle = Assert.Single(sink.Rectangles);
        Assert.Equal([10, 20, 30, 255], rectangle.BgraPixels[..4]);
        var coloredPixelOffset = ((1 * 4) + 1) * 4;
        Assert.Equal([40, 50, 60, 255], rectangle.BgraPixels[coloredPixelOffset..(coloredPixelOffset + 4)]);
        Assert.Equal([40, 50, 60, 255], rectangle.BgraPixels[(coloredPixelOffset + 4)..(coloredPixelOffset + 8)]);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public async Task Connect_LegacyRfbWithoutAuthentication_NegotiatesCompatibleVersion(int minor)
    {
        var stream = new ScriptedDuplexStream(BuildServerScript(minor: minor));
        await using var client = new RfbClient(new ScriptedTransportFactory(stream), new RecordingFrameSink());

        var server = await client.ConnectAsync(new RfbConnectionOptions { Endpoint = new Uri("vnc://legacy.example") });

        Assert.Equal("Test Desktop", server.Name);
        Assert.Equal($"RFB 003.{minor:000}\n", Encoding.ASCII.GetString(stream.Written[..12]));
    }

    [Fact]
    public async Task Connect_WithPassword_UsesVncAuthenticationChallenge()
    {
        var challenge = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var stream = new ScriptedDuplexStream(BuildServerScript(securityType: 2, challenge: challenge));
        await using var client = new RfbClient(new ScriptedTransportFactory(stream), new RecordingFrameSink());

        await client.ConnectAsync(new RfbConnectionOptions
        {
            Endpoint = new Uri("vnc://secured.example"),
            Password = "password"u8.ToArray(),
        });

        Assert.Equal(2, stream.Written[12]);
        Assert.Equal(16, stream.Written[13..29].Length);
        Assert.NotEqual(new byte[16], stream.Written[13..29]);
    }

    [Fact]
    public async Task Connect_WithPassword_RefusesNoAuthenticationDowngrade()
    {
        var stream = new ScriptedDuplexStream(BuildServerScript());
        await using var client = new RfbClient(new ScriptedTransportFactory(stream), new RecordingFrameSink());

        var error = await Assert.ThrowsAsync<RfbAuthenticationException>(() => client.ConnectAsync(
            new RfbConnectionOptions
            {
                Endpoint = new Uri("vnc://unsafe.example"),
                Password = "secret"u8.ToArray(),
            }));

        Assert.Contains("refusing to downgrade", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InteractiveMode_WritesKeyboardAndPointerMessages()
    {
        var stream = new ScriptedDuplexStream(BuildServerScript());
        await using var client = new RfbClient(new ScriptedTransportFactory(stream), new RecordingFrameSink());
        await client.ConnectAsync(new RfbConnectionOptions { Endpoint = new Uri("vnc://server.example") });
        var inputOffset = stream.Written.Length;

        Assert.True(await client.SendKeyAsync(0x41, true));
        Assert.True(await client.SendPointerAsync(1, 20, 30));

        Assert.Equal([4, 1, 0, 0, 0, 0, 0, 0x41], stream.Written[inputOffset..(inputOffset + 8)]);
        Assert.Equal([5, 1, 0, 20, 0, 30], stream.Written[(inputOffset + 8)..]);
    }

    [Fact]
    public async Task InteractiveMode_WritesClientClipboardText()
    {
        var stream = new ScriptedDuplexStream(BuildServerScript());
        await using var client = new RfbClient(new ScriptedTransportFactory(stream), new RecordingFrameSink());
        await client.ConnectAsync(new RfbConnectionOptions { Endpoint = new Uri("vnc://server.example") });
        var inputOffset = stream.Written.Length;

        Assert.True(await client.SendClipboardTextAsync("café"));

        Assert.Equal([6, 0, 0, 0, 0, 0, 0, 4, 99, 97, 102, 233], stream.Written[inputOffset..]);
    }

    [Fact]
    public async Task ReceiveServerClipboardText_RaisesDecodedText()
    {
        var serverCutText = new byte[] { 3, 0, 0, 0, 0, 0, 0, 4, 99, 97, 102, 233 };
        var stream = new ScriptedDuplexStream(BuildServerScript(serverCutText));
        await using var client = new RfbClient(new ScriptedTransportFactory(stream), new RecordingFrameSink());
        string? received = null;
        client.ServerClipboardTextReceived += text => received = text;
        await client.ConnectAsync(new RfbConnectionOptions { Endpoint = new Uri("vnc://server.example") });

        await client.ReceiveNextServerMessageAsync();

        Assert.Equal("café", received);
    }

    private static byte[] BuildServerScript(
        byte[]? trailingMessage = null,
        int minor = 8,
        byte securityType = 1,
        byte[]? challenge = null)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes($"RFB 003.{minor:000}\n"));
        if (minor == 3)
        {
            WriteUInt32(stream, securityType);
        }
        else
        {
            stream.Write([1, securityType]);
        }

        if (securityType == 2)
        {
            stream.Write(challenge ?? new byte[16]);
        }

        if (securityType == 2 || minor >= 8)
        {
            WriteUInt32(stream, 0);
        }
        WriteUInt16(stream, 800);
        WriteUInt16(stream, 600);
        stream.Write(new byte[16]);
        var name = "Test Desktop"u8.ToArray();
        WriteUInt32(stream, (uint)name.Length);
        stream.Write(name);
        if (trailingMessage is not null)
        {
            stream.Write(trailingMessage);
        }

        return stream.ToArray();
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed class ScriptedTransportFactory(ScriptedDuplexStream stream) : IRfbTransportFactory
    {
        public Task<RfbTransport> ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
            Task.FromResult(new RfbTransport(stream, new NoopDisposable()));
    }

    private sealed class NeverConnectingTransportFactory : IRfbTransportFactory
    {
        public async Task<RfbTransport> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after cancellation.");
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class RecordingFrameSink : IRfbFrameSink
    {
        public ushort Width { get; private set; }

        public ushort Height { get; private set; }

        public List<RfbRectangle> Rectangles { get; } = [];

        public List<RfbCopyRectangle> Copies { get; } = [];

        public ValueTask DesktopSizeChangedAsync(ushort width, ushort height, CancellationToken cancellationToken)
        {
            Width = width;
            Height = height;
            return ValueTask.CompletedTask;
        }

        public ValueTask RectangleUpdatedAsync(RfbRectangle rectangle, CancellationToken cancellationToken)
        {
            Rectangles.Add(rectangle);
            return ValueTask.CompletedTask;
        }

        public ValueTask RectangleCopiedAsync(RfbCopyRectangle rectangle, CancellationToken cancellationToken)
        {
            Copies.Add(rectangle);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedDuplexStream(byte[] script) : Stream
    {
        private readonly MemoryStream _reads = new(script, writable: false);
        private readonly MemoryStream _writes = new();

        public byte[] Written => _writes.ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _writes.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _reads.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _writes.Write(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _reads.ReadAsync(buffer, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _writes.WriteAsync(buffer, cancellationToken);
    }
}
