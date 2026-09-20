namespace Anchor.Core.Models;

public sealed record ContextCapsule(
    Guid SessionId,
    DateTimeOffset CapturedAt,
    string TaskTitle,
    string Application,
    string DocumentIdentity,
    string Location,
    string LastAction,
    string NextAction,
    string? SelectedText,
    string? RestoreTarget,
    DistractionReason Reason,
    string CurrentSubtask = "",
    string RelevanceReason = "",
    DateTimeOffset? EvidenceTimestamp = null,
    bool IsEstimatedContext = false,
    ActivityKind Activity = ActivityKind.Unknown,
    string? FocusText = null,
    FocusSource FocusSource = FocusSource.None,
    string? ScreenExcerpt = null,
    int KeyCount = 0,
    int ScrollReversalCount = 0);

public enum DistractionReason
{
    AppSwitch,
    ManualReport,
    IdleReturn,
    LostGaze,
    ReadingSkip,
    StuckPhrase
}

public sealed record ContextObservation(
    string Application,
    string DocumentIdentity,
    string Location,
    string LastAction,
    string NextAction,
    string? SelectedText,
    string? RestoreTarget,
    double Confidence,
    bool IsSensitiveField,
    string CurrentSubtask = "",
    string RelevanceReason = "",
    DateTimeOffset? EvidenceTimestamp = null,
    bool IsEstimatedContext = false,
    ActivityKind Activity = ActivityKind.Unknown,
    string? FocusText = null,
    FocusSource FocusSource = FocusSource.None,
    string? ScreenExcerpt = null,
    int KeyCount = 0,
    int ScrollReversalCount = 0);
