namespace Anchor.Core.Models;

public enum RelevanceClass
{
    Relevant,
    Ambiguous,
    LikelyDetour
}

public sealed record TaskContext(
    string Goal,
    string CurrentSubtask,
    string ProcessName,
    string? WindowTitle,
    string? Domain,
    IReadOnlyList<string> UserRelevantTargets);

public sealed record TaskPlanningResult(
    TaskPlan Plan,
    bool IsFallback,
    string Source,
    string? ErrorCode);

public sealed record RelevanceJudgment(
    double Score,
    RelevanceClass Classification,
    string Reason,
    bool IsFallback,
    DateTimeOffset ExpiresAt);

public sealed record TaskIntelligenceAvailability(
    bool IsConfigured,
    bool IsAvailable,
    string Status);
