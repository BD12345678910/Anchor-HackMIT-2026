using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class OverlayGeometryTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1040, 1.0)]
    [InlineData(0, 0, 2560, 1400, 1.5)]
    [InlineData(-1920, -120, 1920, 1080, 1.0)]
    public void Beacon_and_recovery_stay_inside_selected_work_area(
        int x,
        int y,
        int width,
        int height,
        double scale)
    {
        var work = new OverlayWorkArea(x, y, width, height, scale);

        var beacon = OverlayGeometry.PlaceTopRight(work, 500, 118, 24);
        var recovery = OverlayGeometry.PlaceCentered(work, 720, 380);

        AssertInside(beacon, work);
        AssertInside(recovery, work);
        Assert.Equal(x + width - (int)Math.Round(500 * scale) - (int)Math.Round(24 * scale), beacon.X);
    }

    [Fact]
    public void Spotlight_aperture_clamps_to_negative_secondary_monitor_edges()
    {
        var work = new OverlayWorkArea(-1920, -120, 1920, 1080, 1.5);

        var topLeft = OverlayGeometry.PlaceSpotlight(work, 0, 0, 180, secureWindow: false);
        var bottomRight = OverlayGeometry.PlaceSpotlight(work, 1, 1, 180, secureWindow: false);

        Assert.NotNull(topLeft);
        Assert.NotNull(bottomRight);
        AssertInside(topLeft!, work);
        AssertInside(bottomRight!, work);
    }

    [Fact]
    public void Firewall_is_intersection_of_foreground_window_and_work_area()
    {
        var work = new OverlayWorkArea(-1920, 0, 1920, 1040, 1);
        var foreground = new OverlayBounds(-2100, -50, 1200, 900);

        var result = OverlayGeometry.PlaceFirewall(work, foreground, secureWindow: false);

        Assert.Equal(new OverlayBounds(-1920, 0, 1020, 850), result);
    }

    [Fact]
    public void Secure_windows_return_no_spotlight_or_firewall_bounds()
    {
        var work = new OverlayWorkArea(0, 0, 1920, 1040, 1);

        Assert.Null(OverlayGeometry.PlaceSpotlight(work, 0.5, 0.5, 180, secureWindow: true));
        Assert.Null(OverlayGeometry.PlaceFirewall(
            work,
            new OverlayBounds(10, 10, 1000, 800),
            secureWindow: true));
    }

    private static void AssertInside(OverlayBounds bounds, OverlayWorkArea work)
    {
        Assert.True(bounds.X >= work.X);
        Assert.True(bounds.Y >= work.Y);
        Assert.True(bounds.Right <= work.Right);
        Assert.True(bounds.Bottom <= work.Bottom);
    }
}

