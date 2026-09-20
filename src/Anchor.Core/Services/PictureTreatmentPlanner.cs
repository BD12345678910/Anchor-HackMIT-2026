using System.Security.Cryptography;
using System.Text;
using Anchor.Core.Models;

namespace Anchor.Core.Services;

/// <summary>How much detail to strip from one picture.</summary>
public enum PictureTreatment
{
    /// <summary>Picture illustrates the task, or nothing says it does not: leave the pixels alone.</summary>
    Keep,
    /// <summary>Unrelated content picture: coarse pixels, shape and colour remain.</summary>
    Pixelate,
    /// <summary>Ad-like or off-task: heavy mosaic.</summary>
    Mosaic
}

/// <summary>What the blur pass knows about the front window when it plans each picture.</summary>
public sealed record PictureSceneContext(
    bool WindowRelevant,
    IReadOnlyCollection<string> TaskTokens,
    IReadOnlyList<ScreenLine> Lines,
    int WindowWidth,
    int WindowHeight)
{
    public static PictureSceneContext OffTask(int width, int height) => new(false, [], [], width, height);
}

/// <summary>
/// Turns detected pictures into descriptors (the caption / adjacent text, whether the shape
/// is ad-like) and maps a semantic verdict — DeepSeek's when configured, otherwise the local
/// vocabulary heuristic — onto a treatment. Pictures in an off-task window are always mosaicked.
/// </summary>
public static class PictureTreatmentPlanner
{
    private const double CaptionReach = 72;
    private const double SideTextReach = 48;
    private const double RailShare = 0.28;
    public const int MaxNearbyChars = 220;

    /// <param name="pixelHash">Perceptual hash of the picture's pixels, when the capture is available; makes the key follow the picture rather than its caption.</param>
    /// <param name="thumbnailDataUrl">Small JPEG of the picture as a data URL for vision grading.</param>
    public static PictureDescriptor Describe(PixelRect region, PictureSceneContext context, string? pixelHash = null, string? thumbnailDataUrl = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var nearby = Bound(string.Join(' ', NearbyText(region, context.Lines)), MaxNearbyChars);
        var adShaped = LooksLikeAd(region, context.WindowWidth, context.WindowHeight);
        var key = !string.IsNullOrEmpty(pixelHash)
            ? "pix:" + pixelHash
            : nearby.Length > 0
            ? "text:" + Hash(Normalize(nearby))
            : $"shape:{SizeBucket(region.Width)}x{SizeBucket(region.Height)}:{(adShaped ? "ad" : "content")}";
        return new PictureDescriptor(key, nearby, adShaped, region.Width, region.Height, thumbnailDataUrl);
    }

