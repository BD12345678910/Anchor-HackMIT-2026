namespace Anchor.Core.Services;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// Finds photo-like areas in a BGRA window capture without any knowledge of the app that drew it.
/// Text and UI chrome are mostly two-tone (paper + ink) with little colour; photographs and
/// illustrations are colourful or rich in mid-tones. The frame is split into blocks, each block
/// is scored, and runs of picture-like blocks are grouped into rectangles. Smooth non-paper
/// blocks (a dark studio backdrop, a plain wall, sky) count as picture only when they touch a
/// textured picture block, so a photo is covered whole without swallowing the page around it.
/// </summary>
public static class PictureRegionDetector
{
    public const int BlockSize = 16;
    public const int MinimumEdge = 48;
    public const int MaxRegions = 64;

    private const int MinimumBlocks = 9;
    private const double MinimumFill = 0.45;
    private const double MinimumSeedShare = 0.2;

    internal enum BlockKind : byte
    {
        Page,
        Smooth,
        Picture
    }

    public static IReadOnlyList<PixelRect> Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (width < BlockSize || height < BlockSize || stride < width * 4 || bgra.Length < (long)stride * height)
        {
            return [];
        }

        var columns = width / BlockSize;
        var rows = height / BlockSize;
        var kinds = new BlockKind[rows * columns];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                kinds[row * columns + column] = Classify(bgra, column * BlockSize, row * BlockSize, stride);
            }
        }

        return GroupBlocks(kinds, columns, rows);
    }

    internal static bool IsPictureBlock(ReadOnlySpan<byte> bgra, int left, int top, int stride) =>
        Classify(bgra, left, top, stride) == BlockKind.Picture;

    /// <summary>
    /// <see cref="BlockKind.Picture"/>: clearly colourful, or a broad spread of luminance with many
    /// mid-tones (photographs) rather than the bimodal paper/ink of text.
    /// <see cref="BlockKind.Smooth"/>: low-texture and not page-white, e.g. a photo backdrop; only
    /// kept when connected to a picture block. Everything else (paper, text, flat UI) is
    /// <see cref="BlockKind.Page"/>.
    /// </summary>
    internal static BlockKind Classify(ReadOnlySpan<byte> bgra, int left, int top, int stride)
    {
        const int pixels = BlockSize * BlockSize;
        long chromaSum = 0;
        long lumaSum = 0;
        long lumaSquares = 0;
        var midTones = 0;
        var colourful = 0;
        var paperWhite = 0;

        for (var y = 0; y < BlockSize; y++)
        {
            var offset = (top + y) * stride + left * 4;
            for (var x = 0; x < BlockSize; x++, offset += 4)
            {
                int b = bgra[offset], g = bgra[offset + 1], r = bgra[offset + 2];
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                var chroma = max - min;
                var luma = (r * 299 + g * 587 + b * 114) / 1000;
                chromaSum += chroma;
                lumaSum += luma;
                lumaSquares += luma * luma;
                if (chroma >= 40)
                {
                    colourful++;
                }
                if (luma is >= 56 and <= 200)
                {
                    midTones++;
                }
                if (luma >= 236)
                {
                    paperWhite++;
                }
            }
        }

        var meanChroma = chromaSum / (double)pixels;
        var meanLuma = lumaSum / (double)pixels;
        var variance = lumaSquares / (double)pixels - meanLuma * meanLuma;
        var deviation = Math.Sqrt(Math.Max(0, variance));
        var midToneShare = midTones / (double)pixels;
        var colourfulShare = colourful / (double)pixels;
        var paperShare = paperWhite / (double)pixels;

        if (deviation < 6)
        {
            // Flat fills: page background and UI panels are Page; a dark or tinted flat area could
            // be the plain part of a photo, so it may be absorbed by a neighbouring picture block.
            return paperShare >= 0.5 || meanLuma >= 232 ? BlockKind.Page : BlockKind.Smooth;
        }

        // Colour photos / illustrations: a real share of saturated pixels plus some texture. Dense
        // coloured text on paper (link lists, table cells) is colourful too, but keeps a large
        // paper-white share that photographs do not.
        if (colourfulShare >= 0.35 && meanChroma >= 24 && paperShare < 0.3)
        {
            return BlockKind.Picture;
        }

        // Greyscale or muted photos: lots of mid-tones with texture, unlike two-tone text.
        if (midToneShare >= 0.6 && deviation >= 14)
        {
            return BlockKind.Picture;
        }

        // Gently textured, not mostly paper, and not the ink/paper contrast of text.
        if (paperShare < 0.5 && (deviation < 12 || midToneShare >= 0.35))
        {
            return BlockKind.Smooth;
        }

        return BlockKind.Page;
    }

    private static IReadOnlyList<PixelRect> GroupBlocks(BlockKind[] kinds, int columns, int rows)
    {
        var labels = new int[kinds.Length];
        var results = new List<PixelRect>();
        var stack = new Stack<int>();
        var next = 1;

        for (var start = 0; start < kinds.Length; start++)
        {
            if (kinds[start] != BlockKind.Picture || labels[start] != 0)
            {
                continue;
            }

            var label = next++;
            var grown = new Extent(columns, rows);
            var seedsOnly = new Extent(columns, rows);
            stack.Push(start);
            labels[start] = label;
            while (stack.Count > 0)
            {
                var index = stack.Pop();
                var row = index / columns;
                var column = index % columns;
                grown.Add(column, row);
                if (kinds[index] == BlockKind.Picture)
                {
                    seedsOnly.Add(column, row);
                }

                Visit(index - 1, column > 0);
                Visit(index + 1, column < columns - 1);
                Visit(index - columns, row > 0);
                Visit(index + columns, row < rows - 1);
            }

            // The textured core itself must be picture-sized; a colourful strip along a banner or
            // button edge must not promote the flat fill next to it.
            if (seedsOnly.Count < MinimumBlocks
                || seedsOnly.WidthBlocks * BlockSize < MinimumEdge
                || seedsOnly.HeightBlocks * BlockSize < MinimumEdge)
            {
                continue;
            }

            // Prefer the photo plus its smooth surroundings; if that swallowed too much of the page
            // (a dark theme, a big flat panel), fall back to the textured core alone.
            var chosen = seedsOnly.Count >= grown.Count * MinimumSeedShare && grown.Fill >= MinimumFill
                ? grown
                : seedsOnly;
            if (chosen.Fill < MinimumFill
                || chosen.WidthBlocks * BlockSize < MinimumEdge
                || chosen.HeightBlocks * BlockSize < MinimumEdge)
            {
                continue;
            }

            results.Add(chosen.ToRect());
            if (results.Count >= MaxRegions)
            {
                break;
            }

            void Visit(int neighbour, bool inRange)
            {
                if (inRange && kinds[neighbour] != BlockKind.Page && labels[neighbour] == 0)
                {
                    labels[neighbour] = label;
                    stack.Push(neighbour);
                }
            }
        }

        return results;
    }

    private struct Extent(int columns, int rows)
    {
        private int _minColumn = columns, _minRow = rows, _maxColumn = -1, _maxRow = -1;

        public int Count { get; private set; }
        public int WidthBlocks => _maxColumn - _minColumn + 1;
        public int HeightBlocks => _maxRow - _minRow + 1;
        public double Fill => Count / (double)(WidthBlocks * HeightBlocks);

        public void Add(int column, int row)
        {
            Count++;
            _minColumn = Math.Min(_minColumn, column);
            _maxColumn = Math.Max(_maxColumn, column);
            _minRow = Math.Min(_minRow, row);
            _maxRow = Math.Max(_maxRow, row);
        }

        public PixelRect ToRect() => new(
            _minColumn * BlockSize,
            _minRow * BlockSize,
            WidthBlocks * BlockSize,
            HeightBlocks * BlockSize);
    }
}
