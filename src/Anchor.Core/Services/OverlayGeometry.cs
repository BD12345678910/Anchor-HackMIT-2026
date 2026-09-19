namespace Anchor.Core.Services;

public sealed record OverlayWorkArea(
    int X,
    int Y,
    int Width,
    int Height,
    double Scale)
{
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
}

public sealed record OverlayBounds(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
}

public static class OverlayGeometry
{
    public static OverlayBounds PlaceTopRight(
        OverlayWorkArea workArea,
        double widthDip,
        double heightDip,
        double marginDip)
    {
        Validate(workArea);
        var width = Math.Min(workArea.Width, Scale(widthDip, workArea.Scale));
        var height = Math.Min(workArea.Height, Scale(heightDip, workArea.Scale));
        var margin = Math.Max(0, Scale(marginDip, workArea.Scale));
        return new OverlayBounds(
            Math.Max(workArea.X, workArea.Right - width - margin),
            Math.Min(workArea.Bottom - height, workArea.Y + margin),
            width,
            height);
    }

    public static OverlayBounds PlaceCentered(
        OverlayWorkArea workArea,
        double widthDip,
        double heightDip)
    {
        Validate(workArea);
        var width = Math.Min(workArea.Width, Scale(widthDip, workArea.Scale));
        var height = Math.Min(workArea.Height, Scale(heightDip, workArea.Scale));
        return new OverlayBounds(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height);
    }

    public static OverlayBounds? PlaceSpotlight(
        OverlayWorkArea workArea,
        double normalizedX,
        double normalizedY,
        double diameterDip,
        bool secureWindow)
    {
        if (secureWindow)
        {
            return null;
        }
        Validate(workArea);
        var diameter = Math.Min(
            Math.Min(workArea.Width, workArea.Height),
            Scale(diameterDip, workArea.Scale));
        var centerX = workArea.X + (int)Math.Round(Math.Clamp(normalizedX, 0, 1) * workArea.Width);
        var centerY = workArea.Y + (int)Math.Round(Math.Clamp(normalizedY, 0, 1) * workArea.Height);
        return new OverlayBounds(
            Math.Clamp(centerX - (diameter / 2), workArea.X, workArea.Right - diameter),
            Math.Clamp(centerY - (diameter / 2), workArea.Y, workArea.Bottom - diameter),
            diameter,
            diameter);
    }

    public static OverlayBounds? PlaceFirewall(
        OverlayWorkArea workArea,
        OverlayBounds foregroundWindow,
        bool secureWindow)
    {
        if (secureWindow)
        {
            return null;
        }
        Validate(workArea);
        var left = Math.Max(workArea.X, foregroundWindow.X);
        var top = Math.Max(workArea.Y, foregroundWindow.Y);
        var right = Math.Min(workArea.Right, foregroundWindow.Right);
        var bottom = Math.Min(workArea.Bottom, foregroundWindow.Bottom);
        return right <= left || bottom <= top
            ? null
            : new OverlayBounds(left, top, right - left, bottom - top);
    }

    private static int Scale(double value, double scale) =>
        Math.Max(1, checked((int)Math.Round(value * scale)));

    private static void Validate(OverlayWorkArea workArea)
    {
        ArgumentNullException.ThrowIfNull(workArea);
        if (workArea.Width <= 0 || workArea.Height <= 0 || workArea.Scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }
    }
}
