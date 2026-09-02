using Remote.Application.Layout;

namespace Remote.Application.Tests;

public sealed class AdaptiveShellLayoutTests
{
    [Theory]
    [InlineData(1440, 900, AdaptiveShellMode.Wide, true, true)]
    [InlineData(1024, 768, AdaptiveShellMode.Medium, true, false)]
    [InlineData(700, 900, AdaptiveShellMode.Compact, false, false)]
    [InlineData(1080, 1920, AdaptiveShellMode.Compact, false, false)]
    public void Calculate_SelectsExpectedLayoutForLandscapeAndPortraitWindows(
        double width,
        double height,
        AdaptiveShellMode expectedMode,
        bool expectedTree,
        bool expectedInspector)
    {
        var state = AdaptiveShellLayout.Calculate(width, height, isEditingConnection: false);

        Assert.Equal(expectedMode, state.Mode);
        Assert.Equal(expectedTree, state.ShowConnectionTree);
        Assert.Equal(expectedInspector, state.ShowInspector);
    }

    [Theory]
    [InlineData(300, 280)]
    [InlineData(400, 348)]
    [InlineData(800, 360)]
    public void Calculate_UsesBoundedDrawerForCompactEditor(double width, double expectedWidth)
    {
        var state = AdaptiveShellLayout.Calculate(width, 900, isEditingConnection: true);

        Assert.True(state.ShowInspector);
        Assert.Equal(expectedWidth, state.CompactEditorWidth);
    }
}
