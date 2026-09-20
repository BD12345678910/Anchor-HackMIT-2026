namespace Anchor.Core.Services;

/// <summary>Keys pressed in one sampling window, split by what they do.</summary>
public sealed record KeyStrokeCounts(
    int Letters = 0,
    int Digits = 0,
    int Navigation = 0,
    int Editing = 0,
    int Modifiers = 0,
    int Function = 0,
    int Other = 0)
{
    public static KeyStrokeCounts Empty { get; } = new();

    public int Total => Letters + Digits + Navigation + Editing + Modifiers + Function + Other;

    /// <summary>Keys that put characters into a document or move a caret while editing.</summary>
    public int Productive => Letters + Digits + Editing;

    /// <summary>Keys that change nothing by themselves: function row, punctuation-less stray keys.</summary>
    public int Unproductive => Function + Other;
}

/// <summary>How the last few seconds at the keyboard looked.</summary>
public sealed record KeyboardBehavior(
    bool IsRandom,
    double KeysPerSecond,
    double UnproductiveShare,
    DateTimeOffset? StartedAt,
    string Description);

/// <summary>
/// Detects hammering the keyboard rather than working with it. Real typing lands somewhere: there
/// is a caret to receive it, the mix is mostly letters, digits and edits, and the text on screen
/// changes. Drift at the keyboard looks different — typing into a page that accepts no text, a run
/// of function and stray keys that do nothing, or the arrow keys held down to skim. Content is
/// judged separately by <see cref="TypingQualityAnalyzer"/>; this analyzer only sees the shape and
/// the rate, so it works in any application, including ones Anchor cannot read text out of.
/// </summary>
public sealed class KeyboardBehaviorAnalyzer
{
    /// <summary>Too few keys in the window to say anything about them.</summary>
    public const int MinimumKeys = 10;

    /// <summary>Above this share of keys that do nothing, the keyboard is being hit, not used.</summary>
    public const double UnproductiveShare = 0.5;
    public const double UnproductiveKeysPerSecond = 2;

    /// <summary>Characters arriving where nothing can receive them.</summary>
    public const double BurstKeysPerSecond = 2;
    public const int BurstKeys = 12;

    /// <summary>Arrow and page keys held down to skim rather than read.</summary>
    public const double NavigationShare = 0.8;
    public const double NavigationKeysPerSecond = 5;
    public const int NavigationKeys = 15;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly List<Sample> _samples = [];
    private DateTimeOffset? _startedAt;

    /// <param name="screenChanged">
    /// Whether the text Anchor can read on screen changed since the previous window, or null when
    /// no fresh reading was available — in which case the "nothing appeared" rule stays silent.
    /// </param>
    /// <param name="hasTextCaret">
    /// Whether the foreground window is showing a text caret, i.e. something is willing to accept
    /// characters. Null when it could not be determined.
    /// </param>
    public KeyboardBehavior Observe(
        DateTimeOffset at,
        KeyStrokeCounts counts,
        bool? screenChanged,
        bool? hasTextCaret = null)
    {
        ArgumentNullException.ThrowIfNull(counts);

        _samples.Add(new Sample(at, counts, screenChanged, hasTextCaret));
        _samples.RemoveAll(sample => at - sample.At > Window);

        var span = _samples.Count == 0 ? TimeSpan.Zero : at - _samples[0].At;
        var seconds = Math.Max(span.TotalSeconds, 1);
        var total = _samples.Sum(static sample => sample.Counts.Total);
        var unproductive = _samples.Sum(static sample => sample.Counts.Unproductive);
        var navigation = _samples.Sum(static sample => sample.Counts.Navigation);
        var rate = total / seconds;
        var unproductiveShare = total == 0 ? 0 : unproductive / (double)total;
        var navigationShare = total == 0 ? 0 : navigation / (double)total;
        // Text appearing anywhere Anchor can read clears the keyboard: the characters landed.
        var textAppeared = _samples.Any(static sample => sample.ScreenChanged == true);
        // A page with no caret cannot be receiving the characters, whatever they are.
        var nowhereToType = _samples.Any(static sample => sample.HasTextCaret == false)
            && _samples.All(static sample => sample.HasTextCaret != true);

        var hammering = total >= MinimumKeys
            && unproductiveShare >= UnproductiveShare
            && rate >= UnproductiveKeysPerSecond;
        var producingNothing = total >= BurstKeys
            && rate >= BurstKeysPerSecond
            && nowhereToType
            && !textAppeared;
        var skimming = total >= NavigationKeys
            && navigationShare >= NavigationShare
            && rate >= NavigationKeysPerSecond;
        var random = hammering || producingNothing || skimming;

        if (random)
        {
            _startedAt ??= _samples[0].At;
        }
        else
        {
            _startedAt = null;
        }

        var description = random
            ? hammering
                ? $"hitting keys that do nothing ({unproductiveShare:P0} of {total} keys)"
                : producingNothing
                    ? $"typing into a page that takes no text ({rate:0.#} keys/s)"
                    : $"holding the arrow keys ({rate:0.#} keys/s)"
            : $"{rate:0.#} keys/s";

        return new KeyboardBehavior(random, rate, unproductiveShare, _startedAt, description);
    }

    public void Reset()
    {
        _samples.Clear();
        _startedAt = null;
    }

    private readonly record struct Sample(
        DateTimeOffset At,
        KeyStrokeCounts Counts,
        bool? ScreenChanged,
        bool? HasTextCaret);
}
