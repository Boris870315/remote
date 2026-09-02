using Remote.Protocols;

namespace Remote.Infrastructure.Protocols.Vnc;

public sealed record RfbConnectionOptions
{
    public required Uri Endpoint { get; init; }

    public ReadOnlyMemory<byte> Password { get; init; }

    public SessionAccessMode AccessMode { get; init; } = SessionAccessMode.Interactive;

    public bool SharedSession { get; init; } = true;
}

public sealed record RfbServerInfo(ushort Width, ushort Height, string Name);

public sealed record RfbRectangle(ushort X, ushort Y, ushort Width, ushort Height, byte[] BgraPixels);

public sealed record RfbCopyRectangle(ushort X, ushort Y, ushort Width, ushort Height, ushort SourceX, ushort SourceY);

public interface IRfbFrameSink
{
    ValueTask DesktopSizeChangedAsync(ushort width, ushort height, CancellationToken cancellationToken);

    ValueTask RectangleUpdatedAsync(RfbRectangle rectangle, CancellationToken cancellationToken);

    ValueTask RectangleCopiedAsync(RfbCopyRectangle rectangle, CancellationToken cancellationToken);
}
