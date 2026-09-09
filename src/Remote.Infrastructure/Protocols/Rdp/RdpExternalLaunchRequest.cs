using Remote.Application.Connections;
using Remote.Protocols;

namespace Remote.Infrastructure.Protocols.Rdp;

public sealed record RdpExternalLaunchRequest
{
    public required Uri Endpoint { get; init; }

    public string? Username { get; init; }

    /// <summary>
    /// Optional explicit logon domain. A null or blank value means the host must
    /// leave its Domain property untouched; it must not infer or prepend one.
    /// </summary>
    public string? Domain { get; init; }

    public ReadOnlyMemory<byte> PasswordUtf8 { get; init; }

    public SessionAccessMode AccessMode { get; init; } = SessionAccessMode.Interactive;

    public DisplayPreferences Display { get; init; } = new();

    public RdpConnectionSettings Settings { get; init; } = new();

    public bool StartFullScreen { get; init; }
}
