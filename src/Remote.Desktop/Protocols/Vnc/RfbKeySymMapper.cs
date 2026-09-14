using Avalonia.Input;

namespace Remote.Desktop.Protocols.Vnc;

public static class RfbKeySymMapper
{
    public static uint? Map(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return (uint)('a' + (key - Key.A));
        if (key is >= Key.D0 and <= Key.D9) return (uint)('0' + (key - Key.D0));
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return (uint)(0xFFB0 + (key - Key.NumPad0));
        if (key is >= Key.F1 and <= Key.F24) return (uint)(0xFFBE + (key - Key.F1));

        return key switch
        {
            Key.Enter => 0xFF0D,
            Key.Back => 0xFF08,
            Key.Tab => 0xFF09,
            Key.Escape => 0xFF1B,
            Key.Clear => 0xFF0B,
            Key.Pause => 0xFF13,
            Key.Scroll => 0xFF14,
            Key.Insert => 0xFF63,
            Key.Delete => 0xFFFF,
            Key.PrintScreen => 0xFF61,
            Key.Home => 0xFF50,
            Key.End => 0xFF57,
            Key.PageUp => 0xFF55,
            Key.PageDown => 0xFF56,
            Key.Left => 0xFF51,
            Key.Up => 0xFF52,
            Key.Right => 0xFF53,
            Key.Down => 0xFF54,
            Key.LeftShift => 0xFFE1,
            Key.RightShift => 0xFFE2,
            Key.LeftCtrl => 0xFFE3,
            Key.RightCtrl => 0xFFE4,
            Key.CapsLock => 0xFFE5,
            Key.LWin => 0xFFEB,
            Key.RWin => 0xFFEC,
            Key.Apps => 0xFF67,
            Key.LeftAlt => 0xFFE9,
            Key.RightAlt => 0xFFEA,
            Key.NumLock => 0xFF7F,
            Key.Space => 0x20,
            Key.Multiply => 0xFFAA,
            Key.Add => 0xFFAB,
            Key.Separator => 0xFFAC,
            Key.Subtract => 0xFFAD,
            Key.Decimal => 0xFFAE,
            Key.Divide => 0xFFAF,
            Key.OemSemicolon => 0x003B,
            Key.OemPlus => 0x003D,
            Key.OemComma => 0x002C,
            Key.OemMinus => 0x002D,
            Key.OemPeriod => 0x002E,
            Key.OemQuestion => 0x002F,
            Key.OemTilde => 0x0060,
            Key.OemOpenBrackets => 0x005B,
            Key.OemPipe => 0x005C,
            Key.OemCloseBrackets => 0x005D,
            Key.OemQuotes => 0x0027,
            Key.OemBackslash => 0x005C,
            _ => null,
        };
    }
}
