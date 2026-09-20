using Anchor.Core.Models;

namespace Anchor.Core.Services;

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
    string Trend)
{
    public static AttentionAnalysis Empty { get; } = new(
        0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, 0, TimeSpan.Zero, 0, [], [], "Not enough data yet");
}

/// <summary>
/// Turns the stream of attention predictions and interventions for one session into
/// personal attention metrics: how long focus bouts last before a drift, how quickly focus
/// returns, what reason codes accompany each drift, and whether spans are lengthening.
/// </summary>
public sealed class AttentionAnalyzer
{
    private readonly List<(DateTimeOffset At, AttentionState State, string[] Reasons)> _samples = [];
    private readonly Dictionary<InterventionKind, int> _interventions = [];

    public void Reset()
    {
        _samples.Clear();
        _interventions.Clear();
    }

    public void Record(DateTimeOffset at, AttentionPrediction prediction)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        if (_samples.Count > 0 && at < _samples[^1].At)
        {
            at = _samples[^1].At;
        }

        _samples.Add((at, prediction.State, prediction.ReasonCodes.ToArray()));
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
            DescribeTrend(bouts));
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
                : "Trend needs at least four focus bouts.";
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
