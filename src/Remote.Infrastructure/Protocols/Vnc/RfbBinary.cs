using System.Buffers.Binary;

namespace Remote.Infrastructure.Protocols.Vnc;

internal static class RfbBinary
{
    public static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The VNC server closed the connection unexpectedly.");
            }

            offset += read;
        }
    }

    public static async ValueTask<byte> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return buffer[0];
    }

    public static async ValueTask<ushort> ReadUInt16Async(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[2];
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16BigEndian(buffer);
    }

    public static async ValueTask<uint> ReadUInt32Async(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4];
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    public static async ValueTask<int> ReadInt32Async(Stream stream, CancellationToken cancellationToken) =>
        unchecked((int)await ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false));

    public static void WriteUInt16(Span<byte> buffer, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);

    public static void WriteUInt32(Span<byte> buffer, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
}
