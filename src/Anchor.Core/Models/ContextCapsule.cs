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
    DistractionReason Reason);

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
    bool IsSensitiveField);
