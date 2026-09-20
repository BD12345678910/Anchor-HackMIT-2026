using Microsoft.UI.Xaml;

namespace Anchor_Desktop.Overlays;

public sealed partial class IntentionGateWindow : Window
{
    public IntentionGateWindow()
    {
        InitializeComponent();
        Activated += (_, args) =>
        {
            IsActive = args.WindowActivationState != WindowActivationState.Deactivated;
            if (!IsActive)
            {
                LostFocus?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>False until the gate actually receives activation, which Windows may refuse to a background app.</summary>
    public bool IsActive { get; private set; }

    public event EventHandler? ReturnedToTask;
    public event EventHandler? ContinuedAnyway;
    public event EventHandler? ParkedForLater;
    public event EventHandler? Disabled;
    public event EventHandler? NeededForTask;
    public event EventHandler? DeliberateBreak;
    public event EventHandler? LostFocus;

    public void SetPrompt(string task, string subtask, string reason)
    {
        TaskText.Text = task;
        SubtaskText.Text = string.IsNullOrWhiteSpace(subtask) ? "Choose the next small action" : $"Current step: {subtask}";
        ReasonText.Text = $"Anchor noticed: {reason.Replace('_', ' ')}";
    }

    private void Return_Click(object sender, RoutedEventArgs e) => ReturnedToTask?.Invoke(this, EventArgs.Empty);
    private void Continue_Click(object sender, RoutedEventArgs e) => ContinuedAnyway?.Invoke(this, EventArgs.Empty);
    private void Park_Click(object sender, RoutedEventArgs e) => ParkedForLater?.Invoke(this, EventArgs.Empty);
    private void Disable_Click(object sender, RoutedEventArgs e) => Disabled?.Invoke(this, EventArgs.Empty);
    private void Needed_Click(object sender, RoutedEventArgs e) => NeededForTask?.Invoke(this, EventArgs.Empty);
    private void Break_Click(object sender, RoutedEventArgs e) => DeliberateBreak?.Invoke(this, EventArgs.Empty);
}
