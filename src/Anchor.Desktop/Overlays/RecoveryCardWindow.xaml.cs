using Microsoft.UI.Xaml;

namespace Anchor_Desktop.Overlays;

public sealed partial class RecoveryCardWindow : Window
{
    public RecoveryCardWindow()
    {
        InitializeComponent();
        OverlayWindowHelper.Center(this, 500, 340);
    }

    public event EventHandler? ReturnedToTask;
    public event EventHandler? Dismissed;

    public void SetContext(string task, string lastAction, string nextAction, string reason)
    {
        TaskText.Text = task;
        LastActionText.Text = $"Last anchor: {lastAction}";
        NextActionText.Text = $"Next: {nextAction}";
        ReasonText.Text = reason.Replace('_', ' ');
    }

    private void Return_Click(object sender, RoutedEventArgs e) => ReturnedToTask?.Invoke(this, EventArgs.Empty);
    private void Dismiss_Click(object sender, RoutedEventArgs e) => Dismissed?.Invoke(this, EventArgs.Empty);
}
