namespace Remote.Application.Vaults;

/// <summary>Controls transient secret display and operating-system surface protection.</summary>
public sealed record SensitiveContentPolicy
{
    public TimeSpan? RevealDuration { get; init; } = TimeSpan.FromSeconds(10);

    public bool HideFromScreenCapture { get; init; } = true;

    public bool HideFromRecentWindows { get; init; } = true;

    public bool ClearsClipboardAutomatically => false;

    public bool IsPermanentReveal => RevealDuration is null;
}
