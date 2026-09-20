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

/// <summary>
/// One picture on screen, described by the text around it and, when the capture is available,
/// a small JPEG thumbnail (as a data URL) so a vision model can judge the pixels themselves.
/// </summary>
public sealed record PictureDescriptor(
    string Key,
    string NearbyText,
    bool AdShaped,
    int Width,
    int Height,
    string? ThumbnailDataUrl = null);

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

/// <summary>One passage of on-screen text (a paragraph, list, card or nav block) in window-local pixels.</summary>
public sealed record TextBlockDescriptor(
    string Key,
    string Text,
    double Left,
    double Top,
    double Width,
    double Height);

public sealed record TextGradingRequest(
    string Goal,
    string CurrentSubtask,
    string ProcessName,
    string? WindowTitle,
    IReadOnlyList<TextBlockDescriptor> Blocks);

public enum TextRelevance
{
    /// <summary>Part of what the user is studying: leave it readable.</summary>
    OnTask,
    /// <summary>Navigation, related links, comments, promos or another topic: dim it.</summary>
    OffTask
}

public sealed record TextGrading(
    IReadOnlyDictionary<string, TextRelevance> Verdicts,
    string Source,
    bool IsFallback);

public sealed record TaskIntelligenceAvailability(
    bool IsConfigured,
    bool IsAvailable,
    string Status);
