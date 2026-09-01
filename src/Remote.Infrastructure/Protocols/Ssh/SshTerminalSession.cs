using Renci.SshNet;
using Remote.Protocols;

namespace Remote.Infrastructure.Protocols.Ssh;

/// <summary>An embedded SSH2 terminal session with strict host-key verification.</summary>
public sealed class SshTerminalSession : IAsyncDisposable
{
    private SshClient? _client;
    private ShellStream? _shell;
    private SessionAccessMode _accessMode;

    public bool IsConnected => _client?.IsConnected is true && _shell is not null;

    public async Task ConnectAsync(SshSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        if (_client is not null)
        {
            throw new InvalidOperationException("The SSH session is already connected.");
        }

        var port = options.Endpoint.IsDefaultPort ? 22 : options.Endpoint.Port;
        var client = new SshClient(options.Endpoint.Host, port, options.Username, options.Password);
        client.HostKeyReceived += (_, eventArgs) =>
        {
            var hostKey = new SshHostKeyInfo(eventArgs.HostKeyName, eventArgs.FingerPrintSHA256);
            eventArgs.CanTrust = SshHostKeyPolicy.CanTrust(
                hostKey,
                options.ExpectedHostKeySha256,
                options.ConfirmUnknownHostKey);
        };

        try
        {
            await Task.Run(client.Connect, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _shell = client.CreateShellStream(
                "Remote",
                (uint)options.Columns,
                (uint)options.Rows,
                0,
                0,
                16 * 1024);
            _client = client;
            _accessMode = options.AccessMode;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        (_shell ?? throw new InvalidOperationException("The SSH session is not connected."))
            .ReadAsync(buffer, cancellationToken);

    public async Task<bool> WriteAsync(
        ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken = default)
    {
        if (_accessMode is SessionAccessMode.ViewOnly)
        {
            return false;
        }

        var shell = _shell ?? throw new InvalidOperationException("The SSH session is not connected.");
        await shell.WriteAsync(input, cancellationToken).ConfigureAwait(false);
        await shell.FlushAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public void Resize(uint columns, uint rows, uint width, uint height)
    {
        if (columns == 0 || rows == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal dimensions must be positive.");
        }

        (_shell ?? throw new InvalidOperationException("The SSH session is not connected."))
            .ChangeWindowSize(columns, rows, width, height);
    }

    public ValueTask DisposeAsync()
    {
        _shell?.Dispose();
        _shell = null;
        if (_client is not null)
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }

            _client.Dispose();
            _client = null;
        }

        return ValueTask.CompletedTask;
    }

    private static void Validate(SshSessionOptions options)
    {
        if (!string.Equals(options.Endpoint.Scheme, "ssh", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An ssh endpoint is required.", nameof(options));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.Username);
        ArgumentException.ThrowIfNullOrEmpty(options.Password);
        if (options.Columns <= 0 || options.Rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Terminal dimensions must be positive.");
        }
    }
}
