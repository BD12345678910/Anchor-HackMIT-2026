using Anchor.Core.Models;

namespace Anchor.Core.Services;

/// <summary>
/// Builds the recovery-card reminder without any cloud model, from the frozen capsule alone.
/// The wording changes with the kind of work the user was doing, because "the last sentence
/// you read" is only meaningful for reading; a coder needs the file and line, a solver the problem.
/// </summary>
public static class LocalContextReminder
{
    public static ContextReminder Compose(ContextCapsule capsule)
    {
        ArgumentNullException.ThrowIfNull(capsule);
        var app = string.IsNullOrWhiteSpace(capsule.Application) ? "your work" : capsule.Application;
        var document = string.IsNullOrWhiteSpace(capsule.DocumentIdentity) ? app : capsule.DocumentIdentity;
        var focus = string.IsNullOrWhiteSpace(capsule.FocusText) ? null : Quote(capsule.FocusText);
        var anchorSource = capsule.FocusSource switch
        {
            FocusSource.Gaze => "your eyes were on",
            FocusSource.Caret => "your cursor was at",
            FocusSource.Pointer => "your pointer was near",
            FocusSource.Viewport => "on screen was",
            _ => "you had"
        };
        var step = string.IsNullOrWhiteSpace(capsule.CurrentSubtask) ? capsule.TaskTitle : capsule.CurrentSubtask;

        var (headline, where) = capsule.Activity switch
        {
            ActivityKind.Reading => (
                $"You were reading {document}",
                focus is null
                    ? $"You were part-way through {document} ({capsule.Location})."
                    : $"The last line {anchorSource} {focus}."),
            ActivityKind.Coding => (
                $"You were coding in {document}",
                focus is null
                    ? $"You had typed {capsule.KeyCount} keys in {app} and were editing {document}."
                    : $"You were editing near {focus} in {document}."),
            ActivityKind.Writing => (
                $"You were writing in {document}",
                focus is null
                    ? $"You had been typing in {document} ({capsule.KeyCount} keys in the last window)."
                    : $"Your last sentence was near {focus}."),
            ActivityKind.ProblemSolving => (
                $"You were working on {document}",
                focus is null
                    ? $"You were on the problem shown in {app}; step: {step}."
                    : $"The problem text {anchorSource} {focus}."),
            ActivityKind.Browsing => (
                $"You were looking things up for {step}",
                focus is null
                    ? $"The page was {document}."
                    : $"You were on {document}; {anchorSource} {focus}."),
            ActivityKind.Watching => (
                $"You were watching {document}",
                $"That was part of {step}; note where you paused before continuing."),
            _ => (
                $"You were on {step}",
                focus is null
                    ? $"{document} was in front ({capsule.Location})."
                    : $"{document} was in front; {anchorSource} {focus}.")
        };

        var resume = capsule.Activity switch
        {
            ActivityKind.Reading => $"Reopen {document}, find that line and read the next paragraph.",
            ActivityKind.Coding => $"Go back to {document} and finish the change you were in the middle of.",
            ActivityKind.Writing => $"Return to {document} and finish the sentence you were writing.",
            ActivityKind.ProblemSolving => $"Re-read the problem statement once, then continue: {step}.",
            ActivityKind.Browsing => $"Return to {document} and pull out the one fact you needed for: {step}.",
            _ => string.IsNullOrWhiteSpace(capsule.NextAction) ? $"Continue with: {step}." : capsule.NextAction
        };

        return new ContextReminder(headline, where, resume, "Local recall", IsFallback: true);
    }

    private static string Quote(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 160)
        {
            trimmed = trimmed[..157].TrimEnd() + "…";
        }
        return $"“{trimmed}”";
    }
}
