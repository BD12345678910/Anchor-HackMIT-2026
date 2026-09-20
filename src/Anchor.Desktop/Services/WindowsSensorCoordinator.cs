using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Anchor.Core.Models;
using Anchor.Core.Services;
using Anchor.Infrastructure.Windows;
using Anchor.Infrastructure.Browser;

namespace Anchor_Desktop.Services;

public sealed class WindowsSensorCoordinator : ISensorCoordinator, IDisposable
{
    private readonly BrowserContextTracker? _browserContext;
    private Channel<DerivedEvent>? _events;
    private ForegroundWindowSensor? _foreground;
    private InputActivitySensor? _input;
    private Guid _sessionId;

    public ChannelReader<DerivedEvent>? Events => _events?.Reader;
    public InputActivitySensor? Input => _input;

    public WindowsSensorCoordinator(BrowserContextTracker? browserContext = null)
    {
        _browserContext = browserContext;
    }

    public Task StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_foreground is not null)
        {
            throw new InvalidOperationException("Windows sensors are already active.");
        }

        _sessionId = sessionId;
        _events = SensorEventChannel.Create(256);
        _input = new InputActivitySensor(sessionId);
        _foreground = new ForegroundWindowSensor(sessionId);
        _foreground.Start(_events.Writer);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _foreground?.Dispose();
        _foreground = null;
        _input = null;
        _events?.Writer.TryComplete();
        _events = null;
        _sessionId = Guid.Empty;
        return Task.CompletedTask;
    }

    public void AttachWindow(IntPtr windowHandle)
    {
        if (windowHandle != IntPtr.Zero)
        {
            InputActivitySensor.RegisterRawInput(windowHandle);
        }
    }

    public SensorWindow Sample(string taskTitle, bool workerAvailable, DateTimeOffset now)
    {
        if (_sessionId == Guid.Empty || _input is null)
        {
            throw new InvalidOperationException("Windows sensors are not active.");
        }

        var inputEvent = _input.Snapshot(now);
        _events?.Writer.TryWrite(inputEvent);
        _foreground?.Refresh();
        var foreground = _foreground?.LastEvent;
        var process = GetFeature(foreground, "process");
        var title = GetFeature(foreground, "title");
        var secure = bool.TryParse(GetFeature(foreground, "secure_window"), out var isSecure) && isSecure;
        var switches = int.TryParse(
            GetFeature(foreground, "app_switch_count"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var switchCount)
            ? switchCount
            : 0;

        return SensorWindow.Create(
            keyCount: ParseInt(inputEvent, "key_count"),
            mouseDistance: ParseDouble(inputEvent, "mouse_distance"),
            idleSeconds: IdleTimeSensor.GetIdleSeconds(),
            appRelevance: TaskRelevanceScorer.Score(taskTitle, process, title),
            gazePresence: 0.5,
            appSwitchCount: switches,
            scrollReversalCount: ParseInt(inputEvent, "scroll_reversal_count"),
            isSecureWindow: secure,
            isWorkerAvailable: workerAvailable,
            timestamp: now);
    }

    /// <summary>Feeds raw input to the active session (if any) and returns the key-down virtual key.</summary>
    public ushort? ProcessRawInput(IntPtr rawInputHandle) =>
        _input is null
            ? InputActivitySensor.PeekKeyDown(rawInputHandle)
            : _input.ProcessRawInput(rawInputHandle);

    public ContextObservation CreateContextObservation(
        string taskTitle,
        string? currentSubtask = null,
        string? relevanceReason = null,
        double confidence = 0.7,
        DateTimeOffset? evidenceTimestamp = null,
        ScreenSnapshot? screen = null,
        int keyCount = 0,
        int scrollReversalCount = 0,
        double mouseDistance = 0)
    {
        var foreground = _foreground?.LastEvent;
        var process = GetFeature(foreground, "process");
        var title = GetFeature(foreground, "title");
        var secure = bool.TryParse(GetFeature(foreground, "secure_window"), out var isSecure) && isSecure;
        var browser = IsBrowserProcess(process) ? _browserContext?.Snapshot() : null;
        var browserIsFresh = browser is not null
            && (evidenceTimestamp ?? DateTimeOffset.UtcNow) - browser.UpdatedAt <= TimeSpan.FromSeconds(30);
        var screenIsFresh = screen is not null
            && !secure
            && (evidenceTimestamp ?? DateTimeOffset.UtcNow) - screen.Timestamp <= TimeSpan.FromSeconds(20)
            && string.Equals(screen.ProcessName, process, StringComparison.OrdinalIgnoreCase);
        var activity = ActivityClassifier.Infer(process, browser?.Title ?? title, keyCount, scrollReversalCount, mouseDistance);
        var focusLine = screenIsFresh ? screen!.FocusLine?.Text : null;
        var anchorVerb = screen?.FocusSource switch
        {
            FocusSource.Gaze => "eyes on",
            FocusSource.Caret => "cursor at",
            FocusSource.Pointer => "pointer near",
            _ => "on screen"
        };
        var lastAction = focusLine is not null
            ? $"{ActivityClassifier.Describe(activity)} · {anchorVerb}: \u201c{focusLine}\u201d"
            : browserIsFresh && !string.IsNullOrWhiteSpace(browser!.StuckPhrase)
                ? $"Paused near: {browser.StuckPhrase}"
                : $"Working toward: {taskTitle}";
        return new ContextObservation(
            Application: string.IsNullOrWhiteSpace(process) ? "Desktop" : process,
            DocumentIdentity: secure
                ? "Private window"
                : browserIsFresh
                    ? browser!.Title
                    : string.IsNullOrWhiteSpace(title) ? "Current window" : title,
            Location: browserIsFresh
                ? $"About {browser!.Progress:P0} through the page · paragraph {browser.ParagraphIndex + 1}"
                : "Current foreground window",
            LastAction: lastAction,
            NextAction: "Resume from the current window and take one small step.",
            SelectedText: null,
            RestoreTarget: browserIsFresh ? browser!.Origin : null,
            Confidence: Math.Clamp(confidence, 0, 1),
            IsSensitiveField: secure,
            CurrentSubtask: currentSubtask ?? string.Empty,
            RelevanceReason: relevanceReason ?? string.Empty,
            EvidenceTimestamp: evidenceTimestamp ?? DateTimeOffset.UtcNow,
            Activity: activity,
            FocusText: focusLine,
            FocusSource: screenIsFresh ? screen!.FocusSource : FocusSource.None,
            ScreenExcerpt: screenIsFresh ? screen!.Excerpt : null,
            KeyCount: keyCount,
            ScrollReversalCount: scrollReversalCount);
    }

    public TaskContext CreateTaskContext(
        string goal,
        string currentSubtask,
        IReadOnlyList<string>? userRelevantTargets = null)
    {
        _foreground?.Refresh();
        var foreground = _foreground?.LastEvent;
        var process = GetFeature(foreground, "process");
        var title = GetFeature(foreground, "title");
        var browser = IsBrowserProcess(process) ? _browserContext?.Snapshot() : null;
        return new TaskContext(
            goal,
            currentSubtask,
            string.IsNullOrWhiteSpace(process) ? "Desktop" : process,
            browser?.Title ?? (string.IsNullOrWhiteSpace(title) ? null : title),
            browser?.Origin,
            userRelevantTargets ?? []);
    }

    public bool IsGazeOnForegroundWindow(GazeSample? gaze)
    {
        if (gaze is not { Available: true } || gaze.X is null || gaze.Y is null)
        {
            return false;
        }
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rectangle))
        {
            return true;
        }
        var width = Math.Max(1, GetSystemMetrics(0));
        var height = Math.Max(1, GetSystemMetrics(1));
        var x = gaze.X.Value * width;
        var y = gaze.Y.Value * height;
        return x >= rectangle.Left
            && x <= rectangle.Right
            && y >= rectangle.Top
            && y <= rectangle.Bottom;
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    private static string GetFeature(DerivedEvent? item, string key) =>
        item is not null && item.Features.TryGetValue(key, out var value) ? value : string.Empty;

    private static int ParseInt(DerivedEvent item, string key) =>
        int.TryParse(GetFeature(item, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static double ParseDouble(DerivedEvent item, string key) =>
        double.TryParse(GetFeature(item, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static bool IsBrowserProcess(string process) =>
        process.Contains("chrome", StringComparison.OrdinalIgnoreCase)
        || process.Contains("msedge", StringComparison.OrdinalIgnoreCase)
        || process.Contains("firefox", StringComparison.OrdinalIgnoreCase);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
