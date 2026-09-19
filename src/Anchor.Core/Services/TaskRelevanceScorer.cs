using System.Globalization;
using System.Text;

namespace Anchor.Core.Services;

public static class TaskRelevanceScorer
{
    public static double Score(string? taskTitle, string? processName, string? context)
    {
        var goalTokens = Tokenize(taskTitle);
        if (goalTokens.Count == 0)
        {
            return 0.5;
        }

        var evidence = Tokenize($"{processName} {context}");
        if (evidence.Count == 0)
        {
            return 0.25;
        }

        var overlap = goalTokens.Count(token => evidence.Contains(token));
        var coverage = (double)overlap / goalTokens.Count;
        var score = 0.15 + (0.8 * coverage);

        return Math.Clamp(score, 0, 1);
    }

    private static HashSet<string> Tokenize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new List<char>();

        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.GetUnicodeCategory(character) is UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber)
            {
                buffer.Add(char.ToLowerInvariant(character));
                continue;
            }

            AddToken(tokens, buffer);
        }

        AddToken(tokens, buffer);
        return tokens;
    }

    private static void AddToken(HashSet<string> tokens, List<char> buffer)
    {
        if (buffer.Count == 0)
        {
            return;
        }

        tokens.Add(new string([.. buffer]));
        buffer.Clear();
    }
}
