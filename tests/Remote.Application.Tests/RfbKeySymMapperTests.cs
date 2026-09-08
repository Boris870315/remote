using Avalonia.Input;
using Remote.Desktop.Protocols.Vnc;

namespace Remote.Application.Tests;

public sealed class RfbKeySymMapperTests
{
    [Theory]
    [InlineData(Key.A, 0x0061u)]
    [InlineData(Key.D9, 0x0039u)]
    [InlineData(Key.F1, 0xFFBEu)]
    [InlineData(Key.F12, 0xFFC9u)]
    [InlineData(Key.Home, 0xFF50u)]
    [InlineData(Key.PageDown, 0xFF56u)]
    [InlineData(Key.NumPad7, 0xFFB7u)]
    [InlineData(Key.RightCtrl, 0xFFE4u)]
    public void Map_ReturnsStandardX11KeySym(Key key, uint expected) =>
        Assert.Equal(expected, RfbKeySymMapper.Map(key));

    [Fact]
    public void Map_UnknownKey_ReturnsNull() =>
        Assert.Null(RfbKeySymMapper.Map(Key.None));
}
