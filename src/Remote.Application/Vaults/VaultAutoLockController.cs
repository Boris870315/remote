namespace Remote.Application.Vaults;

/// <summary>Determines when a Vault should lock without owning any UI timer.</summary>
public sealed class VaultAutoLockController(
    VaultLockSettings settings,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private DateTimeOffset _lastActivity = (timeProvider ?? TimeProvider.System).GetUtcNow();

    public DateTimeOffset LastActivity => _lastActivity;

    public void RecordActivity() => _lastActivity = _timeProvider.GetUtcNow();

    public VaultLockReason? EvaluateInactivity()
    {
        var timeout = settings.GetInactivityTimeout();
        return timeout is not null && _timeProvider.GetUtcNow() - _lastActivity >= timeout
            ? VaultLockReason.Inactivity
            : null;
    }

    public VaultLockReason? HandleSystemEvent(VaultSystemEvent systemEvent) => systemEvent switch
    {
        VaultSystemEvent.Sleep when settings.LockOnSystemSleep => VaultLockReason.SystemSleep,
        VaultSystemEvent.SessionLogout when settings.LockOnSessionLogout => VaultLockReason.SessionLogout,
        _ => null,
    };
}

public enum VaultSystemEvent
{
    Sleep,
    SessionLogout,
}

public enum VaultLockReason
{
    Manual,
    Inactivity,
    SystemSleep,
    SessionLogout,
}
