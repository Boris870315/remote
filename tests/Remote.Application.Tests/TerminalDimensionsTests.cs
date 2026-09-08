using Remote.Infrastructure.Protocols.Terminal;

namespace Remote.Application.Tests;

public sealed class TerminalDimensionsTests
{
    [Fact]
    public void FromPixels_ConvertsWorkspaceToRowsAndColumns()
    {
        var result = TerminalDimensions.FromPixels(984, 744);

        Assert.Equal(new TerminalDimensions(120, 40), result);
    }

    [Theory]
    [InlineData(0, 0, 20, 5)]
    [InlineData(100000, 100000, 500, 200)]
    public void FromPixels_ClampsToSupportedRange(
        double width,
        double height,
        int columns,
        int rows) =>
        Assert.Equal(new TerminalDimensions(columns, rows), TerminalDimensions.FromPixels(width, height));
}
