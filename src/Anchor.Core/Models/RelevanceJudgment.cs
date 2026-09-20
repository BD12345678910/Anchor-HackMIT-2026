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
    IReadOnlyList<string> UserRelevantTargets,
    string? ScreenExcerpt = null);

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

/// <summary>One picture on screen, described by the text around it, for semantic grading.</summary>
public sealed record PictureDescriptor(
    string Key,
    string NearbyText,
    bool AdShaped,
    int Width,
    int Height);

public sealed record PictureGradingRequest(
    string Goal,
    string CurrentSubtask,
    string ProcessName,
    string? WindowTitle,
    string ScreenExcerpt,
    IReadOnlyList<PictureDescriptor> Pictures);

public enum PictureRelevance
{
    /// <summary>Illustrates the subject being studied: keep it readable.</summary>
    Illustrates,
    /// <summary>Content, but not about the task.</summary>
    Unrelated,
    /// <summary>Advert, promo, recommendation or other attention bait.</summary>
    Bait
}

public sealed record PictureGrading(
    IReadOnlyDictionary<string, PictureRelevance> Verdicts,
    string Source,
    bool IsFallback);

public sealed record TaskIntelligenceAvailability(
    bool IsConfigured,
    bool IsAvailable,
    string Status);
