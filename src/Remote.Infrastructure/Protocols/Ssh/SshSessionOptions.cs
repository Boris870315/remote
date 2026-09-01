using Remote.Protocols;

namespace Remote.Infrastructure.Protocols.Ssh;

public sealed record SshSessionOptions
{
    public required Uri Endpoint { get; init; }

    public required string Username { get; init; }

    public required string Password { get; init; }

    public string? ExpectedHostKeySha256 { get; init; }

    public Func<SshHostKeyInfo, bool>? ConfirmUnknownHostKey { get; init; }

    public SessionAccessMode AccessMode { get; init; } = SessionAccessMode.Interactive;

    public int Columns { get; init; } = 120;

    public int Rows { get; init; } = 32;
}

public sealed record SshHostKeyInfo(string Algorithm, string Sha256Fingerprint);
