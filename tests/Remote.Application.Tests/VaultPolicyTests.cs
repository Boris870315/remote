using Remote.Application.Vaults;

namespace Remote.Application.Tests;

public sealed class VaultPolicyTests
{
    [Fact]
    public void LockSettings_WhenAnyAutomaticProtectionIsDisabled_RequiresWarning()
    {
        var settings = new VaultLockSettings
        {
            InactivityInterval = InactivityLockInterval.Disabled,
        };

        Assert.True(settings.RequiresReducedSecurityWarning);
        Assert.Null(settings.GetInactivityTimeout());
    }

    [Fact]
    public void SensitiveContent_DefaultsToTenSecondRevealAndNeverClearsClipboard()
    {
        var policy = new SensitiveContentPolicy();

        Assert.Equal(TimeSpan.FromSeconds(10), policy.RevealDuration);
        Assert.False(policy.ClearsClipboardAutomatically);
        Assert.True(policy.HideFromScreenCapture);
        Assert.True(policy.HideFromRecentWindows);
    }

    [Fact]
    public void AutoLock_WhenTimeoutElapsed_ReturnsInactivityReason()
    {
        var clock = new ManualTimeProvider();
        var controller = new VaultAutoLockController(
            new VaultLockSettings { InactivityInterval = InactivityLockInterval.OneMinute },
            clock);

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(VaultLockReason.Inactivity, controller.EvaluateInactivity());
    }

    [Fact]
    public void AutoLock_WhenSleepLockDisabled_DoesNotLock()
    {
        var controller = new VaultAutoLockController(
            new VaultLockSettings { LockOnSystemSleep = false });

        Assert.Null(controller.HandleSystemEvent(VaultSystemEvent.Sleep));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
