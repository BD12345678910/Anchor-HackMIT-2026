using System.Collections.ObjectModel;
using Anchor.Core.Models;
using Anchor.Core.Services;
using Anchor_Desktop.Services;
using Anchor.Infrastructure.DeepSeek;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;

namespace Anchor_Desktop.ViewModels;

public sealed record TimelineItem(string Time, string Label, string Detail);
public sealed record TaskStepItem(string Number, string Title, string CompletionCriterion, string Status);

public partial class MainPageViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _tickInFlight;
    private TaskSessionPlanner? _taskPlanner;
    private string? _plannedGoal;

    public MainPageViewModel(AppServices services)
    {
        _services = services;
        _timer.Tick += Timer_Tick;
    }

    public ObservableCollection<TimelineItem> Timeline { get; } = [];
    public ObservableCollection<TaskStepItem> TaskSteps { get; } = [];

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
    [ObservableProperty] public partial bool IsPlanReady { get; set; }
    [ObservableProperty] public partial string CurrentSubtask { get; set; } = "Plan the goal to choose a first step.";
    [ObservableProperty] public partial string TaskProgressLabel { get; set; } = "0 of 0";
    [ObservableProperty] public partial string PlanSource { get; set; } = "Not planned";
    [ObservableProperty] public partial bool DeepSeekEnabled { get; set; }
    [ObservableProperty] public partial string DeepSeekApiKey { get; set; } = string.Empty;
    [ObservableProperty] public partial string DeepSeekStatus { get; set; } = "Not configured";
    [ObservableProperty] public partial bool ReducedMotion { get; set; }

    partial void OnVisualFilterEnabledChanged(bool value) => _services.Overlays.EnableVisualFilter = value;
    partial void OnPointerGuardEnabledChanged(bool value) => _services.Overlays.EnablePointerGuard = value;
    partial void OnReducedMotionChanged(bool value) => _services.Overlays.ReducedMotion = value;

    partial void OnTaskTitleChanged(string value)
    {
        if (!IsRunning && _plannedGoal is not null
            && !string.Equals(_plannedGoal, value.Trim(), StringComparison.Ordinal))
        {
            IsPlanReady = false;
            PlanSource = "Goal changed — plan again";
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            var settings = await _services.DeepSeekSettings.LoadAsync();
            DeepSeekEnabled = settings?.Enabled == true;
            DeepSeekStatus = settings?.Enabled == true
                ? $"Configured · {settings.Model}"
                : string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY"))
                    ? "Not configured · local fallback remains available"
                    : "Configured from DEEPSEEK_API_KEY";
        }
        catch (Exception error)
        {
            DeepSeekStatus = $"Settings unavailable: {error.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveDeepSeekSettingsAsync()
    {
        if (DeepSeekEnabled && string.IsNullOrWhiteSpace(DeepSeekApiKey))
        {
            DeepSeekStatus = "Enter a DeepSeek API key before enabling cloud intelligence.";
            return;
        }

        IsBusy = true;
        try
        {
            await _services.DeepSeekSettings.SaveAsync(new DeepSeekSettings(
                DeepSeekEnabled,
                DeepSeekApiKey,
                "deepseek-flash",
                DeepSeekClient.DefaultEndpoint.ToString()));
            DeepSeekApiKey = string.Empty;
            DeepSeekStatus = DeepSeekEnabled
                ? "Configured · deepseek-flash · key encrypted for this Windows account"
                : "Disabled · local fallback only";
        }
        catch (Exception error)
        {
            DeepSeekStatus = $"Could not save: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PlanGoalAsync()
    {
        if (IsRunning || IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(TaskTitle))
        {
            StatusMessage = "Enter a concrete goal before planning it.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Breaking the goal into observable steps…";
        try
        {
            _taskPlanner = await _services.CreateTaskPlannerAsync();
            var state = await _taskPlanner.PlanAsync(TaskTitle);
            _plannedGoal = TaskTitle.Trim();
            ApplyTaskPlanState(state);
            IsPlanReady = true;
            StatusMessage = state.IsFallback
                ? "DeepSeek is unavailable. Review the labeled local fallback, then start if it is acceptable."
                : "Plan ready. Review the steps, then start the focus session.";
        }
        catch (Exception error)
        {
            IsPlanReady = false;
            StatusMessage = $"Could not plan this goal: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

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

        if (!IsPlanReady || _taskPlanner?.Current is null
            || !string.Equals(_plannedGoal, TaskTitle.Trim(), StringComparison.Ordinal))
        {
            StatusMessage = "Plan this exact goal and review its steps before starting.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Starting local sensors and attention model…";
        try
        {
            var session = await _services.Orchestrator.StartAsync(TaskTitle);
            var taskState = _taskPlanner.Current;
            _services.Overlays.UpdateGoal(
                session.Title,
                taskState.Progress.CurrentStep?.Title,
                taskState.ProgressLabel);
            _services.Overlays.EnableVisualFilter = VisualFilterEnabled;
            _services.Overlays.EnablePointerGuard = PointerGuardEnabled;
            _services.Overlays.ReducedMotion = ReducedMotion;
            _services.Sensors.AttachWindow(App.WindowHandle);
            _services.Orchestrator.ObserveContext(_services.Sensors.CreateContextObservation(session.Title));
            _services.Overlays.ShowBeacon();
            _services.Watchdog.Arm(DateTimeOffset.UtcNow);
            IsRunning = true;
            CapabilityStatus = _services.Orchestrator.CapabilityStatus;
            AttentionState = "Observing";
            ReasonSummary = "Calibrating from app, idle, pointer, keyboard, and scrolling patterns.";
            StatusMessage = "Session active. Anchor intervenes only when evidence is strong.";
            AddTimeline("Session started", $"{CapabilityStatus} mode · {PlanSource}");
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
    private void MarkCurrentStepComplete()
    {
        if (!IsRunning || _taskPlanner?.Current?.Progress.CurrentStep is null)
        {
            StatusMessage = "Start a planned session before completing a step.";
            return;
        }

        var completed = _taskPlanner.Current.Progress.CurrentStep.Title;
        var state = _taskPlanner.MarkCurrentComplete(CompletionSource.User, DateTimeOffset.UtcNow);
        ApplyTaskPlanState(state);
        _services.Overlays.UpdateGoal(TaskTitle, state.Progress.CurrentStep?.Title, state.ProgressLabel);
        _services.Overlays.ShowBeacon();
        AddTimeline("Step complete", completed);
        StatusMessage = state.Progress.CurrentStep is null
            ? "All planned steps are complete."
            : $"Next step: {state.Progress.CurrentStep.Title}";
    }

    [RelayCommand]
    private void PreviewBeacon()
    {
        _services.Overlays.UpdateGoal(TaskTitle, CurrentSubtask, TaskProgressLabel);
        _services.Overlays.ShowBeacon();
        StatusMessage = "Goal Beacon preview is visible on the active monitor.";
    }

    [RelayCommand]
    private void TestBeaconShake()
    {
        _services.Overlays.UpdateGoal(TaskTitle, CurrentSubtask, TaskProgressLabel);
        _services.Overlays.ShowBeacon(pulse: true);
        StatusMessage = ReducedMotion
            ? "Reduced-motion static beacon emphasis shown."
            : "One-shot beacon shake shown.";
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

    private void ApplyTaskPlanState(TaskSessionPlanState state)
    {
        CurrentSubtask = state.Progress.CurrentStep?.Title ?? "Task complete";
        TaskProgressLabel = state.ProgressLabel;
        PlanSource = state.ErrorCode is null
            ? state.Source
            : $"{state.Source} · {state.ErrorCode.Replace('_', ' ')}";
        TaskSteps.Clear();
        for (var index = 0; index < state.Progress.Plan.Steps.Count; index++)
        {
            var step = state.Progress.Plan.Steps[index];
            var status = index < state.Progress.CompletedCount
                ? "Complete"
                : index == state.Progress.CompletedCount
                    ? "Current"
                    : "Upcoming";
            TaskSteps.Add(new TaskStepItem(
                (index + 1).ToString(),
                step.Title,
                step.CompletionCriterion,
                status));
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
