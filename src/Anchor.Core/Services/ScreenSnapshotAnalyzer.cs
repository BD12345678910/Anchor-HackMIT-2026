using System.Security.Cryptography;
using System.Text;
using Anchor.Core.Models;

namespace Anchor.Core.Services;

/// <summary>
/// Pure logic over OCR output: picks the line the user was most likely looking at and builds
/// the short excerpt around it. Platform code supplies the lines and the candidate focus points.
/// </summary>
public static class ScreenSnapshotAnalyzer
{
    public const int ExcerptLinesBefore = 3;
    public const int ExcerptLinesAfter = 2;
    public const int MaxExcerptChars = 1_200;
    public const int MaxScreenTextChars = 3_000;

    public static ScreenSnapshot Build(
        DateTimeOffset timestamp,
        string processName,
        string windowTitle,
        IReadOnlyList<ScreenLine> rawLines,
        (double X, double Y)? gazePoint,
        (double X, double Y)? caretPoint,
        (double X, double Y)? pointerPoint,
        double windowWidth,
        double windowHeight)
    {
        var lines = rawLines
            .Where(static line => !string.IsNullOrWhiteSpace(line.Text) && line.Text.Trim().Length >= 2)
            .OrderBy(static line => line.Top)
            .ThenBy(static line => line.Left)
            .ToArray();

        (double X, double Y) point;
        FocusSource source;
        if (gazePoint is { } gaze)
        {
            point = gaze;
            source = FocusSource.Gaze;
        }
        else if (caretPoint is { } caret)
        {
            point = caret;
            source = FocusSource.Caret;
        }
        else if (pointerPoint is { } pointer)
        {
            point = pointer;
            source = FocusSource.Pointer;
        }
        else
        {
            point = (windowWidth / 2, windowHeight * 0.4);
            source = FocusSource.Viewport;
        }

        ScreenLine? focus = null;
        var focusIndex = -1;
        if (lines.Length > 0)
        {
            var best = double.MaxValue;
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var dy = Math.Abs(line.CenterY - point.Y);
                var dx = point.X < line.Left ? line.Left - point.X : point.X > line.Left + line.Width ? point.X - (line.Left + line.Width) : 0;
                var distance = dy * 1.0 + dx * 0.35;
                if (distance < best)
                {
                    best = distance;
                    focus = line;
                    focusIndex = index;
                }
            }
        }

        if (lines.Length == 0)
        {
            source = FocusSource.None;
        }

        var excerpt = BuildExcerpt(lines, focusIndex);
        var hash = Hash(lines);
        return new ScreenSnapshot(timestamp, processName, windowTitle, lines, focus, source, excerpt, hash);
    }

    /// <summary>All recognised text, top to bottom, bounded for a prompt.</summary>
    public static string FlattenText(IReadOnlyList<ScreenLine> lines, int maxChars = MaxScreenTextChars)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            var text = line.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }
            if (builder.Length + text.Length + 1 > maxChars)
            {
                break;
            }
            builder.Append(text).Append('\n');
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// One-line summary of a screen for the reading trail: heading-like lines (short, taller than
    /// the median line) plus the top and bottom body lines, so a judge can see how far the user
    /// scrolled through a section over successive screens.
    /// </summary>
    public static string TrailEntry(IReadOnlyList<ScreenLine> lines, int maxChars = 160)
    {
        var texts = lines.Where(static line => line.Text.Trim().Length >= 2).ToArray();
        if (texts.Length == 0)
        {
            return string.Empty;
        }

        var medianHeight = texts.Select(static line => line.Height).Order().ElementAt(texts.Length / 2);
        var headings = texts
            .Where(line => IsHeadingLike(line, medianHeight))
            .Select(static line => line.Text.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        var body = $"top \u201c{Clip(texts[0].Text.Trim(), 45)}\u201d \u2026 bottom \u201c{Clip(texts[^1].Text.Trim(), 45)}\u201d";
        var entry = headings.Length == 0 ? body : $"headings: {string.Join(" / ", headings)} · {body}";
        return Clip(entry, maxChars);
    }

    private static bool IsHeadingLike(ScreenLine line, double medianHeight)
    {
        var text = line.Text.Trim();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is >= 1 and <= 6
            && text.Length <= 48
            && !text.EndsWith('.')
            && !text.EndsWith(',')
            && char.IsLetter(text[0])
            && line.Height >= medianHeight * 1.25;
    }

    private static string Clip(string value, int length) =>
        value.Length <= length ? value : value[..(length - 1)].TrimEnd() + "\u2026";

    private static string BuildExcerpt(ScreenLine[] lines, int focusIndex)
    {
        if (lines.Length == 0)
        {
            return string.Empty;
        }
        var start = focusIndex < 0 ? 0 : Math.Max(0, focusIndex - ExcerptLinesBefore);
        var end = focusIndex < 0 ? Math.Min(lines.Length, ExcerptLinesBefore + ExcerptLinesAfter + 1) : Math.Min(lines.Length, focusIndex + ExcerptLinesAfter + 1);
        var builder = new StringBuilder();
        for (var index = start; index < end; index++)
        {
            var text = lines[index].Text.Trim();
            if (builder.Length + text.Length + 4 > MaxExcerptChars)
            {
                break;
            }
            builder.Append(index == focusIndex ? "» " : "  ").Append(text).Append('\n');
        }
        return builder.ToString().TrimEnd();
    }

    private static string Hash(IReadOnlyList<ScreenLine> lines)
    {
        var text = string.Join('\n', lines.Select(static line => line.Text.Trim().ToLowerInvariant()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }
}
