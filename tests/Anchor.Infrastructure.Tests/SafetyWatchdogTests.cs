using Anchor.Infrastructure.Windows;

namespace Anchor.Infrastructure.Tests;

public sealed class SafetyWatchdogTests
{
    [Theory]
    [InlineData(SafetyReleaseReason.Escape)]
    [InlineData(SafetyReleaseReason.FocusLost)]
    [InlineData(SafetyReleaseReason.SecureWindow)]
    [InlineData(SafetyReleaseReason.Shutdown)]
    public void Safety_signal_releases_every_restriction(SafetyReleaseReason reason)
    {
        var restrictions = new FakeRestrictions();
        var watchdog = new SafetyWatchdog(restrictions, TimeSpan.FromSeconds(30));
        watchdog.Arm(DateTimeOffset.UnixEpoch);

        watchdog.Signal(reason);

        Assert.False(watchdog.IsArmed);
        Assert.Equal(1, restrictions.ReleasePointerCalls);
        Assert.Equal(1, restrictions.ClearOverlayCalls);
        Assert.Equal(1, restrictions.ReleaseHookCalls);
        Assert.Equal(reason, watchdog.LastReleaseReason);
    }

    [Fact]
    public void Expired_watchdog_fails_open()
    {
        var restrictions = new FakeRestrictions();
        var watchdog = new SafetyWatchdog(restrictions, TimeSpan.FromSeconds(30));
        watchdog.Arm(DateTimeOffset.UnixEpoch);

        var expired = watchdog.CheckExpired(DateTimeOffset.UnixEpoch.AddSeconds(31));

        Assert.True(expired);
        Assert.Equal(SafetyReleaseReason.WatchdogExpired, watchdog.LastReleaseReason);
        Assert.Equal(1, restrictions.ReleasePointerCalls);
    }

    [Fact]
    public void Heartbeat_extends_watchdog_deadline()
    {
        var restrictions = new FakeRestrictions();
        var watchdog = new SafetyWatchdog(restrictions, TimeSpan.FromSeconds(30));
        watchdog.Arm(DateTimeOffset.UnixEpoch);
        watchdog.Heartbeat(DateTimeOffset.UnixEpoch.AddSeconds(20));

        Assert.False(watchdog.CheckExpired(DateTimeOffset.UnixEpoch.AddSeconds(40)));
        Assert.True(watchdog.CheckExpired(DateTimeOffset.UnixEpoch.AddSeconds(51)));
    }

    private sealed class FakeRestrictions : IRestrictiveInterventionController
    {
        public int ReleasePointerCalls { get; private set; }
        public int ClearOverlayCalls { get; private set; }
        public int ReleaseHookCalls { get; private set; }

        public void ReleasePointer() => ReleasePointerCalls++;
        public void ClearOverlays() => ClearOverlayCalls++;
        public void ReleaseHooks() => ReleaseHookCalls++;
    }
}
