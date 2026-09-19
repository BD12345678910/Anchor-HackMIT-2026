using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anchor_Desktop.Overlays;

public sealed partial class GazeSpotlightWindow : Window
{
    public GazeSpotlightWindow()
    {
        InitializeComponent();
    }

    public void SetAperture(
        double normalizedX,
        double normalizedY,
        double diameter,
        double viewportWidth,
        double viewportHeight)
    {
        var width = Math.Max(1, viewportWidth);
        var height = Math.Max(1, viewportHeight);
        Layer.Width = width;
        Layer.Height = height;
        var size = Math.Clamp(diameter, 80, Math.Min(width, height));
        var left = Math.Clamp((normalizedX * width) - (size / 2), 0, width - size);
        var top = Math.Clamp((normalizedY * height) - (size / 2), 0, height - size);

        SetRect(TopShade, 0, 0, width, top);
        SetRect(BottomShade, 0, top + size, width, Math.Max(0, height - top - size));
        SetRect(LeftShade, 0, top, left, size);
        SetRect(RightShade, left + size, top, Math.Max(0, width - left - size), size);
        SetRect(FocusRing, left, top, size, size);
    }

    private static void SetRect(FrameworkElement element, double left, double top, double width, double height)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = width;
        element.Height = height;
    }
}
