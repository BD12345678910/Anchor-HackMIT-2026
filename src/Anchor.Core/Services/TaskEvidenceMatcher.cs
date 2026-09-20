using System.Globalization;
using System.Text;
using Anchor.Core.Models;

namespace Anchor.Core.Services;

public sealed record TaskEvidenceMatch(double Score, IReadOnlyList<string> MatchedTokens)
{
    public const double SuggestionThreshold = 0.34;

    public bool SuggestsCompletion => Score >= SuggestionThreshold;
}

/// <summary>
/// Scores how strongly a piece of user evidence (a typed note, a window title, a page title)
/// indicates that the current task step is being worked on or has been finished.
/// </summary>
public static class TaskEvidenceMatcher
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "the", "of", "to", "for", "on", "in", "is", "are", "you", "your", "will",
        "work", "open", "pick", "first", "one", "its", "it", "with", "from", "that", "this", "do",
        "complete", "finish", "finished", "checked", "noted", "name", "what", "learned", "review",
        "step", "current", "next", "then", "into", "at", "by", "as", "or", "be"
    };

    public static TaskEvidenceMatch Evaluate(TaskStep step, string goal, string? evidence)
    {
        ArgumentNullException.ThrowIfNull(step);
        var evidenceTokens = Tokenize(evidence);
        if (evidenceTokens.Count == 0)
        {
            return new TaskEvidenceMatch(0, []);
        }

        var stepTokens = Tokenize(step.Title);
        var goalTokens = Tokenize(goal);
        var keyTokens = stepTokens.Concat(goalTokens)
            .Where(static token => !StopWords.Contains(token) && !IsNumber(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keyTokens.Length == 0)
        {
            return new TaskEvidenceMatch(0, []);
        }

        var matched = keyTokens
            .Where(token => evidenceTokens.Contains(token) || evidenceTokens.Any(item => SharesStem(item, token)))
            .ToArray();
        var coverage = (double)matched.Length / keyTokens.Length;
        var hasIdentifier = evidenceTokens.Any(static token => IsNumber(token) || LooksLikeIdentifier(token));
        var score = coverage * 0.8 + (hasIdentifier && matched.Length > 0 ? 0.2 : 0);
        return new TaskEvidenceMatch(Math.Clamp(score, 0, 1), matched);
    }

    internal static bool SharesStem(string left, string right)
    {
        if (left.Length < 4 || right.Length < 4)
        {
            return false;
        }

        var prefix = Math.Min(left.Length, right.Length) - 1;
        prefix = Math.Max(prefix, 4);
        return left.Length >= prefix && right.Length >= prefix
            && string.Compare(left, 0, right, 0, prefix, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static bool IsNumber(string token) =>
        token.All(char.IsDigit);

    private static bool LooksLikeIdentifier(string token) =>
        token.Any(char.IsDigit) && token.Any(char.IsLetter);

    internal static HashSet<string> Tokenize(string? value)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return tokens;
        }

        var buffer = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.GetUnicodeCategory(character) is UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber)
            {
                buffer.Append(char.ToLowerInvariant(character));
                continue;
            }

            Flush(tokens, buffer);
        }

        Flush(tokens, buffer);
        return tokens;
    }

    private static void Flush(HashSet<string> tokens, StringBuilder buffer)
    {
        if (buffer.Length > 0)
        {
            tokens.Add(buffer.ToString());
            buffer.Clear();
        }
    }
}
