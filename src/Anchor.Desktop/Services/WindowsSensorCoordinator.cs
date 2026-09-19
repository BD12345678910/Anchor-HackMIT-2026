using System.Globalization;
using System.Threading.Channels;
using Anchor.Core.Models;
using Anchor.Core.Services;
using Anchor.Infrastructure.Windows;

namespace Anchor_Desktop.Services;

public sealed class WindowsSensorCoordinator : ISensorCoordinator, IDisposable
{
    private Channel<DerivedEvent>? _events;
    private ForegroundWindowSensor? _foreground;
    private InputActivitySensor? _input;
    private Guid _sessionId;

    public ChannelReader<DerivedEvent>? Events => _events?.Reader;
    public InputActivitySensor? Input => _input;

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

    public void ProcessRawInput(IntPtr rawInputHandle) => _input?.ProcessRawInput(rawInputHandle);

    public ContextObservation CreateContextObservation(string taskTitle)
    {
        var foreground = _foreground?.LastEvent;
        var process = GetFeature(foreground, "process");
        var title = GetFeature(foreground, "title");
        var secure = bool.TryParse(GetFeature(foreground, "secure_window"), out var isSecure) && isSecure;
        return new ContextObservation(
            Application: string.IsNullOrWhiteSpace(process) ? "Desktop" : process,
            DocumentIdentity: secure || string.IsNullOrWhiteSpace(title) ? "Private window" : title,
            Location: "Current foreground window",
            LastAction: $"Working toward: {taskTitle}",
            NextAction: "Resume from the current window and take one small step.",
            SelectedText: null,
            RestoreTarget: null,
            Confidence: 0.7,
            IsSensitiveField: secure);
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
}
