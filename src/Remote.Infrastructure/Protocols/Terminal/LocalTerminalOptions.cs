using Remote.Protocols;

namespace Remote.Infrastructure.Protocols.Terminal;

public sealed record LocalTerminalOptions
{
    public string? ShellPath { get; init; }

    public string WorkingDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public SessionAccessMode AccessMode { get; init; } = SessionAccessMode.Interactive;

    public int Columns { get; init; } = 120;

    public int Rows { get; init; } = 32;
}
