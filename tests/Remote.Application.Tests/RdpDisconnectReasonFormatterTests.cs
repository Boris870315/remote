using Remote.Desktop.Protocols.Rdp;

namespace Remote.Application.Tests;

public sealed class RdpDisconnectReasonFormatterTests
{
    [Theory]
    [InlineData(768, "帳號或密碼錯誤")]
    [InlineData(7, "伺服器拒絕連線")]
    [InlineData(9, "此帳號沒有足夠的遠端登入權限")]
    [InlineData(257, "遠端桌面授權失敗")]
    [InlineData(4096, "RDP 協定錯誤（4096）")]
    public void Format_ReturnsActionableMessage(int reason, string expected) =>
        Assert.Equal(expected, RdpDisconnectReasonFormatter.Format(reason));
}
