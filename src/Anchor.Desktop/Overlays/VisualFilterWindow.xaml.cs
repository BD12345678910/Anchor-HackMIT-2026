using Microsoft.UI.Xaml;

namespace Anchor_Desktop.Overlays;

public sealed partial class VisualFilterWindow : Window
{
    public VisualFilterWindow()
    {
        InitializeComponent();
        OverlayWindowHelper.Configure(this, 0, 0, clickThrough: true, fullScreen: true);
    }
}
