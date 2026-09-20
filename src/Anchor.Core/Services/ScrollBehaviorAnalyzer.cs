namespace Anchor.Core.Services;

/// <summary>
/// What a burst of scrolling looks like over the last few seconds: how fast the wheel moved,
/// how often it changed direction, and whether that pattern is reading or flicking through pages.
/// </summary>
public sealed record ScrollBehavior(
    bool IsThrashing,
    double NotchesPerSecond,
    int Reversals,
    DateTimeOffset? StartedAt,
    string Description);

/// <summary>
/// Detects fast or erratic scrolling — flicking through a PDF or a long page without stopping to
/// read. Reading scrolls in bursts with pauses between them and rarely turns around; skimming past
/// your place is continuous, fast, and often reverses direction while nothing is typed.
/// </summary>
public sealed class ScrollBehaviorAnalyzer
{
    /// <summary>Wheel notches per second that count as fast on their own.</summary>
    public const double FastNotchesPerSecond = 10;

    /// <summary>Slower, but with direction changes it is hunting rather than reading.</summary>
    public const double ErraticNotchesPerSecond = 6;
    public const int ErraticReversals = 5;

    /// <summary>Typing means the scrolling is part of editing or note-taking, not skimming.</summary>
    public const int TypingKeyCount = 6;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(4);

    private readonly List<(DateTimeOffset At, int Notches, int Reversals, int Keys)> _samples = [];
    private DateTimeOffset? _startedAt;

    public ScrollBehavior Observe(DateTimeOffset at, int notches, int reversals, int keyCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(notches);
        ArgumentOutOfRangeException.ThrowIfNegative(reversals);
        ArgumentOutOfRangeException.ThrowIfNegative(keyCount);

        _samples.Add((at, notches, reversals, keyCount));
        _samples.RemoveAll(sample => at - sample.At > Window);

        var span = _samples.Count == 0 ? TimeSpan.Zero : at - _samples[0].At;
        var seconds = Math.Max(span.TotalSeconds, 1);
        var totalNotches = _samples.Sum(static sample => sample.Notches);
        var totalReversals = _samples.Sum(static sample => sample.Reversals);
        var totalKeys = _samples.Sum(static sample => sample.Keys);
        var rate = totalNotches / seconds;

        var thrashing = totalKeys < TypingKeyCount
            && (rate >= FastNotchesPerSecond
                || (rate >= ErraticNotchesPerSecond && totalReversals >= ErraticReversals));

        if (thrashing)
        {
            _startedAt ??= _samples[0].At;
        }
        else
        {
            _startedAt = null;
        }

        var description = thrashing
            ? totalReversals >= ErraticReversals
                ? $"scrolling back and forth ({rate:0.#} notches/s, {totalReversals} direction changes)"
                : $"scrolling straight past the page ({rate:0.#} notches/s)"
            : $"{rate:0.#} notches/s";

        return new ScrollBehavior(thrashing, rate, totalReversals, _startedAt, description);
    }

    public void Reset()
    {
        _samples.Clear();
        _startedAt = null;
    }
}
