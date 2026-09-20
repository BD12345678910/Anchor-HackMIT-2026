using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Anchor.Core.Services;

namespace Anchor_Desktop.Overlays;

public sealed partial class GoalBeaconWindow : Window
{
    public GoalBeaconWindow()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user presses Done on the beacon to tick the current step.</summary>
    public event EventHandler? StepMarkedDone;

    private void DoneButton_Click(object sender, RoutedEventArgs e) => StepMarkedDone?.Invoke(this, EventArgs.Empty);

    public void SetGoal(
        string goal,
        string currentSubtask,
        string progressLabel,
        bool emphasize,
        bool reducedMotion)
    {
        GoalText.Text = goal;
        SubtaskText.Text = currentSubtask;
        ProgressText.Text = progressLabel;
        DoneButton.Visibility = string.Equals(currentSubtask, "Task complete", StringComparison.Ordinal)
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (!emphasize)
        {
            BeaconCard.Opacity = 0.92;
            BeaconCard.BorderThickness = new Thickness(1);
            BeaconTranslation.X = 0;
            return;
        }

        if (reducedMotion)
        {
            BeaconCard.BorderThickness = new Thickness(3);
            var borderAnimation = new DoubleAnimation
            {
                From = 1,
                To = 0.78,
                Duration = TimeSpan.FromMilliseconds(350),
                AutoReverse = true
            };
            Storyboard.SetTarget(borderAnimation, BeaconCard);
            Storyboard.SetTargetProperty(borderAnimation, "Opacity");
            var reducedStoryboard = new Storyboard();
            reducedStoryboard.Children.Add(borderAnimation);
            reducedStoryboard.Completed += (_, _) =>
            {
                BeaconCard.BorderThickness = new Thickness(1);
                BeaconCard.Opacity = 0.92;
            };
            reducedStoryboard.Begin();
            return;
        }

        var animation = new DoubleAnimationUsingKeyFrames();
        foreach (var frame in BeaconAnimationModel.CreateShake(reducedMotion: false))
        {
            animation.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(frame.At),
                Value = frame.X
            });
        }

        Storyboard.SetTarget(animation, BeaconTranslation);
        Storyboard.SetTargetProperty(animation, "X");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => BeaconTranslation.X = 0;
        storyboard.Begin();
    }
}
