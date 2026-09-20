using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Anchor_Desktop.Overlays;

/// <summary>
/// Dims everything except a circle around the gaze point. Windows cannot have see-through holes,
/// so the surround is built from four translucent shade windows (this window is the top shade).
/// </summary>
public sealed partial class GazeSpotlightWindow : Window
{
    private const byte ShadeAlpha = 0xA8;
    private static readonly Windows.UI.Color ShadeColor = Windows.UI.Color.FromArgb(255, 7, 11, 18);

    private readonly Window[] _sideShades =
    [
        CreateShade(),
        CreateShade(),
        CreateShade(),
    ];

    public GazeSpotlightWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            foreach (var shade in _sideShades)
            {
                shade.Close();
            }
        };
    }

    public void SetAperture(double normalizedX, double normalizedY, double diameter, RectInt32 work)
    {
        var width = Math.Max(1, work.Width);
        var height = Math.Max(1, work.Height);
        var size = (int)Math.Clamp(diameter, 80, Math.Min(width, height));
        var left = (int)Math.Clamp((normalizedX * width) - (size / 2.0), 0, width - size);
        var top = (int)Math.Clamp((normalizedY * height) - (size / 2.0), 0, height - size);

        Place(this, work.X, work.Y, width, top);
        Place(_sideShades[0], work.X, work.Y + top + size, width, height - top - size);
        Place(_sideShades[1], work.X, work.Y + top, left, size);
        Place(_sideShades[2], work.X + left + size, work.Y + top, width - left - size, size);
    }

    private static void Place(Window window, int x, int y, int width, int height)
    {
        OverlayWindowHelper.ConfigureBounds(
            window,
            new RectInt32(x, y, Math.Max(1, width), Math.Max(1, height)),
            clickThrough: true);
        OverlayWindowHelper.MakeTranslucent(window, ShadeAlpha);
    }

    private static Window CreateShade() => new()
    {
        Title = "Anchor overlay",
        Content = new Grid { Background = new SolidColorBrush(ShadeColor) },
    };
}
