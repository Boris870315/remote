using Remote.Application.Connections;
using Remote.Protocols;

namespace Remote.Infrastructure.Protocols.Terminal;

public sealed record LocalTerminalOptions
{
    public string? ShellPath { get; init; }

    public string WorkingDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public SessionAccessMode AccessMode { get; init; } = SessionAccessMode.Interactive;

    public int Columns { get; init; } = 120;

    public int Rows { get; init; } = 32;

    public ProtocolSettings ToProtocolSettings()
    {
        var settings = new ProtocolSettings().Set("terminal.working-directory", WorkingDirectory);
        return string.IsNullOrWhiteSpace(ShellPath)
            ? settings
            : settings.Set("terminal.shell-path", ShellPath);
    }

    public static LocalTerminalOptions FromProtocolSettings(ProtocolSettings settings) => new()
    {
        ShellPath = settings.Get("terminal.shell-path") ?? settings.Get("shellPath"),
        WorkingDirectory = settings.Get("terminal.working-directory")
            ?? settings.Get("workingDirectory")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    };
}
