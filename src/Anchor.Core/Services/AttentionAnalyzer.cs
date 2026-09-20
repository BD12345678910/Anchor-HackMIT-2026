using Anchor.Core.Models;

namespace Anchor.Core.Services;

/// <summary>Where the user's attention went during distractions: one app or site, how often it pulled them away and how long it kept them.</summary>
public sealed record Distractor(string Name, int Episodes, TimeSpan TimeLost);

public sealed record AttentionAnalysis(
    int Samples,
    int FocusBouts,
    TimeSpan MedianAttentionSpan,
    TimeSpan LongestAttentionSpan,
    TimeSpan CurrentAttentionSpan,
    int Distractions,
    double DistractionsPerHour,
    TimeSpan MedianRecovery,
    double OnTaskShare,
    IReadOnlyList<KeyValuePair<string, int>> Triggers,
    IReadOnlyList<KeyValuePair<InterventionKind, int>> Interventions,
    string Trend,
    IReadOnlyList<Distractor> Distractors,
    TimeSpan TimeLost)
{
    public static AttentionAnalysis Empty { get; } = new(
        0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, 0, TimeSpan.Zero, 0, [], [], "Not enough data yet", [], TimeSpan.Zero);
}

/// <summary>
/// Turns the stream of attention predictions and interventions for one session into
/// personal attention metrics: how long focus bouts last before a drift, how quickly focus
/// returns, what reason codes accompany each drift, and whether spans are lengthening.
/// </summary>
public sealed class AttentionAnalyzer
{
    private readonly List<(DateTimeOffset At, AttentionState State, string[] Reasons, string? Place)> _samples = [];
    private readonly Dictionary<InterventionKind, int> _interventions = [];

    public void Reset()
    {
        _samples.Clear();
        _interventions.Clear();
    }

    /// <param name="place">Where the user was at this moment (site domain, else the app), used to rank what distracts them most.</param>
    public void Record(DateTimeOffset at, AttentionPrediction prediction, string? place = null)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        if (_samples.Count > 0 && at < _samples[^1].At)
        {
            at = _samples[^1].At;
        }

