using Remote.Protocols;

namespace Remote.Application.Connections;

/// <summary>A saved, protocol-neutral description of a remote endpoint.</summary>
public sealed record ConnectionProfile
{
    public required ConnectionId Id { get; init; }

    public required string Name { get; init; }

    public required Uri Endpoint { get; init; }

    public required string ProtocolId { get; init; }

    public FolderId? FolderId { get; init; }

    public ConnectionCredentialReference Credential { get; init; } =
        ConnectionCredentialReference.Inherited;

    public SessionAccessMode DefaultAccessMode { get; init; } =
        SessionAccessMode.Interactive;

    public DisplayPreferences Display { get; init; } = new();
}

public readonly record struct ConnectionId(Guid Value)
{
    public static ConnectionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public sealed record DisplayPreferences
{
    public DisplayScaleMode ScaleMode { get; init; } = DisplayScaleMode.Fit;

    public MonitorSelection MonitorSelection { get; init; } = MonitorSelection.Single;

    public int? MonitorIndex { get; init; } = 0;

    public bool UseDynamicResolution { get; init; } = true;
}

public enum DisplayScaleMode
{
    Fit,
    ActualSize,
    Fill,
    Scroll,
}

public enum MonitorSelection
{
    Single,
    All,
}
