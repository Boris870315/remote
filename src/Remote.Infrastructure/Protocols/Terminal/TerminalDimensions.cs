namespace Remote.Infrastructure.Protocols.Terminal;

public readonly record struct TerminalDimensions(int Columns, int Rows)
{
    public static TerminalDimensions FromPixels(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Terminal pixel dimensions must be finite.");
        }

        // Matches the 12px monospace terminal surface with a small allowance
        // for glyph advance and line spacing on both Windows and macOS.
        return new(
            Math.Clamp((int)Math.Floor(Math.Max(0, width - 24) / 8), 20, 500),
            Math.Clamp((int)Math.Floor(Math.Max(0, height - 24) / 18), 5, 200));
    }
}
