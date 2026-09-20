using System.Security.Cryptography;
using System.Text;
using Anchor.Core.Models;

namespace Anchor.Core.Services;

/// <summary>
/// Groups OCR lines into passages (paragraphs, list blocks, cards) so each passage can be graded
/// against the task and dimmed as a unit. Grouping is geometric: consecutive lines with a small
/// vertical gap and overlapping horizontal extent belong to the same passage.
/// </summary>
public static class TextBlockPlanner
{
    public const int MaxBlocks = 40;
    public const int MaxBlockChars = 600;
    private const int MinBlockChars = 12;
    private const double GapFactor = 0.9;
    private const double MinHorizontalOverlap = 0.25;

    public static IReadOnlyList<TextBlockDescriptor> Group(IReadOnlyList<ScreenLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var ordered = lines
            .Where(static line => line.Width > 0 && line.Height > 0 && !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(static line => line.Top)
            .ThenBy(static line => line.Left)
            .ToList();
        var blocks = new List<TextBlockDescriptor>();
        var current = new List<ScreenLine>();
        foreach (var line in ordered)
        {
            if (current.Count > 0 && !Continues(current[^1], current, line))
            {
                Flush(current, blocks);
                current.Clear();
            }
            current.Add(line);
        }
        Flush(current, blocks);
        return blocks
            .OrderByDescending(static block => block.Text.Length)
            .Take(MaxBlocks)
            .OrderBy(static block => block.Top)
            .ToList();
    }

    /// <summary>Everything is readable when no grader is configured: dimming is never applied by rules alone.</summary>
    public static TextGrading LocalGrade(TextGradingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var verdicts = request.Blocks.ToDictionary(static block => block.Key, static _ => TextRelevance.OnTask, StringComparer.Ordinal);
        return new TextGrading(verdicts, "Local rules · text is not dimmed without DeepSeek", true);
    }

    private static bool Continues(ScreenLine previous, List<ScreenLine> block, ScreenLine line)
    {
        var lineHeight = Math.Max(previous.Height, line.Height);
        var gap = line.Top - (previous.Top + previous.Height);
        if (gap > lineHeight * GapFactor || gap < -lineHeight)
        {
            return false;
        }
        var blockLeft = block.Min(static l => l.Left);
        var blockRight = block.Max(static l => l.Left + l.Width);
        var overlap = Math.Min(blockRight, line.Left + line.Width) - Math.Max(blockLeft, line.Left);
        var narrower = Math.Min(blockRight - blockLeft, line.Width);
        return overlap >= narrower * MinHorizontalOverlap;
    }

    private static void Flush(List<ScreenLine> block, List<TextBlockDescriptor> blocks)
    {
        if (block.Count == 0)
        {
            return;
        }
        var text = string.Join(' ', block.Select(static l => l.Text.Trim()));
        if (text.Length < MinBlockChars)
        {
            return;
        }
        if (text.Length > MaxBlockChars)
        {
            text = text[..MaxBlockChars];
        }
        var left = block.Min(static l => l.Left);
        var top = block.Min(static l => l.Top);
        var right = block.Max(static l => l.Left + l.Width);
        var bottom = block.Max(static l => l.Top + l.Height);
        var normalized = string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var key = "text:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        blocks.Add(new TextBlockDescriptor(key, text, left, top, right - left, bottom - top));
    }
}
