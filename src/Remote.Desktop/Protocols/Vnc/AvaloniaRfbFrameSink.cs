using System.Buffers;
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
    private bool _framebufferUpdateInProgress;
    private PendingFrame? _pendingFrame;
    private bool _renderScheduled;
    private bool _disposed;

    public ValueTask FramebufferUpdateStartedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _framebufferUpdateInProgress = true;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DesktopSizeChangedAsync(ushort width, ushort height, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _width = width;
            _height = height;
            _pixels = new byte[checked(width * height * 4)];
        }

        return PublishUnlessUpdatingAsync(cancellationToken);
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

        return PublishUnlessUpdatingAsync(cancellationToken);
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

        return PublishUnlessUpdatingAsync(cancellationToken);
    }

    public ValueTask FramebufferUpdateCompletedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _framebufferUpdateInProgress = false;
        }

        return PublishAsync(cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_pendingFrame is { } pending)
            {
                ArrayPool<byte>.Shared.Return(pending.Pixels);
                _pendingFrame = null;
            }
        }
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private ValueTask PublishAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scheduleRender = false;
        lock (_gate)
        {
            if (_disposed || _pixels.Length == 0)
            {
                return ValueTask.CompletedTask;
            }

            var snapshot = ArrayPool<byte>.Shared.Rent(_pixels.Length);
            _pixels.AsSpan().CopyTo(snapshot);
            if (_pendingFrame is { } superseded)
            {
                ArrayPool<byte>.Shared.Return(superseded.Pixels);
            }
            _pendingFrame = new PendingFrame(snapshot, _width, _height);
            if (!_renderScheduled)
            {
                _renderScheduled = true;
                scheduleRender = true;
            }
        }

        if (scheduleRender)
        {
            Dispatcher.UIThread.Post(RenderLatestFrame, DispatcherPriority.Render);
        }
        return ValueTask.CompletedTask;
    }

    private void RenderLatestFrame()
    {
        PendingFrame? pending;
        lock (_gate)
        {
            if (_disposed)
            {
                _renderScheduled = false;
                return;
            }
            pending = _pendingFrame;
            _pendingFrame = null;
        }

        if (pending is not null)
        {
            try
            {
                if (_bitmap is null ||
                    _bitmap.PixelSize.Width != pending.Width ||
                    _bitmap.PixelSize.Height != pending.Height)
                {
                    _bitmap?.Dispose();
                    _bitmap = new WriteableBitmap(
                        new PixelSize(pending.Width, pending.Height),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Opaque);
                }

                using var framebuffer = _bitmap.Lock();
                var sourceRowBytes = checked(pending.Width * 4);
                for (var row = 0; row < pending.Height; row++)
                {
                    Marshal.Copy(
                        pending.Pixels,
                        row * sourceRowBytes,
                        IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes),
                        sourceRowBytes);
                }
                frameChanged(_bitmap);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pending.Pixels);
            }
        }

        lock (_gate)
        {
            if (_disposed || _pendingFrame is null)
            {
                _renderScheduled = false;
                return;
            }
        }
        Dispatcher.UIThread.Post(RenderLatestFrame, DispatcherPriority.Render);
    }

    private ValueTask PublishUnlessUpdatingAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_framebufferUpdateInProgress)
            {
                return ValueTask.CompletedTask;
            }
        }

        return PublishAsync(cancellationToken);
    }

    private sealed record PendingFrame(byte[] Pixels, ushort Width, ushort Height);
}
