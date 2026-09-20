using Anchor.Core.Models;

namespace Anchor.Core.Services;

/// <summary>
/// Offline fallback for screen-based progress detection. It looks for explicit completion
/// markers in the recognised screen text (an accepted verdict, a submitted form, a "complete"
/// banner) combined with the step's own keywords. Confidence is deliberately capped below the
/// auto-complete threshold: without DeepSeek the app suggests, it does not decide.
/// </summary>
public static class LocalProgressJudge
{
    private static readonly string[] CompletionMarkers =
    [
        "accepted", "all tests passed", "correct!", "correct answer", "you passed", "passed all",
        "submission accepted", "success", "completed", "well done", "100%", "all test cases",
        "marked as done", "you got it", "solved", "quiz complete", "lesson complete"
    ];

    private static readonly string[] NegativeMarkers =
    [
        "wrong answer", "time limit exceeded", "runtime error", "compilation error", "incorrect",
        "try again", "failed", "0/", "not accepted"
    ];

    public static ProgressJudgment Judge(ProgressEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var text = (evidence.ScreenText ?? string.Empty).ToLowerInvariant();
        var combined = $"{evidence.WindowTitle}\n{evidence.ScreenText}";
        var match = TaskEvidenceMatcher.Evaluate(evidence.CurrentStep, evidence.Goal, combined);

        if (NegativeMarkers.Any(text.Contains))
        {
            return new ProgressJudgment(false, 0.1, "Screen shows a rejection or error, keep going.", true, "Local rules");
        }

        var marker = CompletionMarkers.FirstOrDefault(text.Contains);
        if (marker is not null && match.Score >= 0.2)
        {
            return new ProgressJudgment(
                true,
                Math.Min(ProgressJudgment.AutoCompleteThreshold - 0.05, 0.45 + match.Score * 0.5),
                $"Screen shows \"{marker}\" while on {evidence.WindowTitle}.",
                true,
                "Local rules");
        }

        if (match.SuggestsCompletion)
        {
            return new ProgressJudgment(
                false,
                Math.Min(0.49, match.Score),
                $"\"{evidence.WindowTitle}\" matches the step but shows no completion marker yet.",
                true,
                "Local rules");
        }

        return new ProgressJudgment(false, 0, "No progress evidence on screen.", true, "Local rules");
    }
}
