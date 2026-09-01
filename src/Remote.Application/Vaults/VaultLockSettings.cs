namespace Remote.Application.Vaults;

/// <summary>User-controlled conditions that return the Vault to its locked state.</summary>
public sealed record VaultLockSettings
{
    public InactivityLockInterval InactivityInterval { get; init; } = InactivityLockInterval.FiveMinutes;

    public bool LockOnSystemSleep { get; init; } = true;

    public bool LockOnSessionLogout { get; init; } = true;

    public bool RequiresReducedSecurityWarning =>
        InactivityInterval is InactivityLockInterval.Disabled ||
        !LockOnSystemSleep ||
        !LockOnSessionLogout;

    public TimeSpan? GetInactivityTimeout() => InactivityInterval switch
    {
        InactivityLockInterval.OneMinute => TimeSpan.FromMinutes(1),
        InactivityLockInterval.FiveMinutes => TimeSpan.FromMinutes(5),
        InactivityLockInterval.FifteenMinutes => TimeSpan.FromMinutes(15),
        InactivityLockInterval.ThirtyMinutes => TimeSpan.FromMinutes(30),
        InactivityLockInterval.Disabled => null,
        _ => throw new ArgumentOutOfRangeException(nameof(InactivityInterval)),
    };
}

public enum InactivityLockInterval
{
    OneMinute,
    FiveMinutes,
    FifteenMinutes,
    ThirtyMinutes,
    Disabled,
}
