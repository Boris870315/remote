using Remote.Infrastructure.Protocols.Terminal;
using Remote.Protocols;
using System.Text;

namespace Remote.Application.Tests;

public sealed class LocalTerminalSessionTests
{
    [Fact]
    public void ResolveShell_WithMissingConfiguredPath_FailsClosed()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-shell-{Guid.NewGuid():N}");
        Assert.Throws<FileNotFoundException>(() => LocalTerminalSession.ResolveShell(missing));
    }

    [Fact]
    public void ResolveShell_Default_ReturnsExistingPlatformShell()
    {
        Assert.True(File.Exists(LocalTerminalSession.ResolveShell(null)));
    }

    [Fact]
    public async Task InteractivePty_RoundTripsCommandOutput()
    {
        await using var terminal = new LocalTerminalSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await terminal.StartAsync(new LocalTerminalOptions(), timeout.Token);
        var input = Encoding.UTF8.GetBytes("echo remote-pty-roundtrip\r");

        Assert.True(await terminal.WriteAsync(input, timeout.Token));

        var output = new StringBuilder();
        var buffer = new byte[4096];
        while (!output.ToString().Contains("remote-pty-roundtrip", StringComparison.Ordinal))
        {
            var read = await terminal.ReadAsync(buffer, timeout.Token);
            Assert.True(read > 0);
            output.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }

    [Fact]
    public async Task ViewOnlyPty_BlocksInputBeforeWriting()
    {
        await using var terminal = new LocalTerminalSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await terminal.StartAsync(new LocalTerminalOptions
        {
            AccessMode = SessionAccessMode.ViewOnly,
        }, timeout.Token);

        Assert.False(await terminal.WriteAsync("echo blocked\r"u8.ToArray(), timeout.Token));
    }
}
