using Anchor.Core.Models;

namespace Anchor.Core.Services;

public sealed class InterventionPolicy
{
    private static readonly TimeSpan BeaconCooldown = TimeSpan.FromSeconds(45);
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset _nextAllowedAt = DateTimeOffset.MinValue;
    private InterventionKind _lastKind = InterventionKind.None;

    public InterventionPolicy(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private int _waitLonger;
    private int _speakSooner;

    public double CurrentThreshold { get; private set; } = UserPreferences.Default.MinimumConfidence;

    /// <summary>One line for the UI: how sure Anchor must be before interrupting, and what taught it that.</summary>
    public string PatienceSummary
    {
        get
        {
            var head = CurrentThreshold >= 1
                ? "Gates off — you turned them off"
                : $"Interrupts when ≥ {CurrentThreshold:P0} sure";
            if (_waitLonger == 0 && _speakSooner == 0)
            {
                return head + " · default, nothing learned yet";
            }

            var parts = new List<string>(2);
            if (_waitLonger > 0)
            {
                parts.Add($"waits longer after {_waitLonger} × \"not now\"");
            }

            if (_speakSooner > 0)
            {
                parts.Add($"speaks sooner after {_speakSooner} × \"take me back\"");
            }

            return head + " · " + string.Join(", ", parts);
        }
    }

    public InterventionDecision Decide(
        AttentionPrediction prediction,
        UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        ArgumentNullException.ThrowIfNull(preferences);

        if (!preferences.InterventionsEnabled
            || prediction.ReasonCodes.Contains("secure_window", StringComparer.Ordinal))
        {
            return None("suppressed");
        }

        if (prediction.ReasonCodes.Contains("manual_report", StringComparer.Ordinal))
        {
            return new InterventionDecision(
                InterventionKind.RecoveryCard,
                "manual_report",
                prediction.Confidence,
                TimeSpan.Zero);
        }

        var threshold = Math.Max(CurrentThreshold, preferences.MinimumConfidence);
        if (prediction.Confidence < threshold)
        {
            return None("low_confidence");
        }

        var kind = prediction.State switch
        {
            AttentionState.Drifting => InterventionKind.BeaconPulse,
            AttentionState.Distracted => InterventionKind.IntentionGate,
            AttentionState.Recovering or AttentionState.Stuck => InterventionKind.RecoveryCard,
            _ => InterventionKind.None
        };

        if (kind == InterventionKind.None)
        {
            return None("not_needed");
        }

        var now = _clock();
        if (now >= _nextAllowedAt)
        {
            _lastKind = InterventionKind.None;
        }
        else if (Severity(kind) <= Severity(_lastKind))
        {
            return None("cooldown");
        }

        var cooldown = kind == InterventionKind.BeaconPulse
            ? (BeaconCooldown < preferences.InterventionCooldown ? BeaconCooldown : preferences.InterventionCooldown)
            : preferences.InterventionCooldown;
        _lastKind = kind;
        _nextAllowedAt = now + cooldown;
        return new InterventionDecision(
            kind,
            prediction.ReasonCodes.FirstOrDefault() ?? "attention_state",
            prediction.Confidence,
            cooldown);
    }

    public void RecordResponse(InterventionResponse response)
    {
        switch (response)
        {
            case InterventionResponse.Dismissed:
            case InterventionResponse.Snoozed:
                _waitLonger++;
                CurrentThreshold = Math.Min(0.9, CurrentThreshold + 0.05);
                break;
            case InterventionResponse.ReturnedToTask:
            case InterventionResponse.NeededForTask:
                _speakSooner++;
                CurrentThreshold = Math.Max(0.55, CurrentThreshold - 0.02);
                break;
            case InterventionResponse.Disabled:
                CurrentThreshold = 1;
                break;
        }
    }

    private static int Severity(InterventionKind kind) => kind switch
    {
        InterventionKind.None => 0,
        InterventionKind.BeaconPulse => 1,
        InterventionKind.VisualFilter => 2,
        InterventionKind.RecoveryCard => 3,
        InterventionKind.BreakSuggestion => 3,
        InterventionKind.IntentionGate => 4,
        _ => 0
    };

    private static InterventionDecision None(string reason) =>
        new(InterventionKind.None, reason, 0, TimeSpan.Zero);
}
