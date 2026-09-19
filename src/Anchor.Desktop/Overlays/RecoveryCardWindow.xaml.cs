using Microsoft.UI.Xaml;

namespace Anchor_Desktop.Overlays;

public sealed partial class RecoveryCardWindow : Window
{
    private string _task = string.Empty;
    private string _lastAction = string.Empty;
    private string _nextAction = string.Empty;
    private Uri? _restoreTarget;

    public RecoveryCardWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? ReturnedToTask;
    public event EventHandler? Dismissed;
    public event EventHandler? BreakRequested;
    public event EventHandler? SmallerStepRequested;

    public void SetContext(
        string task,
        string subtask,
        string lastAction,
        string nextAction,
        string reason,
        string relevanceReason,
        DateTimeOffset? evidenceTimestamp,
        bool estimated,
        string? restoreTarget)
    {
        _task = task;
        _lastAction = lastAction;
        _nextAction = nextAction;
        _restoreTarget = Uri.TryCreate(restoreTarget, UriKind.Absolute, out var target)
            && target.Scheme is "http" or "https"
                ? target
                : null;
        TaskText.Text = task;
        SubtaskText.Text = string.IsNullOrWhiteSpace(subtask) ? "Current step unavailable" : $"Current step: {subtask}";
        LastActionText.Text = $"Last anchor: {lastAction}";
        NextActionText.Text = $"Next: {nextAction}";
        ReasonText.Text = reason.Replace('_', ' ');
        EvidenceText.Text = string.IsNullOrWhiteSpace(relevanceReason) ? "" : $"Why this context: {relevanceReason}";
        SourceText.Text = estimated
            ? "Estimated from the active task plan · no safe window content was stored"
            : $"Saved locally{(evidenceTimestamp is null ? "" : $" · {evidenceTimestamp.Value.LocalDateTime:t}")}";
    }

    private void Return_Click(object sender, RoutedEventArgs e) => ReturnedToTask?.Invoke(this, EventArgs.Empty);
    private void Dismiss_Click(object sender, RoutedEventArgs e) => Dismissed?.Invoke(this, EventArgs.Empty);
    private void Break_Click(object sender, RoutedEventArgs e) => BreakRequested?.Invoke(this, EventArgs.Empty);
    private async void Reopen_Click(object sender, RoutedEventArgs e)
    {
        if (_restoreTarget is not null && await Windows.System.Launcher.LaunchUriAsync(_restoreTarget))
        {
            ReasonText.Text = "Reopened the validated page origin. Use the saved progress marker to restore your exact place.";
            return;
        }
        ReasonText.Text = "No validated reopen target is available, so Anchor highlighted the saved context instead.";
        LastActionText.Text = $"Saved place: {_lastAction}";
    }

    private void Recap_Click(object sender, RoutedEventArgs e)
    {
        ReasonText.Text = $"You were working on “{_task}”. {_lastAction} The intended continuation is: {_nextAction}";
    }

    private void BreakDown_Click(object sender, RoutedEventArgs e)
    {
        SmallerStepRequested?.Invoke(this, EventArgs.Empty);
        SourceText.Text = "Breaking the step down…";
    }

    public void SetSmallerStep(string step, string source)
    {
        NextActionText.Text = $"Smallest next step: {step}";
        SourceText.Text = source;
    }
}
