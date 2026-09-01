namespace Remote.Application.Vaults;

/// <summary>Platform boundary for protecting sensitive windows from capture and recent-window previews.</summary>
public interface ISensitiveSurfaceProtector
{
    Task<SensitiveSurfaceProtectionResult> ApplyAsync(
        nint nativeWindowHandle,
        SensitiveContentPolicy policy,
        CancellationToken cancellationToken = default);
}

public sealed record SensitiveSurfaceProtectionResult(
    bool ScreenCaptureProtectionApplied,
    bool RecentWindowProtectionApplied,
    string? Limitation = null);
