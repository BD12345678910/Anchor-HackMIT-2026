namespace Anchor.Core.Services;

/// <summary>
/// What the last few seconds of pointer use looked like: how far the cursor travelled, how little
/// of that travel went anywhere, and how hard the buttons were worked.
/// </summary>
public sealed record MouseBehavior(
    bool IsAimless,
    double PixelsPerSecond,
    double Wander,
    double ClicksPerSecond,
    DateTimeOffset? StartedAt,
    string Description);

/// <summary>
/// Detects fidgeting with the mouse: the cursor circling or jittering without going anywhere,
/// and rapid repeated clicking on nothing. Purposeful pointing travels in fairly straight lines
/// towards a target and stops there, so distance alone is never enough — what marks drift is a
/// long path with almost no net displacement, or a click rate no interface asks for, while the
/// keyboard stays quiet.
/// </summary>
public sealed class MouseBehaviorAnalyzer
{
    /// <summary>Below this the cursor is barely moving; nothing to judge.</summary>
    public const double MovingPixelsPerSecond = 200;

    /// <summary>A long path with this little net displacement is circling, not pointing.</summary>
    public const double WanderingRatio = 0.6;
    public const double WanderingPixelsPerSecond = 400;

    /// <summary>Faster than any UI needs: clicking to have something to do.</summary>
    public const double RestlessClicksPerSecond = 2.5;

    /// <summary>Typing means the pointer work belongs to a task, not to fidgeting.</summary>
    public const int TypingKeyCount = 6;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly List<Sample> _samples = [];
    private DateTimeOffset? _startedAt;

    public MouseBehavior Observe(
        DateTimeOffset at,
        double pathDistance,
        double netDistance,
        int directionChanges,
        int clickCount,
        int keyCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pathDistance);
        ArgumentOutOfRangeException.ThrowIfNegative(netDistance);
        ArgumentOutOfRangeException.ThrowIfNegative(directionChanges);
        ArgumentOutOfRangeException.ThrowIfNegative(clickCount);
        ArgumentOutOfRangeException.ThrowIfNegative(keyCount);

        _samples.Add(new Sample(at, pathDistance, Math.Min(netDistance, pathDistance), directionChanges, clickCount, keyCount));
        _samples.RemoveAll(sample => at - sample.At > Window);

        var span = _samples.Count == 0 ? TimeSpan.Zero : at - _samples[0].At;
        var seconds = Math.Max(span.TotalSeconds, 1);
        var path = _samples.Sum(static sample => sample.Path);
        var net = _samples.Sum(static sample => sample.Net);
        var changes = _samples.Sum(static sample => sample.DirectionChanges);
        var clicks = _samples.Sum(static sample => sample.Clicks);
        var keys = _samples.Sum(static sample => sample.Keys);

        var rate = path / seconds;
        var clickRate = clicks / seconds;
        var wander = path > 0 ? 1 - (net / path) : 0;

        var aimless = keys < TypingKeyCount
            && (clickRate >= RestlessClicksPerSecond
                || (rate >= WanderingPixelsPerSecond && wander >= WanderingRatio)
                || (rate >= MovingPixelsPerSecond && changes >= 12 && wander >= WanderingRatio));

        if (aimless)
        {
            _startedAt ??= _samples[0].At;
        }
        else
        {
            _startedAt = null;
        }

        var description = aimless
            ? clickRate >= RestlessClicksPerSecond
                ? $"clicking repeatedly ({clickRate:0.#} clicks/s)"
                : $"cursor wandering ({rate:0} px/s, {wander:P0} of it going nowhere)"
            : $"{rate:0} px/s";

        return new MouseBehavior(aimless, rate, wander, clickRate, _startedAt, description);
    }

    public void Reset()
    {
        _samples.Clear();
        _startedAt = null;
    }

    private readonly record struct Sample(
        DateTimeOffset At,
        double Path,
        double Net,
        int DirectionChanges,
        int Clicks,
        int Keys);
}
