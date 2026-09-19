namespace Anchor.Core.Models;

public enum InterventionKind
{
    None,
    BeaconPulse,
    VisualFilter,
    IntentionGate,
    RecoveryCard,
    BreakSuggestion
}

public sealed record InterventionDecision(
    InterventionKind Kind,
    string ReasonCode,
    double Confidence,
    TimeSpan Cooldown);
