using System.Text.RegularExpressions;

namespace Anchor.Core.Services;

/// <summary>
/// What the text someone just typed looks like: real words and code, or keyboard mashing.
/// </summary>
public sealed record TypingQuality(bool IsGibberish, double WordLikeness, string Description);

/// <summary>
/// Tells thinking apart from mashing. Writing and coding both stall for long stretches while the
/// person thinks, so silence says nothing; what does say something is the shape of the characters
/// that arrive — "asdkjhasdkjh", a held-down key, or a run of consonants no language produces.
/// Code is deliberately tolerated: identifiers, snake_case, symbols and short keywords all count
/// as word-like, so only text that is overwhelmingly unpronounceable is called gibberish.
/// </summary>
public static partial class TypingQualityAnalyzer
{
    /// <summary>Below this share of word-like tokens the text stops reading as language.</summary>
    public const double GibberishWordLikeness = 0.4;

    /// <summary>Fewer tokens than this is too little evidence to judge.</summary>
    public const int MinimumTokens = 4;

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"(.)\1{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedCharacter();

    [GeneratedRegex(@"[bcdfghjklmnpqrstvwxz]{5,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsonantRun();

    public static TypingQuality Assess(string? text)
    {
        var value = text?.Trim() ?? string.Empty;
        var tokens = Token().Matches(value)
            .Select(match => match.Value)
            .Where(static token => token.Length > 0)
            .ToArray();

        if (tokens.Length < MinimumTokens)
        {
            return new TypingQuality(false, 1, "too little text to judge");
        }

        var wordLike = tokens.Count(IsWordLike) / (double)tokens.Length;
        var mashed = RepeatedCharacter().IsMatch(value);
        var gibberish = wordLike < GibberishWordLikeness || (mashed && wordLike < 0.7);
        var description = gibberish
            ? mashed
                ? "repeated characters and unpronounceable words"
                : $"{wordLike:P0} of the words are pronounceable"
            : "reads like words or code";
        return new TypingQuality(gibberish, wordLike, description);
    }

    private static bool IsWordLike(string token)
    {
        if (token.Length <= 3)
        {
            // Keywords, operators-as-words and short identifiers ("if", "i", "x2") are fine.
            return true;
        }

        if (token.Any(char.IsDigit) || token.Contains('_', StringComparison.Ordinal))
        {
            return true;
        }

        if (HasCamelHump(token))
        {
            return true;
        }

        if (ConsonantRun().IsMatch(token))
        {
            return false;
        }

        var vowels = token.Count(static character => "aeiouyAEIOUY".Contains(character, StringComparison.Ordinal));
        var ratio = vowels / (double)token.Length;
        return ratio is >= 0.2 and <= 0.75;
    }

    private static bool HasCamelHump(string token)
    {
        for (var index = 1; index < token.Length; index++)
        {
            if (char.IsUpper(token[index]) && char.IsLower(token[index - 1]))
            {
                return true;
            }
        }

        return false;
    }
}
