using System.Globalization;
using System.Text;

namespace Anchor.Core.Services;

public static class TaskRelevanceScorer
{
    public const double SelfWindowScore = 0.75;
    public const double NeutralToolScore = 0.5;
    public const double KnownDetourScore = 0.05;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "of", "to", "for", "in", "on", "at", "by", "with", "my", "our",
        "do", "does", "finish", "complete", "work", "start", "then", "some", "few", "this", "that",
        "up", "out", "into", "from", "it", "is", "are", "be", "i", "we", "you", "me", "1st", "2nd", "3rd",
        "first", "second", "third", "next", "last", "one", "two", "three", "four", "five", "six", "seven",
        "eight", "nine", "ten", "step", "part",
    };

    private static readonly string[] DetourMarkers =
    [
        "youtube", "tiktok", "netflix", "twitch", "instagram", "facebook", "reddit", "twitter", "x.com",
        "bilibili", "douyin", "weibo", "9gag", "pinterest", "snapchat", "discord", "steam", "epic games",
        "hulu", "disney+", "primevideo", "prime video", "crunchyroll", "tumblr", "imgur", "buzzfeed",
        "9anime", "kick.com", "onlyfans", "roblox", "minecraft", "league of legends", "valorant", "fortnite",
    ];

    private static readonly string[] ToolProcesses =
    [
        "code", "devenv", "rider", "idea", "idea64", "pycharm", "pycharm64", "clion", "clion64", "webstorm",
        "goland", "sublime_text", "notepad", "notepad++", "acrobat", "acrord32", "sumatrapdf", "foxit",
        "winword", "excel", "powerpnt", "onenote", "obsidian", "notion", "logseq", "windowsterminal", "cmd",
        "powershell", "pwsh", "wt", "explorer", "typora", "zotero", "mendeleydesktop", "anki", "calc",
        "matlab", "rstudio", "jupyter", "geogebra", "texstudio", "texworks", "overleaf", "wordpad",
    ];

    public static double Score(string? taskTitle, string? processName, string? context)
    {
        var goalTokens = MeaningfulTokens(taskTitle);
        if (goalTokens.Count == 0)
        {
            return 0.5;
        }

        var process = (processName ?? string.Empty).Trim();
        if (process.Equals("Anchor", StringComparison.OrdinalIgnoreCase))
        {
            return SelfWindowScore;
        }

        var evidence = Tokenize($"{process} {context}");
        if (evidence.Count == 0)
        {
            return 0.25;
        }

        var overlap = goalTokens.Count(goal => evidence.Any(token => TokensMatch(goal, token)));
        var coverage = (double)overlap / goalTokens.Count;
        var score = overlap switch
        {
            0 => 0.15,
            1 => Math.Max(0.62, 0.15 + (0.8 * coverage)),
            _ => Math.Max(0.75, 0.15 + (0.8 * coverage))
        };

        if (overlap == 0)
        {
            var haystack = $"{process} {context}".ToLowerInvariant();
            if (DetourMarkers.Any(marker => haystack.Contains(marker, StringComparison.Ordinal)))
            {
                return KnownDetourScore;
            }

            if (ToolProcesses.Contains(process.ToLowerInvariant()))
            {
                return NeutralToolScore;
            }
        }

        return Math.Clamp(score, 0, 1);
    }

    private static bool TokensMatch(string goal, string evidence)
    {
        if (string.Equals(goal, evidence, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (goal.Length < 4 || evidence.Length < 4)
        {
            return false;
        }

        return goal.StartsWith(evidence, StringComparison.OrdinalIgnoreCase)
            || evidence.StartsWith(goal, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> MeaningfulTokens(string? value)
    {
        var tokens = Tokenize(value);
        tokens.RemoveWhere(token => StopWords.Contains(token) || token.All(char.IsDigit) || token.Length < 2);
        return tokens.Count == 0 ? Tokenize(value) : tokens;
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
