using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class PictureRegionDetectorTests
{
    private const int Width = 320;
    private const int Height = 240;

    [Fact]
    public void Text_on_paper_is_not_a_picture()
    {
        var frame = new Frame();
        frame.FillText(0, 0, Width, Height);

        Assert.Empty(PictureRegionDetector.Detect(frame.Pixels, Width, Height, frame.Stride));
    }

    [Fact]
    public void Dense_link_text_on_paper_is_not_a_picture()
    {
        var frame = new Frame();
        frame.FillText(0, 0, Width, Height, 51, 102, 204, dense: true);

        Assert.Empty(PictureRegionDetector.Detect(frame.Pixels, Width, Height, frame.Stride));
    }

    [Fact]
    public void Colour_photo_inside_text_is_found_where_it_is()
    {
        var frame = new Frame();
        frame.FillText(0, 0, Width, Height);
        frame.FillPhoto(64, 48, 128, 96, greyscale: false);

        var region = Assert.Single(PictureRegionDetector.Detect(frame.Pixels, Width, Height, frame.Stride));
        Assert.InRange(region.X, 48, 64);
        Assert.InRange(region.Y, 32, 48);
        Assert.InRange(region.Right, 192, 208);
        Assert.InRange(region.Bottom, 144, 160);
    }

    [Fact]
    public void Greyscale_photo_is_found_by_its_midtones()
    {
        var frame = new Frame();
        frame.FillText(0, 0, Width, Height);
        frame.FillPhoto(160, 96, 128, 128, greyscale: true);

        Assert.Single(PictureRegionDetector.Detect(frame.Pixels, Width, Height, frame.Stride));
    }

    [Fact]
    public void Flat_colour_banner_and_tiny_icons_are_ignored()
    {
        var frame = new Frame();
        frame.FillText(0, 0, Width, Height);
        frame.FillSolid(0, 0, Width, 40, r: 30, g: 110, b: 220);
        frame.FillPhoto(200, 200, 24, 24, greyscale: false);

        Assert.Empty(PictureRegionDetector.Detect(frame.Pixels, Width, Height, frame.Stride));
    }

    [Fact]
    public void Photo_with_plain_dark_backdrop_is_covered_whole()
    {
        var frame = new Frame();
        frame.FillText(0, 0, Width, Height);
        frame.FillSolid(64, 48, 160, 128, r: 18, g: 18, b: 22);
        frame.FillPhoto(112, 80, 64, 64, greyscale: false);

        var region = Assert.Single(PictureRegionDetector.Detect(frame.Pixels, Width, Height, frame.Stride));
        Assert.InRange(region.X, 48, 64);
        Assert.InRange(region.Y, 32, 48);
        Assert.InRange(region.Right, 224, 240);
        Assert.InRange(region.Bottom, 176, 192);
    }

    [Fact]
    public void Dark_theme_page_does_not_get_swallowed_around_a_photo()
    {
        var frame = new Frame();
        frame.FillSolid(0, 0, Width, Height, r: 24, g: 26, b: 30);
        frame.FillPhoto(160, 96, 128, 96, greyscale: false);

        var region = Assert.Single(PictureRegionDetector.Detect(frame.Pixels, Width, Height, frame.Stride));
        Assert.InRange(region.X, 144, 160);
        Assert.InRange(region.Y, 80, 96);
        Assert.InRange(region.Right, 288, 304);
        Assert.InRange(region.Bottom, 192, 208);
    }

    private sealed class Frame
    {
        public int Stride => Width * 4;
        public byte[] Pixels { get; } = new byte[Width * Height * 4];

        public void FillSolid(int x, int y, int w, int h, byte r, byte g, byte b)
        {
            for (var yy = y; yy < y + h; yy++)
            {
                for (var xx = x; xx < x + w; xx++)
                {
                    Set(xx, yy, r, g, b);
                }
            }
        }

        /// <summary>White paper with black glyph-like strokes every few pixels.</summary>
        public void FillText(int x, int y, int w, int h) => FillText(x, y, w, h, 20, 20, 20, dense: false);

        public void FillText(int x, int y, int w, int h, byte r, byte g, byte b, bool dense)
        {
            for (var yy = y; yy < y + h; yy++)
            {
                for (var xx = x; xx < x + w; xx++)
                {
                    var ink = dense
                        ? yy % 12 is >= 2 and <= 9 && (xx % 4 is 0 or 1 || yy % 12 is 2 or 9)
                        : yy % 14 is >= 3 and <= 10 && (xx % 7 is 1 or 2 || yy % 14 is 3 or 10);
                    if (ink)
                    {
                        Set(xx, yy, r, g, b);
                    }
                    else
                    {
                        Set(xx, yy, 250, 250, 250);
                    }
                }
            }
        }

        /// <summary>Smoothly varying tones, like a photograph.</summary>
        public void FillPhoto(int x, int y, int w, int h, bool greyscale)
        {
            var random = new Random(7);
            for (var yy = y; yy < y + h; yy++)
            {
                for (var xx = x; xx < x + w; xx++)
                {
                    var t = (xx - x) / (double)w;
                    var s = (yy - y) / (double)h;
                    var noise = random.Next(-12, 13);
                    if (greyscale)
                    {
                        var v = (byte)Math.Clamp(120 + 60 * Math.Sin(t * 6) * Math.Cos(s * 4) + noise * 2, 0, 255);
                        Set(xx, yy, v, v, v);
                    }
                    else
                    {
                        Set(
                            xx,
                            yy,
                            (byte)Math.Clamp(180 + 60 * Math.Sin(t * 5) + noise, 0, 255),
                            (byte)Math.Clamp(90 + 70 * Math.Cos(s * 3) + noise, 0, 255),
                            (byte)Math.Clamp(60 + 40 * t + noise, 0, 255));
                    }
                }
            }
        }

        private void Set(int x, int y, byte r, byte g, byte b)
        {
            var offset = y * Stride + x * 4;
            Pixels[offset] = b;
            Pixels[offset + 1] = g;
            Pixels[offset + 2] = r;
            Pixels[offset + 3] = 255;
        }
    }
}
