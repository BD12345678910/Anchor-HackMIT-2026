using Microsoft.UI.Xaml;

namespace Anchor_Desktop.Overlays;

public sealed partial class RecoveryCardWindow : Window
{
    private string _task = string.Empty;
    private string _lastAction = string.Empty;
    private string _nextAction = string.Empty;

    public RecoveryCardWindow()
    {
        InitializeComponent();
        OverlayWindowHelper.Center(this, 720, 380);
    }

    public event EventHandler? ReturnedToTask;
    public event EventHandler? Dismissed;

    public void SetContext(string task, string lastAction, string nextAction, string reason)
    {
        _task = task;
        _lastAction = lastAction;
        _nextAction = nextAction;
        TaskText.Text = task;
        LastActionText.Text = $"Last anchor: {lastAction}";
        NextActionText.Text = $"Next: {nextAction}";
        ReasonText.Text = reason.Replace('_', ' ');
    }

    private void Return_Click(object sender, RoutedEventArgs e) => ReturnedToTask?.Invoke(this, EventArgs.Empty);
    private void Dismiss_Click(object sender, RoutedEventArgs e) => Dismissed?.Invoke(this, EventArgs.Empty);
    private void Reopen_Click(object sender, RoutedEventArgs e)
    {
        ReasonText.Text = "Anchor cannot safely reopen this target, so it has highlighted the saved context instead.";
        LastActionText.Text = $"Saved place: {_lastAction}";
    }

    private void Recap_Click(object sender, RoutedEventArgs e)
    {
        ReasonText.Text = $"You were working on “{_task}”. {_lastAction} The intended continuation is: {_nextAction}";
    }

    private void BreakDown_Click(object sender, RoutedEventArgs e)
    {
        NextActionText.Text = $"Smallest next step: open the task window, find the saved place, then {_nextAction.ToLowerInvariant()}";
    }
}
