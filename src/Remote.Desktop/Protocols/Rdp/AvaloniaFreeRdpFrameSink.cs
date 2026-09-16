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
    private bool _isForeground = true;
    private bool _disposed;

    private static readonly TimeSpan BackgroundFrameInterval = TimeSpan.FromMilliseconds(250);

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

        ScheduleRender();
    }

    public void SetForeground(bool isForeground)
    {
        var renderImmediately = false;
        lock (_gate)
        {
            if (_disposed || _isForeground == isForeground) return;
            _isForeground = isForeground;
            renderImmediately = isForeground && _pending.Count > 0;
        }

        if (renderImmediately)
            Dispatcher.UIThread.Post(RenderLatestFrame, DispatcherPriority.Render);
    }

    private void ScheduleRender()
    {
        bool isForeground;
        lock (_gate) isForeground = _isForeground;
        if (isForeground)
        {
            Dispatcher.UIThread.Post(RenderLatestFrame, DispatcherPriority.Render);
            return;
        }

        _ = Task.Delay(BackgroundFrameInterval).ContinueWith(
            _ => Dispatcher.UIThread.Post(RenderLatestFrame, DispatcherPriority.Background),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

        if (pending.Count == 0)
        {
            lock (_gate)
            {
                if (_pending.Count == 0) _renderScheduled = false;
            }
            return;
        }

        try
        {
            var latest = pending[^1];
            var bitmap = _bitmap;
            if (bitmap is null ||
                bitmap.PixelSize.Width != latest.Width ||
                bitmap.PixelSize.Height != latest.Height)
            {
                bitmap?.Dispose();
                bitmap = new WriteableBitmap(
                    new PixelSize(latest.Width, latest.Height),
                    new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                _bitmap = bitmap;
            }

            // Lock once per UI render pass. FreeRDP can produce many small dirty
            // rectangles between two Avalonia frames; locking for every rectangle
            // serializes the render thread and makes interaction visibly stutter.
            using (var framebuffer = bitmap.Lock())
            {
                foreach (var frame in pending)
                {
                    // A resize supersedes patches produced for the previous surface.
                    if (frame.Width != latest.Width || frame.Height != latest.Height) continue;

                    for (var row = 0; row < frame.DirtyHeight; row++)
                    {
                        Marshal.Copy(frame.Pixels, row * frame.Stride,
                            IntPtr.Add(framebuffer.Address,
                                ((frame.Y + row) * framebuffer.RowBytes) + (frame.X * 4)),
                            frame.Stride);
                    }
                }
            }

            // Every pending dirty rectangle is now represented in the bitmap.
            // Notify Avalonia once so rendering cannot fall behind by repainting
            // every intermediate rectangle as a separate stale frame.
            frameChanged(_bitmap);
        }
        finally
        {
            foreach (var frame in pending) ArrayPool<byte>.Shared.Return(frame.Pixels);
        }

        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                _renderScheduled = false;
                return;
            }
        }
        ScheduleRender();
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
