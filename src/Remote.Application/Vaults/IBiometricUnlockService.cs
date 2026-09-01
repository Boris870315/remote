namespace Remote.Application.Vaults;

/// <summary>Platform boundary for Windows Hello, Touch ID, and future equivalents.</summary>
public interface IBiometricUnlockService
{
    BiometricAvailability GetAvailability();

    Task<BiometricUnlockResult> RequestUnlockAsync(
        string reason,
        CancellationToken cancellationToken = default);
}

public sealed record BiometricAvailability(
    bool IsAvailable,
    BiometricKind Kind,
    string? UnavailableReason = null);

public enum BiometricKind
{
    None,
    WindowsHello,
    TouchId,
}

public sealed record BiometricUnlockResult(
    bool IsSuccessful,
    BiometricUnlockFailure Failure = BiometricUnlockFailure.None);

public enum BiometricUnlockFailure
{
    None,
    Cancelled,
    NotAvailable,
    NotRecognized,
    PlatformError,
}
