using Microsoft.UI.Xaml;

namespace Anchor_Desktop.Overlays;

public sealed partial class IntentionGateWindow : Window
{
    public IntentionGateWindow()
    {
        InitializeComponent();
        OverlayWindowHelper.Center(this, 540, 320);
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                LostFocus?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public event EventHandler? ReturnedToTask;
    public event EventHandler? ContinuedAnyway;
    public event EventHandler? ParkedForLater;
    public event EventHandler? Disabled;
    public event EventHandler? LostFocus;

    public void SetPrompt(string task, string reason)
    {
        TaskText.Text = task;
        ReasonText.Text = $"Anchor noticed: {reason.Replace('_', ' ')}";
    }

    private void Return_Click(object sender, RoutedEventArgs e) => ReturnedToTask?.Invoke(this, EventArgs.Empty);
    private void Continue_Click(object sender, RoutedEventArgs e) => ContinuedAnyway?.Invoke(this, EventArgs.Empty);
    private void Park_Click(object sender, RoutedEventArgs e) => ParkedForLater?.Invoke(this, EventArgs.Empty);
    private void Disable_Click(object sender, RoutedEventArgs e) => Disabled?.Invoke(this, EventArgs.Empty);
}
