using System.Text.RegularExpressions;
using Anchor.Core.Models;

namespace Anchor.Core.Services;

/// <summary>
/// Reads "where in the document" from what is on screen, so a reminder can name the page the user
/// was on rather than just the file. PDF readers, e-book readers and browsers all print the page
/// somewhere (toolbar, status bar, footer, window title); OCR sees it like any other text.
/// </summary>
public static partial class DocumentPositionReader
{
    /// <summary>"Page 7 of 30", "page 7 / 30", "7 of 30", "(7/30)".</summary>
    [GeneratedRegex(@"(?:^|\b)(?:page|p\.)?\s*(\d{1,4})\s*(?:of|/)\s*(\d{1,4})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageOfTotal();

    /// <summary>"Page 7" on its own, e.g. a running header.</summary>
    [GeneratedRegex(@"\bpage\s+(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageOnly();

    /// <summary>
    /// The most likely page indicator on screen, or <c>null</c> when nothing names a page.
    /// Window-title matches are used only when the screen itself shows nothing.
    /// </summary>
    public static string? DescribePage(IReadOnlyList<ScreenLine>? lines, string? windowTitle = null)
    {
        foreach (var candidate in Candidates(lines))
        {
            if (Describe(candidate) is { } page)
            {
                return page;
            }
        }

        return string.IsNullOrWhiteSpace(windowTitle) ? null : Describe(windowTitle);
    }

    private static IEnumerable<string> Candidates(IReadOnlyList<ScreenLine>? lines)
    {
        if (lines is null || lines.Count == 0)
        {
            yield break;
        }

        // Short lines first: a page counter is a toolbar or footer label, never a paragraph.
        foreach (var line in lines
                     .Where(static line => line.Text.Trim().Length is > 0 and <= 40)
                     .OrderBy(static line => line.Text.Trim().Length))
        {
            yield return line.Text;
        }
    }

    private static string? Describe(string text)
    {
        var value = text.Trim();
        var pair = PageOfTotal().Match(value);
        if (pair.Success
            && int.TryParse(pair.Groups[1].Value, out var page)
            && int.TryParse(pair.Groups[2].Value, out var total)
            && page >= 1
            && total >= page)
        {
            return $"page {page} of {total}";
        }

        var single = PageOnly().Match(value);
        return single.Success && int.TryParse(single.Groups[1].Value, out var only) && only >= 1
            ? $"page {only}"
            : null;
    }
}
