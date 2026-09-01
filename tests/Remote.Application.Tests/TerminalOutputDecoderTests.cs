using System.Text;
using Remote.Infrastructure.Protocols.Terminal;

namespace Remote.Application.Tests;

public sealed class TerminalOutputDecoderTests
{
    [Fact]
    public void Decode_StripsAnsiColorAndTitleSequences()
    {
        var decoder = new TerminalOutputDecoder();
        var bytes = Encoding.UTF8.GetBytes("\u001b]0;title\a\u001b[31m紅色\u001b[0m text\r\n");

        Assert.Equal("紅色 text\n", decoder.Decode(bytes));
    }

    [Fact]
    public void Decode_HandlesControlAndUtf8SequencesSplitAcrossReads()
    {
        var decoder = new TerminalOutputDecoder();
        var bytes = Encoding.UTF8.GetBytes("A\u001b[32m測試\u001b[0mB");

        var first = decoder.Decode(bytes.AsSpan(0, 5));
        var second = decoder.Decode(bytes.AsSpan(5, 3));
        var third = decoder.Decode(bytes.AsSpan(8));

        Assert.Equal("A測試B", first + second + third);
    }
}
