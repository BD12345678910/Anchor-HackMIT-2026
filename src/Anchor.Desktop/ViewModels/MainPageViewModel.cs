using System.Collections.ObjectModel;
using Anchor.Core.Models;
using Anchor_Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;

namespace Anchor_Desktop.ViewModels;

public sealed record TimelineItem(string Time, string Label, string Detail);

public partial class MainPageViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _tickInFlight;

    public MainPageViewModel(AppServices services)
    {
        _services = services;
        _timer.Tick += Timer_Tick;
    }

    public ObservableCollection<TimelineItem> Timeline { get; } = [];

    [ObservableProperty] public partial string TaskTitle { get; set; } = "Finish the HackMIT project plan";
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string AttentionState { get; set; } = "Ready";
    [ObservableProperty] public partial string Confidence { get; set; } = "—";
    [ObservableProperty] public partial string ReasonSummary { get; set; } = "Start a task to begin private, on-device sensing.";
    [ObservableProperty] public partial string FocusedDuration { get; set; } = "0m 00s";
    [ObservableProperty] public partial string RecoveryDuration { get; set; } = "0m 00s";
    [ObservableProperty] public partial int InterruptionCount { get; set; }
    [ObservableProperty] public partial string CapabilityStatus { get; set; } = "Not started";
    [ObservableProperty] public partial string StatusMessage { get; set; } = "Anchor stays quiet until you ask for a session.";
    [ObservableProperty] public partial bool VisualFilterEnabled { get; set; } = true;
    [ObservableProperty] public partial bool BlurImagesEnabled { get; set; } = true;
    [ObservableProperty] public partial bool HideFutureTextEnabled { get; set; } = true;
    [ObservableProperty] public partial bool AudioShieldEnabled { get; set; }
    [ObservableProperty] public partial bool PointerGuardEnabled { get; set; }

    partial void OnVisualFilterEnabledChanged(bool value) => _services.Overlays.EnableVisualFilter = value;
    partial void OnPointerGuardEnabledChanged(bool value) => _services.Overlays.EnablePointerGuard = value;

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning || IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(TaskTitle))
        {
            StatusMessage = "Give this session a short task title first.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Starting local sensors and attention model…";
        try
        {
            var session = await _services.Orchestrator.StartAsync(TaskTitle);
            _services.Overlays.TaskTitle = session.Title;
            _services.Overlays.EnableVisualFilter = VisualFilterEnabled;
            _services.Overlays.EnablePointerGuard = PointerGuardEnabled;
            _services.Sensors.AttachWindow(App.WindowHandle);
            _services.Orchestrator.ObserveContext(_services.Sensors.CreateContextObservation(session.Title));
            _services.Overlays.ShowBeacon();
            _services.Watchdog.Arm(DateTimeOffset.UtcNow);
            IsRunning = true;
            CapabilityStatus = _services.Orchestrator.CapabilityStatus;
            AttentionState = "Observing";
            ReasonSummary = "Calibrating from app, idle, pointer, keyboard, and scrolling patterns.";
            StatusMessage = "Session active. Anchor intervenes only when evidence is strong.";
            AddTimeline("Session started", $"{CapabilityStatus} mode · local only");
            _timer.Start();
        }
        catch (Exception error)
        {
            StatusMessage = $"Could not start: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!IsRunning || IsBusy)
        {
            return;
        }

        IsBusy = true;
        _timer.Stop();
        try
        {
            await _services.Orchestrator.StopAsync();
            _services.Watchdog.Signal(Anchor.Infrastructure.Windows.SafetyReleaseReason.Shutdown);
            AddTimeline("Session complete", $"{FocusedDuration} focused · {InterruptionCount} interruptions");
            IsRunning = false;
            AttentionState = "Ready";
            CapabilityStatus = "Stopped";
            Confidence = "—";
            StatusMessage = "Session saved locally. Start another whenever you are ready.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReportDistractedAsync()
    {
        if (!IsRunning || IsBusy)
        {
            StatusMessage = "Start a session before asking Anchor to restore context.";
            return;
        }

        IsBusy = true;
        try
        {
            _services.Orchestrator.ObserveContext(_services.Sensors.CreateContextObservation(TaskTitle));
            var prediction = await _services.Orchestrator.ReportDistractedAsync();
            ApplyPrediction(prediction);
            AddTimeline("Recovery requested", "Manual report bypassed automatic confidence thresholds");
            StatusMessage = "A context reminder is open.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void EmergencyRelease()
    {
        _services.Watchdog.Signal(Anchor.Infrastructure.Windows.SafetyReleaseReason.Escape);
        StatusMessage = "All overlays and restrictions released.";
        AddTimeline("Safety release", "Emergency control cleared interventions");
    }

    [RelayCommand]
    private async Task DeleteLocalHistoryAsync()
    {
        if (IsRunning)
        {
            StatusMessage = "Stop the current session before deleting local history.";
            return;
        }

        await _services.Store.InitializeAsync();
        await _services.Store.DeleteAllAsync();
        Timeline.Clear();
        FocusedDuration = "0m 00s";
        RecoveryDuration = "0m 00s";
        InterruptionCount = 0;
        StatusMessage = "All locally stored session events were deleted.";
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        if (IsRunning)
        {
            await _services.Orchestrator.StopAsync();
        }
    }

    private async void Timer_Tick(object? sender, object e)
    {
        if (_tickInFlight || !IsRunning)
        {
            return;
        }

        _tickInFlight = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            _services.Watchdog.Heartbeat(now);
            _services.Watchdog.CheckExpired(now);
            _services.Orchestrator.ObserveContext(_services.Sensors.CreateContextObservation(TaskTitle));
            var window = _services.Sensors.Sample(TaskTitle, _services.Inference.IsAvailable, now);
            var prediction = await _services.Orchestrator.ProcessAsync(window);
            ApplyPrediction(prediction);
        }
        catch (Exception error)
        {
            StatusMessage = $"Sensing recovered from an error: {error.Message}";
        }
        finally
        {
            _tickInFlight = false;
        }
    }

    private void ApplyPrediction(AttentionPrediction prediction)
    {
        AttentionState = prediction.State.ToString();
        Confidence = $"{prediction.Confidence:P0}";
        ReasonSummary = prediction.ReasonCodes.Count == 0
            ? "No distraction signals"
            : string.Join(" · ", prediction.ReasonCodes.Select(static item => item.Replace('_', ' ')));
        CapabilityStatus = _services.Orchestrator.CapabilityStatus;

        if (_services.Orchestrator.Progress is { } progress)
        {
            FocusedDuration = FormatDuration(progress.FocusedSeconds);
            RecoveryDuration = FormatDuration(progress.RecoverySeconds);
            InterruptionCount = progress.InterruptionCount;
        }

        if (prediction.State is Anchor.Core.Models.AttentionState.Distracted
            or Anchor.Core.Models.AttentionState.Recovering
            or Anchor.Core.Models.AttentionState.Stuck)
        {
            AddTimeline(prediction.State.ToString(), ReasonSummary);
        }
    }

    private void AddTimeline(string label, string detail)
    {
        Timeline.Insert(0, new TimelineItem(DateTime.Now.ToString("HH:mm:ss"), label, detail));
        while (Timeline.Count > 30)
        {
            Timeline.RemoveAt(Timeline.Count - 1);
        }
    }

    private static string FormatDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)duration.TotalMinutes}m {duration.Seconds:00}s";
    }
}
