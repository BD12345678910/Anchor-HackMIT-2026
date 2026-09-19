namespace Anchor.Core.Models;

public sealed record SensorWindow(
    DateTimeOffset Timestamp,
    int KeyCount,
    double MouseDistance,
    double IdleSeconds,
    double AppRelevance,
    double GazePresence,
    int AppSwitchCount,
    int ScrollReversalCount,
    bool IsSecureWindow,
    bool IsWorkerAvailable,
    bool IsManualReport)
{
    public static SensorWindow Create(
        int keyCount,
        double mouseDistance,
        double idleSeconds,
        double appRelevance = 0.5,
        double gazePresence = 0.5,
        int appSwitchCount = 0,
        int scrollReversalCount = 0,
        bool isSecureWindow = false,
        bool isWorkerAvailable = false,
        bool isManualReport = false,
        DateTimeOffset? timestamp = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keyCount);
        ArgumentOutOfRangeException.ThrowIfNegative(mouseDistance);
        ArgumentOutOfRangeException.ThrowIfNegative(idleSeconds);
        ArgumentOutOfRangeException.ThrowIfNegative(appSwitchCount);
        ArgumentOutOfRangeException.ThrowIfNegative(scrollReversalCount);

        return new SensorWindow(
            timestamp ?? DateTimeOffset.UtcNow,
            keyCount,
            mouseDistance,
            idleSeconds,
            ClampScore(appRelevance),
            ClampScore(gazePresence),
            appSwitchCount,
            scrollReversalCount,
            isSecureWindow,
            isWorkerAvailable,
            isManualReport);
    }

    private static double ClampScore(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
