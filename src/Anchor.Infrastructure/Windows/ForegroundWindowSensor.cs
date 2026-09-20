using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Infrastructure.Windows;

public sealed class ForegroundWindowSensor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;

    private readonly Guid _sessionId;
    private readonly Queue<DateTimeOffset> _switches = new();
    private string? _lastProcess;
    private WinEventDelegate? _callback;
    private IntPtr _hook;
    private ChannelWriter<DerivedEvent>? _writer;

    public ForegroundWindowSensor(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        }

        _sessionId = sessionId;
    }

    public DerivedEvent? LastEvent { get; private set; }

    public DerivedEvent Observe(string processName, string windowTitle, DateTimeOffset timestamp)
    {
        processName = string.IsNullOrWhiteSpace(processName) ? "unknown" : processName.Trim();
        if (_lastProcess is not null && !string.Equals(_lastProcess, processName, StringComparison.OrdinalIgnoreCase))
        {
            _switches.Enqueue(timestamp);
        }

        _lastProcess = processName;
        while (_switches.TryPeek(out var item) && timestamp - item > TimeSpan.FromSeconds(30))
        {
            _switches.Dequeue();
        }

        LastEvent = DerivedEvent.Create(
            _sessionId,
            timestamp,
            "window",
            "foreground_changed",
            new Dictionary<string, string>
            {
                ["process"] = processName,
                ["title"] = SensitiveTextRedactor.Redact(windowTitle) ?? string.Empty,
                ["app_switch_count"] = _switches.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["secure_window"] = SecureWindowClassifier.IsSecure(processName, windowTitle).ToString()
            });
        return LastEvent;
    }

    public void Start(ChannelWriter<DerivedEvent> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _writer = writer;
        _callback = OnForegroundChanged;
        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WineventOutOfContext);
        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Foreground hook failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        // The hook only reports changes, so seed with whatever is already in front.
        ObserveWindow(GetForegroundWindow());
    }

    /// <summary>
    /// Re-reads the foreground window so in-place title changes (tab switches, page
    /// navigation) are seen even though they raise no foreground event.
    /// </summary>
    public void Refresh()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || _writer is null)
        {
            return;
        }

        var title = new StringBuilder(512);
        GetWindowText(window, title, title.Capacity);
        var redacted = SensitiveTextRedactor.Redact(title.ToString()) ?? string.Empty;
        if (LastEvent is { } last
            && last.Features.TryGetValue("title", out var previous)
            && string.Equals(previous, redacted, StringComparison.Ordinal))
        {
            return;
        }

        ObserveWindow(window);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        _callback = null;
        _writer = null;
    }

    private void OnForegroundChanged(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        _ = hook;
        _ = eventType;
        _ = objectId;
        _ = childId;
        _ = eventThread;
        _ = eventTime;
        ObserveWindow(window);
    }

    private void ObserveWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || _writer is null)
        {
            return;
        }

        GetWindowThreadProcessId(window, out var processId);
        string processName;
        try
        {
            processName = Process.GetProcessById(checked((int)processId)).ProcessName;
        }
        catch (ArgumentException)
        {
            processName = "unknown";
        }

        var title = new StringBuilder(512);
        GetWindowText(window, title, title.Capacity);
        _writer.TryWrite(Observe(processName, title.ToString(), DateTimeOffset.UtcNow));
    }

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);
}
