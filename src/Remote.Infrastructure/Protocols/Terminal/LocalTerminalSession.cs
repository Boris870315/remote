using Porta.Pty;
using Remote.Protocols;

namespace Remote.Infrastructure.Protocols.Terminal;

/// <summary>A real local PTY backed by ConPTY on Windows and forkpty on macOS/Linux.</summary>
public sealed class LocalTerminalSession : IAsyncDisposable
{
    private IPtyConnection? _connection;
    private SessionAccessMode _accessMode;

    public bool IsRunning => _connection is not null;

    public int? ProcessId => _connection?.Pid;

    public async Task StartAsync(LocalTerminalOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_connection is not null)
        {
            throw new InvalidOperationException("The local terminal is already running.");
        }

        var shell = ResolveShell(options.ShellPath);
        var workingDirectory = Path.GetFullPath(options.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Terminal working directory '{workingDirectory}' does not exist.");
        }

        if (options.Columns <= 0 || options.Rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Terminal dimensions must be positive.");
        }

        _connection = await PtyProvider.SpawnAsync(new PtyOptions
        {
            Name = "Remote Local Terminal",
            Cols = options.Columns,
            Rows = options.Rows,
            Cwd = workingDirectory,
            App = shell,
            CommandLine = [],
            Environment = new Dictionary<string, string>
            {
                ["TERM"] = "xterm-256color",
                ["COLORTERM"] = "truecolor",
            },
        }, cancellationToken).ConfigureAwait(false);
        _accessMode = options.AccessMode;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        (_connection ?? throw new InvalidOperationException("The local terminal is not running."))
            .ReaderStream.ReadAsync(buffer, cancellationToken);

    public async Task<bool> WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        if (_accessMode is SessionAccessMode.ViewOnly)
        {
            return false;
        }

        var stream = (_connection ?? throw new InvalidOperationException("The local terminal is not running."))
            .WriterStream;
        await stream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public void Resize(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal dimensions must be positive.");
        }

        (_connection ?? throw new InvalidOperationException("The local terminal is not running."))
            .Resize(columns, rows);
    }

    public ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            try
            {
                _connection.Kill();
            }
            catch (InvalidOperationException)
            {
                // The process may already have exited; disposal still releases PTY handles.
            }

            _connection.Dispose();
            _connection = null;
        }

        return ValueTask.CompletedTask;
    }

    public static string ResolveShell(string? configuredShell)
    {
        if (!string.IsNullOrWhiteSpace(configuredShell))
        {
            var fullPath = Path.GetFullPath(configuredShell);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("The configured terminal shell was not found.", fullPath);
            }

            return fullPath;
        }

        if (OperatingSystem.IsWindows())
        {
            return Environment.GetEnvironmentVariable("ComSpec")
                ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        }

        var environmentShell = Environment.GetEnvironmentVariable("SHELL");
        return !string.IsNullOrWhiteSpace(environmentShell) && File.Exists(environmentShell)
            ? environmentShell
            : OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/sh";
    }
}
