using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Remote.Desktop.Protocols.Rdp;

internal sealed class AvaloniaFreeRdpFrameSink(Action<WriteableBitmap?> frameChanged) : IDisposable
{
    private WriteableBitmap? _bitmap;

    public WriteableBitmap? Frame => _bitmap;

    public void Publish(byte[] pixels, int width, int height, int stride)
    {
        var snapshot = pixels;
        Dispatcher.UIThread.Post(() =>
        {
            if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }

            using var framebuffer = _bitmap.Lock();
            var rowBytes = checked(width * 4);
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(snapshot, row * stride,
                    IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), rowBytes);
            }
            frameChanged(_bitmap);
        }, DispatcherPriority.Render);
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
