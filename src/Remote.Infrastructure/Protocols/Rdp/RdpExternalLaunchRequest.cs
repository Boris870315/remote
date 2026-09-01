using Remote.Application.Connections;
using Remote.Protocols;

namespace Remote.Infrastructure.Protocols.Rdp;

public sealed record RdpExternalLaunchRequest
{
    public required Uri Endpoint { get; init; }

    public string? Username { get; init; }

    public SessionAccessMode AccessMode { get; init; } = SessionAccessMode.Interactive;

    public DisplayPreferences Display { get; init; } = new();

    public RdpConnectionSettings Settings { get; init; } = new();

    public bool StartFullScreen { get; init; }
}
