namespace Anchor.Core.Models;

/// <summary>What kind of work the user was doing, inferred from the monitored signals.</summary>
public enum ActivityKind
{
    Unknown,
    Reading,
    Writing,
    Coding,
    ProblemSolving,
    Browsing,
    Watching
}

/// <summary>Where the "focus point" used to pick the anchored line came from.</summary>
public enum FocusSource
{
    None,
    Gaze,
    Caret,
    Pointer,
    Viewport
}

/// <summary>One line of recognised on-screen text, in window-local pixels.</summary>
public sealed record ScreenLine(string Text, double Left, double Top, double Width, double Height)
{
    public double CenterY => Top + Height / 2;
    public double CenterX => Left + Width / 2;
}

/// <summary>
/// A privacy-bounded snapshot of the front window's text, captured locally with OCR.
/// <see cref="FocusLine"/> is the line closest to the gaze point (camera on), the caret,
/// or the pointer; <see cref="Excerpt"/> is a short window of lines around it.
/// </summary>
public sealed record ScreenSnapshot(
    DateTimeOffset Timestamp,
    string ProcessName,
    string WindowTitle,
    IReadOnlyList<ScreenLine> Lines,
    ScreenLine? FocusLine,
    FocusSource FocusSource,
    string Excerpt,
    string ContentHash);

/// <summary>Evidence handed to the task-intelligence layer to decide whether a step is done.</summary>
public sealed record ProgressEvidence(
    string Goal,
    IReadOnlyList<TaskStep> Steps,
    int CompletedCount,
    TaskStep CurrentStep,
    string ProcessName,
    string WindowTitle,
    string ScreenText,
    ActivityKind Activity,
    IReadOnlyList<string> RecentlyCompletedEvidence,
    IReadOnlyList<string>? ScreenTrail = null,
    TimeSpan TimeOnStep = default)
{
    /// <summary>Headings/first lines of the screens seen since the step became active, oldest first.</summary>
    public IReadOnlyList<string> Trail => ScreenTrail ?? [];
}

public sealed record ProgressJudgment(
    bool StepCompleted,
    double Confidence,
    string Evidence,
    bool IsFallback,
    string Source)
{
    public const double AutoCompleteThreshold = 0.75;
    public const double SuggestThreshold = 0.5;
}

/// <summary>The task-specific "here is where you were" text shown on the recovery card.</summary>
public sealed record ContextReminder(
    string Headline,
    string WhereYouWere,
    string ResumeWith,
    string Source,
    bool IsFallback);
