namespace Anchor.Infrastructure.Windows;

public enum SafetyReleaseReason
{
    Escape,
    EmergencyHotkey,
    FocusLost,
    ProcessExited,
    SecureWindow,
    WatchdogExpired,
    Shutdown
}

public interface IRestrictiveInterventionController
{
    void ReleasePointer();
    void ClearOverlays();
    void ReleaseHooks();
}

public sealed class SafetyWatchdog
{
    private readonly IRestrictiveInterventionController _restrictions;
    private readonly TimeSpan _timeout;
    private DateTimeOffset _lastHeartbeat;

    public SafetyWatchdog(IRestrictiveInterventionController restrictions, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(restrictions);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _restrictions = restrictions;
        _timeout = timeout;
    }

    public bool IsArmed { get; private set; }
    public SafetyReleaseReason? LastReleaseReason { get; private set; }

    public void Arm(DateTimeOffset now)
    {
        _lastHeartbeat = now;
        LastReleaseReason = null;
        IsArmed = true;
    }

    public void Heartbeat(DateTimeOffset now)
    {
        if (IsArmed && now >= _lastHeartbeat)
        {
            _lastHeartbeat = now;
        }
    }

    public bool CheckExpired(DateTimeOffset now)
    {
        if (!IsArmed || now - _lastHeartbeat <= _timeout)
        {
            return false;
        }

        Signal(SafetyReleaseReason.WatchdogExpired);
        return true;
    }

    public void Signal(SafetyReleaseReason reason)
    {
        LastReleaseReason = reason;
        ReleaseAll();
    }

    public void ReleaseAll()
    {
        _restrictions.ReleasePointer();
        _restrictions.ClearOverlays();
        _restrictions.ReleaseHooks();
        IsArmed = false;
    }
}
