using System.Drawing;
using System.Windows.Forms;

namespace Anchor_Desktop.Services;

public sealed record TrayTaskSummary(string Goal, string Subtask, bool IsRunning);

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _goal;
    private readonly ToolStripMenuItem _subtask;
    private readonly Func<TrayTaskSummary> _summaryProvider;
    private bool _disposed;

    public TrayIconService(Func<TrayTaskSummary> summaryProvider)
    {
        _summaryProvider = summaryProvider ?? throw new ArgumentNullException(nameof(summaryProvider));
        _goal = new ToolStripMenuItem("No active task") { Enabled = false };
        _subtask = new ToolStripMenuItem("Open Settings to plan a task") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _goal,
            _subtask,
            new ToolStripSeparator(),
            Item("Open Anchor Settings", () => ShowSettingsRequested?.Invoke(this, EventArgs.Empty)),
            Item("I'm distracted", () => ReportDistractedRequested?.Invoke(this, EventArgs.Empty)),
            Item("Emergency release", () => EmergencyReleaseRequested?.Invoke(this, EventArgs.Empty)),
            new ToolStripSeparator(),
            Item("Exit Anchor", () => ExitRequested?.Invoke(this, EventArgs.Empty)),
        ]);
        menu.Opening += (_, _) => RefreshSummary();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _icon = new NotifyIcon
        {
            Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application,
            Text = "Anchor focus assistant",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ShowSettingsRequested;
    public event EventHandler? ReportDistractedRequested;
    public event EventHandler? EmergencyReleaseRequested;
    public event EventHandler? ExitRequested;

    public void RefreshSummary()
    {
        var summary = _summaryProvider();
        _goal.Text = summary.IsRunning ? Bound(summary.Goal, 72) : "No active task";
        _subtask.Text = summary.IsRunning ? $"Current: {Bound(summary.Subtask, 64)}" : "Open Settings to plan a task";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }

    private static ToolStripMenuItem Item(string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    private static string Bound(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim()[..Math.Min(value.Trim().Length, maximum)];
}
