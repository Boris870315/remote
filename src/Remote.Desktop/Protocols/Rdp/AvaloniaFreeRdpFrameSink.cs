using System.Buffers;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Remote.Desktop.Protocols.Rdp;

internal sealed class AvaloniaFreeRdpFrameSink(Action<WriteableBitmap?> frameChanged) : IDisposable
{
    private readonly object _gate = new();
    private WriteableBitmap? _bitmap;
    private List<PendingFrame> _pending = [];
    private bool _renderScheduled;
    private bool _disposed;

    public WriteableBitmap? Frame => _bitmap;

    public void Publish(byte[] pixels, int width, int height, int x, int y, int stride, int dirtyHeight)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                ArrayPool<byte>.Shared.Return(pixels);
                return;
            }
            _pending.Add(new PendingFrame(pixels, width, height, x, y, stride, dirtyHeight));
            if (_renderScheduled) return;
            _renderScheduled = true;
        }

        Dispatcher.UIThread.Post(RenderLatestFrame, DispatcherPriority.Render);
    }

    private void RenderLatestFrame()
    {
        List<PendingFrame> pending;
        lock (_gate)
        {
            if (_disposed)
            {
                _renderScheduled = false;
                foreach (var frame in _pending) ArrayPool<byte>.Shared.Return(frame.Pixels);
                _pending.Clear();
                return;
            }
            pending = _pending;
            _pending = [];
        }

        foreach (var frame in pending)
        {
            try
            {
                var pixels = frame.Pixels;
                var width = frame.Width;
                var height = frame.Height;
                var stride = frame.Stride;
                if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
                {
                    _bitmap?.Dispose();
                    _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                        PixelFormat.Bgra8888, AlphaFormat.Opaque);
                }

                using var framebuffer = _bitmap.Lock();
                var rowBytes = stride;
                for (var row = 0; row < frame.DirtyHeight; row++)
                {
                    Marshal.Copy(pixels, row * stride,
                        IntPtr.Add(framebuffer.Address,
                            ((frame.Y + row) * framebuffer.RowBytes) + (frame.X * 4)), rowBytes);
                }
                frameChanged(_bitmap);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame.Pixels);
            }
        }

        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                _renderScheduled = false;
                return;
            }
        }
        Dispatcher.UIThread.Post(() =>
            RenderLatestFrame(), DispatcherPriority.Render);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var frame in _pending) ArrayPool<byte>.Shared.Return(frame.Pixels);
            _pending.Clear();
        }
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private sealed record PendingFrame(byte[] Pixels, int Width, int Height,
        int X, int Y, int Stride, int DirtyHeight);
}