    /// <summary>Treatment for a picture given the window verdict and, when known, its semantic verdict.</summary>
    public static PictureTreatment Plan(PictureDescriptor picture, PictureSceneContext context, PictureRelevance? verdict)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.WindowRelevant)
        {
            return PictureTreatment.Mosaic;
        }
        return verdict switch
        {
            PictureRelevance.Illustrates => PictureTreatment.Keep,
            PictureRelevance.Unrelated => PictureTreatment.Pixelate,
            PictureRelevance.Bait => PictureTreatment.Mosaic,
            // Verdict still pending: on a relevant page leave pictures alone rather than
            // punishing the user while the grader catches up; only ad shapes are mosaicked.
            _ => picture.AdShaped ? PictureTreatment.Mosaic : PictureTreatment.Keep
        };
    }

    /// <summary>
    /// Heuristic used when DeepSeek is unavailable. Only positive evidence degrades a picture:
    /// an ad shape or promotional wording beside it. A caption that merely fails to repeat the
    /// goal's words says nothing — the page is already known to be on task, so the picture stays.
    /// </summary>
    public static PictureGrading LocalGrade(PictureGradingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var verdicts = new Dictionary<string, PictureRelevance>(StringComparer.Ordinal);
        foreach (var picture in request.Pictures)
        {
            verdicts[picture.Key] = picture.AdShaped || Matches(picture.NearbyText, PromoWords)
                ? PictureRelevance.Bait
                : PictureRelevance.Illustrates;
        }
        return new PictureGrading(verdicts, "Local rules", true);
    }

    /// <summary>Mosaic cells across the shorter side; fewer cells means less detail survives.</summary>
    public static int CellsAcrossShortSide(PictureTreatment treatment) => treatment switch
    {
        PictureTreatment.Keep => 0,
        PictureTreatment.Pixelate => 18,
        _ => 9
    };

    public static IReadOnlyCollection<string> TaskTokens(params string?[] sources)
    {
        var tokens = TaskEvidenceMatcher.Tokenize(string.Join(' ', sources.Where(static s => !string.IsNullOrWhiteSpace(s))));
        tokens.RemoveWhere(static token => token.Length < 3 || token.All(char.IsDigit) || CommonWords.Contains(token));
        return tokens;
    }

    private static bool Matches(string nearbyText, IReadOnlyCollection<string> taskTokens)
    {
        if (nearbyText.Length == 0 || taskTokens.Count == 0)
        {
            return false;
        }
        var nearby = TaskEvidenceMatcher.Tokenize(nearbyText);
        return taskTokens.Any(token => nearby.Any(word => SameWord(word, token)));
    }

    private static bool SameWord(string left, string right)
    {
        if (left.Length < 3 || right.Length < 3)
        {
            return false;
        }

        var shorter = left.Length <= right.Length ? left : right;
        var longer = ReferenceEquals(shorter, left) ? right : left;
        return longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase)
            && longer.Length - shorter.Length <= 3;
    }

    private static bool LooksLikeAd(PixelRect region, int windowWidth, int windowHeight)
    {
        if (windowWidth <= 0 || windowHeight <= 0)
        {
            return false;
        }

        var aspect = region.Width / (double)Math.Max(1, region.Height);
        var thinBanner = aspect >= 3.5 && region.Height <= 140;
        var skyscraper = aspect <= 0.45 && region.Height >= 300;
        if (thinBanner || skyscraper)
        {
            return true;
        }

        var centre = region.X + region.Width / 2.0;
        var inRail = centre < windowWidth * RailShare || centre > windowWidth * (1 - RailShare);
        var narrow = region.Width <= windowWidth * 0.3;
        var boxAd = Math.Abs(aspect - 1.2) <= 0.15 && region.Width is >= 240 and <= 360;
        return inRail && narrow && boxAd;
    }

    private static IEnumerable<string> NearbyText(PixelRect region, IReadOnlyList<ScreenLine> lines)
    {
        var left = region.X;
        var right = region.X + region.Width;
        var top = region.Y;
        var bottom = region.Y + region.Height;
        foreach (var line in lines)
        {
            var lineRight = line.Left + line.Width;
            var lineBottom = line.Top + line.Height;
            var horizontalOverlap = Math.Min(right, lineRight) - Math.Max(left, line.Left);
            var verticalOverlap = Math.Min(bottom, lineBottom) - Math.Max(top, line.Top);
            var isCaption = horizontalOverlap > Math.Min(region.Width, line.Width) * 0.3
                && (line.Top >= bottom && line.Top - bottom <= CaptionReach
                    || lineBottom <= top && top - lineBottom <= CaptionReach * 0.6);
            var isBeside = verticalOverlap > 0
                && (line.Left >= right && line.Left - right <= SideTextReach
                    || lineRight <= left && left - lineRight <= SideTextReach);
            if (isCaption || isBeside)
            {
                yield return line.Text;
            }
        }
    }

    private static int SizeBucket(int pixels) => Math.Max(1, pixels / 80);

    private static string Normalize(string value) =>
        string.Join(' ', value.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private static string Bound(string value, int maximum)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum];
    }

    /// <summary>Wording beside a picture that marks it as promotion rather than page content.</summary>
    private static readonly HashSet<string> PromoWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "advert", "advertisement", "sponsored", "promoted", "promotion", "discount",
        "subscribe", "trending", "recommended"
    };

    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "read", "reading", "article", "page",
        "wikipedia", "open", "finish", "complete", "learn", "about", "study", "write", "notes",
        "problem", "problems", "solve", "chapter", "section", "watch", "video", "step"
    };
}
