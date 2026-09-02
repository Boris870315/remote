namespace Remote.Application.Layout;

public static class AdaptiveShellLayout
{
    public static AdaptiveShellState Calculate(double width, double height, bool isEditingConnection)
    {
        if (width <= 0 || height <= 0)
        {
            return new AdaptiveShellState(AdaptiveShellMode.Compact, false, false, false, 280);
        }

        var isPortrait = height > width;
        var mode = width < 820 || isPortrait
            ? AdaptiveShellMode.Compact
            : width < 1120
                ? AdaptiveShellMode.Medium
                : AdaptiveShellMode.Wide;
        var showCompactEditor = mode is AdaptiveShellMode.Compact && isEditingConnection;
        return new AdaptiveShellState(
            mode,
            ShowConnectionTree: mode is not AdaptiveShellMode.Compact,
            ShowInspector: showCompactEditor || mode is AdaptiveShellMode.Wide,
            ShowQuickConnect: width >= 760,
            CompactEditorWidth: Math.Min(360, Math.Max(280, width - 52)));
    }
}

public sealed record AdaptiveShellState(
    AdaptiveShellMode Mode,
    bool ShowConnectionTree,
    bool ShowInspector,
    bool ShowQuickConnect,
    double CompactEditorWidth);

public enum AdaptiveShellMode
{
    Compact,
    Medium,
    Wide,
}
