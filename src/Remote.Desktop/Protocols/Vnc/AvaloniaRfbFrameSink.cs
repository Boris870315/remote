using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Remote.Infrastructure.Protocols.Vnc;

namespace Remote.Desktop.Protocols.Vnc;

public sealed class AvaloniaRfbFrameSink(Action<WriteableBitmap?> frameChanged) : IRfbFrameSink, IDisposable
{
    private readonly object _gate = new();
    private byte[] _pixels = [];
    private ushort _width;
    private ushort _height;
    private WriteableBitmap? _bitmap;

    public ValueTask DesktopSizeChangedAsync(ushort width, ushort height, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _width = width;
            _height = height;
            _pixels = new byte[checked(width * height * 4)];
        }

        return PublishAsync(cancellationToken);
    }

    public ValueTask RectangleUpdatedAsync(RfbRectangle rectangle, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (rectangle.X + rectangle.Width > _width || rectangle.Y + rectangle.Height > _height)
            {
                throw new InvalidDataException("The VNC update rectangle exceeds the desktop bounds.");
            }

            var sourceRowBytes = checked(rectangle.Width * 4);
            var destinationRowBytes = checked(_width * 4);
            for (var row = 0; row < rectangle.Height; row++)
            {
                rectangle.BgraPixels.AsSpan(row * sourceRowBytes, sourceRowBytes).CopyTo(
                    _pixels.AsSpan(((rectangle.Y + row) * destinationRowBytes) + (rectangle.X * 4), sourceRowBytes));
            }
        }

        return PublishAsync(cancellationToken);
    }

    public ValueTask RectangleCopiedAsync(RfbCopyRectangle rectangle, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (rectangle.X + rectangle.Width > _width || rectangle.Y + rectangle.Height > _height ||
                rectangle.SourceX + rectangle.Width > _width || rectangle.SourceY + rectangle.Height > _height)
            {
                throw new InvalidDataException("The VNC CopyRect operation exceeds the desktop bounds.");
            }

            var rowBytes = checked(rectangle.Width * 4);
            var desktopRowBytes = checked(_width * 4);
            var copiedPixels = new byte[checked(rowBytes * rectangle.Height)];
            for (var row = 0; row < rectangle.Height; row++)
            {
                _pixels.AsSpan(
                    ((rectangle.SourceY + row) * desktopRowBytes) + (rectangle.SourceX * 4),
                    rowBytes).CopyTo(copiedPixels.AsSpan(row * rowBytes, rowBytes));
            }

            for (var row = 0; row < rectangle.Height; row++)
            {
                copiedPixels.AsSpan(row * rowBytes, rowBytes).CopyTo(
                    _pixels.AsSpan(
                        ((rectangle.Y + row) * desktopRowBytes) + (rectangle.X * 4),
                        rowBytes));
            }
        }

        return PublishAsync(cancellationToken);
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private async ValueTask PublishAsync(CancellationToken cancellationToken)
    {
        byte[] snapshot;
        ushort width;
        ushort height;
        lock (_gate)
        {
            snapshot = _pixels.ToArray();
            width = _width;
            height = _height;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Opaque);
            }

            using var framebuffer = _bitmap.Lock();
            var sourceRowBytes = checked(width * 4);
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(
                    snapshot,
                    row * sourceRowBytes,
                    IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes),
                    sourceRowBytes);
            }

            frameChanged(_bitmap);
        });
    }
}