        _samples.Add((at, prediction.State, prediction.ReasonCodes.ToArray(), string.IsNullOrWhiteSpace(place) ? null : place.Trim()));
    }

    /// <summary>The label used for a foreground context: the site when it is a browser page, otherwise the application.</summary>
    public static string? DescribePlace(string? processName, string? domain)
    {
        if (!string.IsNullOrWhiteSpace(domain))
        {
            return domain.Trim().ToLowerInvariant();
        }
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }
        var name = processName.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public void RecordIntervention(InterventionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Kind == InterventionKind.None)
        {
            return;
        }

        _interventions[decision.Kind] = _interventions.GetValueOrDefault(decision.Kind) + 1;
    }

    public AttentionAnalysis Analyze(DateTimeOffset now)
    {
        if (_samples.Count < 2)
        {
            return AttentionAnalysis.Empty;
        }

        var bouts = new List<TimeSpan>();
        var recoveries = new List<TimeSpan>();
        var triggers = new Dictionary<string, int>(StringComparer.Ordinal);
        var onTask = TimeSpan.Zero;
        var total = TimeSpan.Zero;
        var distractions = 0;
        var lost = TimeSpan.Zero;
        var placeTime = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var placeEpisodes = new Dictionary<string, int>(StringComparer.Ordinal);
        string? episodePlace = null;

        DateTimeOffset? boutStart = _samples[0].State == AttentionState.Focused ? _samples[0].At : null;
        DateTimeOffset? distractionStart = null;

        for (var i = 1; i < _samples.Count; i++)
        {
            var previous = _samples[i - 1];
            var current = _samples[i];
            var delta = current.At - previous.At;
            total += delta;
            if (IsOnTask(previous.State))
            {
                onTask += delta;
            }
            else if (previous.State == AttentionState.Distracted)
            {
                lost += delta;
                var place = previous.Place ?? current.Place;
                if (place is not null)
                {
                    placeTime[place] = placeTime.GetValueOrDefault(place) + delta;
                    if (!string.Equals(episodePlace, place, StringComparison.Ordinal))
                    {
                        placeEpisodes[place] = placeEpisodes.GetValueOrDefault(place) + 1;
                        episodePlace = place;
                    }
                }
            }

            if (current.State == AttentionState.Focused && boutStart is null)
            {
                boutStart = current.At;
            }

            if (previous.State != AttentionState.Distracted && current.State == AttentionState.Distracted)
            {
                if (boutStart is not null)
                {
                    bouts.Add(current.At - boutStart.Value);
                    boutStart = null;
                }
                distractions++;
                distractionStart = current.At;
                foreach (var reason in current.Reasons)
                {
                    triggers[reason] = triggers.GetValueOrDefault(reason) + 1;
                }
            }

            if (distractionStart is not null && current.State == AttentionState.Focused)
            {
                recoveries.Add(current.At - distractionStart.Value);
                distractionStart = null;
            }
            if (current.State != AttentionState.Distracted)
            {
                episodePlace = null;
            }
        }

        var openBout = boutStart is null || now < boutStart.Value ? TimeSpan.Zero : now - boutStart.Value;

        List<TimeSpan> closedAndOpen = openBout > TimeSpan.Zero ? [.. bouts, openBout] : bouts;
        var hours = Math.Max(total.TotalHours, 1.0 / 60);
        return new AttentionAnalysis(
            _samples.Count,
            closedAndOpen.Count,
            Median(closedAndOpen),
            closedAndOpen.Count == 0 ? TimeSpan.Zero : closedAndOpen.Max(),
            openBout,
            distractions,
            distractions / hours,
            Median(recoveries),
            total > TimeSpan.Zero ? onTask / total : 0,
            triggers.OrderByDescending(static pair => pair.Value).ThenBy(static pair => pair.Key, StringComparer.Ordinal).ToArray(),
            _interventions.OrderByDescending(static pair => pair.Value).ThenBy(static pair => pair.Key).ToArray(),
            DescribeTrend(bouts),
            placeTime
                .Select(pair => new Distractor(pair.Key, placeEpisodes.GetValueOrDefault(pair.Key), pair.Value))
                .OrderByDescending(static item => item.TimeLost)
                .ThenByDescending(static item => item.Episodes)
                .ThenBy(static item => item.Name, StringComparer.Ordinal)
                .ToArray(),
            lost);
    }

    private static bool IsOnTask(AttentionState state) =>
        state is AttentionState.Focused or AttentionState.Drifting or AttentionState.Recovering;

    private static TimeSpan Median(IReadOnlyList<TimeSpan> values)
    {
        if (values.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var sorted = values.OrderBy(static value => value).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : TimeSpan.FromTicks((sorted[middle - 1].Ticks + sorted[middle].Ticks) / 2);
    }

    private static string DescribeTrend(IReadOnlyList<TimeSpan> bouts)
    {
        if (bouts.Count < 4)
        {
            return bouts.Count == 0
                ? "No completed focus bout yet — keep going."
                : $"{bouts.Count} of the 4 focus bouts needed to compare early vs. late spans.";
        }

        var half = bouts.Count / 2;
        var early = bouts.Take(half).Average(static bout => bout.TotalSeconds);
        var late = bouts.Skip(bouts.Count - half).Average(static bout => bout.TotalSeconds);
        if (early <= 0)
        {
            return "Trend unavailable.";
        }

        var change = (late - early) / early;
        return change switch
        {
            >= 0.2 => $"Focus bouts are lengthening: {Math.Round(late / 60, 1)} min lately vs {Math.Round(early / 60, 1)} min early on.",
            <= -0.2 => $"Focus bouts are shortening: {Math.Round(late / 60, 1)} min lately vs {Math.Round(early / 60, 1)} min early on — consider a break.",
            _ => $"Focus bouts are steady around {Math.Round(late / 60, 1)} min."
        };
    }
}
