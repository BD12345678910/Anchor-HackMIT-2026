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
/// Detects fast, erratic or unbroken scrolling — flicking through a PDF or a long page without
/// stopping to read. Reading scrolls in bursts with pauses between them and rarely turns around;
/// skimming past your place is continuous, fast, and often reverses direction while nothing is
/// typed. Scrolling that never reaches the speed of a flick still counts once it never stops
/// either: pages moving under the eye second after second with no pause is not reading.
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

    /// <summary>A moderate rate is enough when the wheel never stops for this long.</summary>
    public const double UnbrokenNotchesPerSecond = 4;

    /// <summary>Share of the long window that must contain scrolling for it to count as unbroken.</summary>
    public const double UnbrokenCoverage = 0.7;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(4);

    /// <summary>Reading pauses show up over this span; a steady drag through a document does not.</summary>
    public static readonly TimeSpan UnbrokenWindow = TimeSpan.FromSeconds(12);

    private readonly List<(DateTimeOffset At, int Notches, int Reversals, int Keys)> _samples = [];
    private DateTimeOffset? _startedAt;

    public ScrollBehavior Observe(DateTimeOffset at, int notches, int reversals, int keyCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(notches);
        ArgumentOutOfRangeException.ThrowIfNegative(reversals);
        ArgumentOutOfRangeException.ThrowIfNegative(keyCount);

        _samples.Add((at, notches, reversals, keyCount));
        _samples.RemoveAll(sample => at - sample.At > UnbrokenWindow);

        var recent = _samples.Where(sample => at - sample.At <= Window).ToList();
        var span = recent.Count == 0 ? TimeSpan.Zero : at - recent[0].At;
        var seconds = Math.Max(span.TotalSeconds, 1);
        var totalNotches = recent.Sum(static sample => sample.Notches);
        var totalReversals = recent.Sum(static sample => sample.Reversals);
        var totalKeys = recent.Sum(static sample => sample.Keys);
        var rate = totalNotches / seconds;

        var unbroken = IsUnbroken(at, out var unbrokenRate);

        var thrashing = totalKeys < TypingKeyCount
            && (rate >= FastNotchesPerSecond
                || (rate >= ErraticNotchesPerSecond && totalReversals >= ErraticReversals)
                || unbroken);

        if (thrashing)
        {
            _startedAt ??= unbroken ? _samples[0].At : recent[0].At;
        }
        else
        {
            _startedAt = null;
        }

        var description = thrashing
            ? totalReversals >= ErraticReversals
                ? $"scrolling back and forth ({rate:0.#} notches/s, {totalReversals} direction changes)"
                : rate < FastNotchesPerSecond
                    ? $"scrolling without pausing ({unbrokenRate:0.#} notches/s for {UnbrokenWindow.TotalSeconds:0}s)"
                    : $"scrolling straight past the page ({rate:0.#} notches/s)"
            : $"{rate:0.#} notches/s";

        return new ScrollBehavior(thrashing, rate, totalReversals, _startedAt, description);
    }

    /// <summary>
    /// True when the wheel has been turning through most of the long window at a steady rate with
    /// no typing — the pattern of dragging a document past you rather than reading it.
    /// </summary>
    private bool IsUnbroken(DateTimeOffset at, out double rate)
    {
        rate = 0;
        var span = at - _samples[0].At;
        if (span < UnbrokenWindow - TimeSpan.FromSeconds(2))
        {
            return false;
        }

        if (_samples.Sum(static sample => sample.Keys) >= TypingKeyCount)
        {
            return false;
        }

        var scrolled = _samples.Count(static sample => sample.Notches > 0);
        rate = _samples.Sum(static sample => sample.Notches) / Math.Max(span.TotalSeconds, 1);
        return scrolled >= _samples.Count * UnbrokenCoverage && rate >= UnbrokenNotchesPerSecond;
    }

    public void Reset()
    {
        _samples.Clear();
        _startedAt = null;
    }
}
