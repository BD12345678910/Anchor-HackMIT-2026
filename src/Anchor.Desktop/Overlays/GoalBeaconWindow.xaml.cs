using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Anchor_Desktop.Overlays;

public sealed partial class GoalBeaconWindow : Window
{
    public GoalBeaconWindow()
    {
        InitializeComponent();
        OverlayWindowHelper.Configure(this, 420, 92, clickThrough: true);
    }

    public void SetTask(string title, bool pulse)
    {
        TaskText.Text = title;
        if (!pulse)
        {
            BeaconCard.Opacity = 0.92;
            return;
        }

        var animation = new DoubleAnimation
        {
            From = 0.35,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(260),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3)
        };
        Storyboard.SetTarget(animation, BeaconCard);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }
}
