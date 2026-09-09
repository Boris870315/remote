using Avalonia.Input;

namespace Remote.Desktop.Protocols.Rdp;

internal static class RdpVirtualKeyMapper
{
    public static uint? Map(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return 0x41u + (uint)(key - Key.A);
        if (key is >= Key.D0 and <= Key.D9) return 0x30u + (uint)(key - Key.D0);
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return 0x60u + (uint)(key - Key.NumPad0);
        if (key is >= Key.F1 and <= Key.F12) return 0x70u + (uint)(key - Key.F1);
        return key switch
        {
            Key.Back => 0x08, Key.Tab => 0x09, Key.Enter => 0x0D, Key.Escape => 0x1B,
            Key.Space => 0x20, Key.PageUp => 0x21, Key.PageDown => 0x22,
            Key.End => 0x23, Key.Home => 0x24, Key.Left => 0x25, Key.Up => 0x26,
            Key.Right => 0x27, Key.Down => 0x28, Key.Insert => 0x2D, Key.Delete => 0x2E,
            Key.Multiply => 0x6A, Key.Add => 0x6B, Key.Subtract => 0x6D,
            Key.Decimal => 0x6E, Key.Divide => 0x6F, Key.CapsLock => 0x14,
            Key.LeftShift or Key.RightShift => 0x10,
            Key.LeftCtrl or Key.RightCtrl => 0x11,
            Key.LeftAlt or Key.RightAlt => 0x12,
            Key.LWin => 0x5B, Key.RWin => 0x5C,
            _ => null,
        };
    }
}
