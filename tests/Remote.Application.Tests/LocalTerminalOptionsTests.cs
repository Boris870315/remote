using Remote.Infrastructure.Protocols.Terminal;
using Remote.Protocols;

namespace Remote.Application.Tests;

public sealed class LocalTerminalOptionsTests
{
    [Fact]
    public void ProtocolSettings_RoundTripShellAndWorkingDirectory()
    {
        var original = new LocalTerminalOptions
        {
            ShellPath = "/bin/sh",
            WorkingDirectory = "/tmp",
            AccessMode = SessionAccessMode.ViewOnly,
        };

        var restored = LocalTerminalOptions.FromProtocolSettings(original.ToProtocolSettings());

        Assert.Equal(original.ShellPath, restored.ShellPath);
        Assert.Equal(original.WorkingDirectory, restored.WorkingDirectory);
    }
}
