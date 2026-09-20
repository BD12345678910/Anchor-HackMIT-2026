using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Anchor.Core.Models;
using Anchor.Core.Services;
using Anchor_Desktop.Overlays;
using Anchor_Desktop.Services;
using Anchor.Infrastructure.Browser;
using Anchor.Infrastructure.DeepSeek;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.Storage.Pickers;
using ActivityKind = Anchor.Core.Models.ActivityKind;

namespace Anchor_Desktop.ViewModels;

public sealed record TimelineItem(string Time, string Label, string Detail);

public sealed partial class AttentionAnalyticsViewModel : ObservableObject
{
    [ObservableProperty] public partial string AttentionSpan { get; set; } = "—";
    [ObservableProperty] public partial string DistractionRate { get; set; } = "—";
    [ObservableProperty] public partial string OnTaskShare { get; set; } = "—";
    [ObservableProperty] public partial string Triggers { get; set; } = "What pulls you away: no distraction recorded yet.";
    [ObservableProperty] public partial string Distractors { get; set; } = NoDistractors;
    [ObservableProperty] public partial string Interventions { get; set; } = "Interventions used: none yet.";

    private const string NoDistractors = "What distracts you most: nothing recorded yet.";
    [ObservableProperty] public partial string Trend { get; set; } = "Trend: not enough data yet.";

    public void Apply(AttentionAnalysis analysis)
    {
        if (analysis.Samples < 2)
        {
            Reset();
            return;
        }

        AttentionSpan = $"{Short(analysis.MedianAttentionSpan)} / {Short(analysis.LongestAttentionSpan)}"
            + (analysis.CurrentAttentionSpan > TimeSpan.Zero ? $" · now {Short(analysis.CurrentAttentionSpan)}" : string.Empty);
        DistractionRate = $"{analysis.DistractionsPerHour:0.#}/h · {Short(analysis.MedianRecovery)}";
        OnTaskShare = $"{analysis.OnTaskShare:P0}";
        Triggers = analysis.Triggers.Count == 0
            ? "What pulls you away: no distraction recorded yet."
            : "What pulls you away: " + string.Join(", ", analysis.Triggers.Take(4).Select(static pair => $"{pair.Key.Replace('_', ' ')} ×{pair.Value}"));
        Distractors = analysis.Distractors.Count == 0
            ? NoDistractors
            : $"What distracts you most ({Short(analysis.TimeLost)} lost): "
              + string.Join(", ", analysis.Distractors.Take(4).Select((item, index) =>
                  $"{index + 1}. {item.Name} — {Short(item.TimeLost)} over {item.Episodes} visit{(item.Episodes == 1 ? string.Empty : "s")}"));
        Interventions = analysis.Interventions.Count == 0
            ? "Interventions used: none yet."
            : "Interventions used: " + string.Join(", ", analysis.Interventions.Select(static pair => $"{Describe(pair.Key)} ×{pair.Value}"));
        Trend = "Trend: " + analysis.Trend;
    }

    public void Reset()
    {
        AttentionSpan = "—";
        DistractionRate = "—";
        OnTaskShare = "—";
        Triggers = "What pulls you away: no distraction recorded yet.";
        Distractors = NoDistractors;
        Interventions = "Interventions used: none yet.";
        Trend = "Trend: not enough data yet.";
    }

    private static string Short(TimeSpan value) =>
        value.TotalMinutes >= 1 ? $"{(int)value.TotalMinutes}m {value.Seconds:00}s" : $"{value.Seconds}s";

    private static string Describe(InterventionKind kind) => kind switch
    {
        InterventionKind.BeaconPulse => "beacon pulse",
        InterventionKind.VisualFilter => "visual filter",
        InterventionKind.IntentionGate => "intention gate",
        InterventionKind.RecoveryCard => "recovery card",
        InterventionKind.BreakSuggestion => "break suggestion",
        _ => kind.ToString()
    };
}
public sealed record TaskStepItem(string Number, string Title, string CompletionCriterion, string Status);

public partial class MainPageViewModel : ObservableObject, IAsyncDisposable
{
    private int _disposeStarted;
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _gazeTimer = new() { Interval = TimeSpan.FromMilliseconds(125) };
    private readonly DispatcherTimer _recordingTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _webcamRecordingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _tickInFlight;
    private bool _gazeTickInFlight;
    private bool _recordingTickInFlight;
    private AttentionFusion _attentionFusion = new();
    private bool _sessionGazeActive;
    private bool _lastSecureWindow;
    private TaskSessionPlanner? _taskPlanner;
    private string? _plannedGoal;
    private string? _lastEvidenceTitle;
    private bool _loadingPreferences;
    /// <summary>Below this many keys in a window there is not enough text to call it mashing.</summary>
    private const int TypedTextKeyCount = 8;

    private string? _lastTypedLine;
    private string? _lastKeyboardScreenHash;
    private string? _lastPageEdit;
    private DateTimeOffset _lastPageContextRead = DateTimeOffset.MinValue;
    private ScreenSnapshot? _lastScreen;
    private DateTimeOffset _lastScreenRead = DateTimeOffset.MinValue;
    private string? _lastScreenIdentity;
    private string? _lastJudgedScreenHash;
    private bool _screenJudgeInFlight;
    private readonly List<string> _screenEvidenceUsed = [];
    private readonly List<string> _screenTrail = [];
    private string? _trailStepId;
    private string? _trailLastHash;
    private DateTimeOffset _stepActiveSince = DateTimeOffset.MinValue;
    private string? _lastRelevanceTrace;
    private static readonly TimeSpan ScreenReadInterval = TimeSpan.FromSeconds(4);
    private const int MaxTrailEntries = 12;
    private readonly HashSet<string> _dismissedSuggestions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _userRelevantTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly AttentionAnalyzer _analyzer = new();

    public MainPageViewModel(AppServices services)
    {
        _services = services;
        _services.Orchestrator.InterventionPresented += Orchestrator_InterventionPresented;
        _timer.Tick += Timer_Tick;
        _gazeTimer.Tick += GazeTimer_Tick;
        _recordingTimer.Tick += RecordingTimer_Tick;
        _webcamRecordingTimer.Tick += WebcamRecordingTimer_Tick;
        _services.Overlays.CurrentWindowMarkedRelevant += Overlays_CurrentWindowMarkedRelevant;
        _services.Overlays.StepMarkedDoneFromBeacon += Overlays_StepMarkedDoneFromBeacon;
        _services.Overlays.OverlaysCleared += Overlays_Cleared;
        _services.Overlays.ImageBlur.StatusChanged += ImageBlur_StatusChanged;
        _services.Overlays.BreakdownProvider = BreakDownRecoveryStepAsync;
        _services.Overlays.ReminderProvider = ComposeReminderAsync;
        ImageBlurStatus = _services.Overlays.ImageBlur.Status;
    }

    private void ImageBlur_StatusChanged(object? sender, string status) =>
        App.DispatcherQueue.TryEnqueue(() => ImageBlurStatus = $"Image blur: {status}");

    private void Orchestrator_InterventionPresented(InterventionDecision decision, ContextCapsule? capsule)
    {
        _analyzer.RecordIntervention(decision);
        App.DispatcherQueue.TryEnqueue(() => Analytics.Apply(_analyzer.Analyze(DateTimeOffset.UtcNow)));
    }

    public AttentionAnalyticsViewModel Analytics { get; } = new();
    public ObservableCollection<TimelineItem> Timeline { get; } = [];
    public ObservableCollection<TaskStepItem> TaskSteps { get; } = [];
    public ObservableCollection<CameraDevice> CameraDevices { get; } = [];

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
    [ObservableProperty] public partial bool SuppressAnimationsEnabled { get; set; }
    [ObservableProperty] public partial bool RemovePageClutterEnabled { get; set; }
    [ObservableProperty] public partial bool SimplifyPageTextEnabled { get; set; }
    [ObservableProperty] public partial bool AudioShieldEnabled { get; set; }
    [ObservableProperty] public partial bool PointerGuardEnabled { get; set; }
    [ObservableProperty] public partial bool IsPlanReady { get; set; }
    [ObservableProperty] public partial string CurrentSubtask { get; set; } = "Plan the goal to choose a first step.";
    [ObservableProperty] public partial string TaskProgressLabel { get; set; } = "0 of 0";
    [ObservableProperty] public partial string PlanSource { get; set; } = "Not planned";
    [ObservableProperty] public partial string ProgressNote { get; set; } = string.Empty;
    [ObservableProperty] public partial string? PendingSuggestion { get; set; }
    [ObservableProperty] public partial bool HasPendingSuggestion { get; set; }
    [ObservableProperty] public partial bool IsPlanComplete { get; set; }
    [ObservableProperty] public partial bool DeepSeekEnabled { get; set; }
    [ObservableProperty] public partial string DeepSeekApiKey { get; set; } = string.Empty;
    [ObservableProperty] public partial string DeepSeekStatus { get; set; } = "Not configured";
    [ObservableProperty] public partial string ScreenReaderStatus { get; set; } = "Screen reading idle";
    [ObservableProperty] public partial string ScreenProgressStatus { get; set; } = "Progress is detected from the screen while a session runs.";
    [ObservableProperty] public partial string ScreenAnchor { get; set; } = "No anchor yet";
    [ObservableProperty] public partial bool ReducedMotion { get; set; }
    [ObservableProperty] public partial bool GazeSpotlightEnabled { get; set; }
    [ObservableProperty] public partial bool WindowFirewallEnabled { get; set; }
    [ObservableProperty] public partial string ToolkitStatus { get; set; } = "Desktop tools ready";
    [ObservableProperty] public partial string BrowserStatus { get; set; } = "Focused browser: not open";
    [ObservableProperty] public partial bool IsBrowserConnected { get; set; }
    [ObservableProperty] public partial string ImageBlurStatus { get; set; } = "Image blur: off";
    [ObservableProperty] public partial CameraDevice? SelectedCamera { get; set; }
    [ObservableProperty] public partial bool GazeMirror { get; set; } = true;
    [ObservableProperty] public partial double GazeRotationDegrees { get; set; }
    [ObservableProperty] public partial double GazeOffsetX { get; set; }
    [ObservableProperty] public partial double GazeOffsetY { get; set; }
    [ObservableProperty] public partial double GazeSmoothing { get; set; } = 0.65;
    [ObservableProperty] public partial double GazeSensitivity { get; set; } = 1.0;
    [ObservableProperty] public partial double GazeMinimumConfidence { get; set; } = 0.45;
    [ObservableProperty] public partial bool IsGazeRunning { get; set; }
    [ObservableProperty] public partial string GazeStatusMessage { get; set; } = "Camera off · no gaze coordinates are inferred.";
    [ObservableProperty] public partial string GazeCoordinates { get; set; } = "Unavailable";
    [ObservableProperty] public partial string GazeConfidenceLabel { get; set; } = "—";
    [ObservableProperty] public partial ImageSource? GazePreviewSource { get; set; }
    [ObservableProperty] public partial string CalibrationStatus { get; set; } = "Not calibrated · gaze is a rough eye-direction estimate until you calibrate.";
    [ObservableProperty] public partial bool IsWebcamRecording { get; set; }
    [ObservableProperty] public partial string WebcamRecordingStatusText { get; set; } = "Not recording";
    [ObservableProperty] public partial string WebcamRecordingFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Anchor Webcam");
    [ObservableProperty] public partial bool TreatCurrentWindowAsRelevant { get; set; }
    [ObservableProperty] public partial string ParticipantCode { get; set; } = "P01";
    [ObservableProperty] public partial int RecordingModeIndex { get; set; } = 1;
    [ObservableProperty] public partial string RecordingOutputFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Anchor Studies");
    [ObservableProperty] public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial string EvidenceRecordingFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Anchor Evidence");
    [ObservableProperty] public partial string EvidenceRecordingStatus { get; set; } =
        "Not recording · one MP4 holds the screen, your eyes and the attention verdict.";
    [ObservableProperty] public partial string RecordingStatus { get; set; } = "Ready · screen and gaze stay local";
    [ObservableProperty] public partial string RecordingElapsed { get; set; } = "00:00";
    [ObservableProperty] public partial string LatestRecordingFiles { get; set; } = "No recording created yet";
    [ObservableProperty] public partial string ComparisonReport { get; set; } = "Choose Compare recordings after creating one baseline and one Anchor-enabled trial.";

    partial void OnVisualFilterEnabledChanged(bool value) => ToolPreferenceChanged();
    partial void OnBlurImagesEnabledChanged(bool value) => ToolPreferenceChanged();
    partial void OnHideFutureTextEnabledChanged(bool value) => ToolPreferenceChanged();
    partial void OnSuppressAnimationsEnabledChanged(bool value) => ToolPreferenceChanged();
    partial void OnRemovePageClutterEnabledChanged(bool value) => ToolPreferenceChanged();
    partial void OnSimplifyPageTextEnabledChanged(bool value) => ToolPreferenceChanged();
    partial void OnPointerGuardEnabledChanged(bool value) => ToolPreferenceChanged();
    partial void OnGazeSpotlightEnabledChanged(bool value) => ToolPreferenceChanged();
    partial void OnWindowFirewallEnabledChanged(bool value) => ToolPreferenceChanged();
    partial void OnAudioShieldEnabledChanged(bool value) => _ = SavePreferencesAsync();

    partial void OnReducedMotionChanged(bool value)
    {
        _services.Overlays.ReducedMotion = value;
        _ = SavePreferencesAsync();
    }

    private void ToolPreferenceChanged()
    {
        _ = ApplyToolkitStateAsync();
        _ = SavePreferencesAsync();
    }

    private async Task SavePreferencesAsync()
    {
        if (_loadingPreferences)
        {
            return;
        }

        try
        {
            await _services.Preferences.SaveAsync(new ToolPreferences(
                VisualFilterEnabled,
                BlurImagesEnabled,
                HideFutureTextEnabled,
                SuppressAnimationsEnabled,
                AudioShieldEnabled,
                PointerGuardEnabled,
                GazeSpotlightEnabled,
                WindowFirewallEnabled,
                ReducedMotion,
                RemovePageClutterEnabled,
                SimplifyPageTextEnabled));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _services.LogError("save preferences", error);
        }
    }

    private async Task LoadPreferencesAsync()
    {
        ToolPreferences? saved;
        try
        {
            saved = await _services.Preferences.LoadAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _services.LogError("load preferences", error);
            return;
        }
        if (saved is null)
        {
            return;
        }

        _loadingPreferences = true;
        try
        {
            VisualFilterEnabled = saved.VisualFilter;
            BlurImagesEnabled = saved.BlurImages;
            HideFutureTextEnabled = saved.HideFutureText;
            SuppressAnimationsEnabled = saved.SuppressAnimations;
            AudioShieldEnabled = saved.AudioShield;
            PointerGuardEnabled = saved.PointerGuard;
            GazeSpotlightEnabled = saved.GazeSpotlight;
            WindowFirewallEnabled = saved.WindowFirewall;
            ReducedMotion = saved.ReducedMotion;
            RemovePageClutterEnabled = saved.RemovePageClutter;
            SimplifyPageTextEnabled = saved.SimplifyPageText;
        }
        finally
        {
            _loadingPreferences = false;
        }
    }

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
        await LoadPreferencesAsync();
        try
        {
            var settings = await _services.DeepSeekSettings.LoadAsync();
            var key = AppServices.ResolveDeepSeekKey(settings);
            DeepSeekEnabled = key.Length > 0;
            DeepSeekStatus = key.Length == 0
                ? "No API key yet · running on local rules (screen progress only suggests, never auto-completes). Paste your DeepSeek key below."
                : !string.IsNullOrWhiteSpace(settings?.ApiKey)
                    ? $"Active · {settings.Model} · plans, relevance, screen progress and context reminders"
                    : "Active · deepseek-flash · key from DEEPSEEK_API_KEY";
            ScreenReaderStatus = _services.ScreenReader.Status;
        }
        catch (Exception error)
        {
            DeepSeekStatus = $"Settings unavailable: {error.Message}";
        }
        await ApplyToolkitStateAsync();
    }

    [RelayCommand]
    private async Task SaveDeepSeekSettingsAsync()
    {
        IsBusy = true;
        try
        {
            var existing = await _services.DeepSeekSettings.LoadAsync();
            var savedKey = string.IsNullOrWhiteSpace(DeepSeekApiKey)
                ? existing?.ApiKey ?? string.Empty
                : DeepSeekApiKey.Trim();
            var environmentKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? string.Empty;
            if (savedKey.Length == 0 && string.IsNullOrWhiteSpace(environmentKey))
            {
                DeepSeekStatus = "Enter a DeepSeek API key (or set DEEPSEEK_API_KEY). Until then Anchor runs on local rules.";
                return;
            }

            await _services.DeepSeekSettings.SaveAsync(new DeepSeekSettings(
                Enabled: true,
                savedKey,
                "deepseek-flash",
                DeepSeekClient.DefaultEndpoint.ToString()));
            DeepSeekApiKey = string.Empty;
            DeepSeekEnabled = true;
            DeepSeekStatus = savedKey.Length > 0
                ? "Active · deepseek-flash · key encrypted for this Windows account"
                : "Active · deepseek-flash · key from DEEPSEEK_API_KEY";
            if (_taskPlanner is not null && !IsRunning)
            {
                _taskPlanner = null;
                IsPlanReady = false;
                PlanSource = "DeepSeek settings changed — plan again";
            }
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
            var planner = _taskPlanner;
            _services.Overlays.ImageBlur.Grader = (request, token) => planner.GradePicturesAsync(request, token);
            _services.Overlays.ImageBlur.TextGrader = (request, token) => planner.GradeTextBlocksAsync(request, token);
            _services.Overlays.ImageBlur.ResetVerdicts();
            var state = await _taskPlanner.PlanAsync(TaskTitle);
            _plannedGoal = TaskTitle.Trim();
            _lastEvidenceTitle = null;
            _lastJudgedScreenHash = null;
            _screenEvidenceUsed.Clear();
            _dismissedSuggestions.Clear();
            ApplyTaskPlanState(state);
            IsPlanReady = true;
            _services.Overlays.UpdateGoal(TaskTitle, state.Progress.CurrentStep?.Title, state.ProgressLabel);
            StatusMessage = state.IsFallback
                ? "Planned with local rules (no DeepSeek key or DeepSeek unreachable). Review the steps, then start the focus session."
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
            _attentionFusion = new AttentionFusion();
            _analyzer.Reset();
            Analytics.Reset();
            var gazeStatus = await _services.Inference.StartGazeAsync();
            _sessionGazeActive = gazeStatus.Running;
            GazeStatusMessage = gazeStatus.Running
                ? "Camera on · gaze anchors the line you are reading and drives the spotlight."
                : $"Camera not open · gaze unavailable, reminders anchor to caret/pointer instead{(string.IsNullOrWhiteSpace(gazeStatus.Error) ? string.Empty : $" ({gazeStatus.Error})")}.";
            var taskState = _taskPlanner.Current;
            _services.Overlays.UpdateGoal(
                session.Title,
                taskState.Progress.CurrentStep?.Title,
                taskState.ProgressLabel);
            _services.Overlays.EnableVisualFilter = VisualFilterEnabled;
            _services.Overlays.EnablePointerGuard = PointerGuardEnabled;
            _services.Overlays.ReducedMotion = ReducedMotion;
            _services.Sensors.AttachWindow(App.WindowHandle);
            _services.Orchestrator.UpdateTaskContext(
                taskState.Progress.CurrentStep?.Title,
                taskState.Progress.CurrentStep?.CompletionCriterion);
            _services.Orchestrator.ObserveContext(_services.Sensors.CreateContextObservation(
                session.Title,
                taskState.Progress.CurrentStep?.Title,
                "Session starting window",
                evidenceTimestamp: DateTimeOffset.UtcNow));
            IsRunning = true;
            await ApplyToolkitStateAsync();
            _services.Overlays.ShowBeacon();
            _services.Watchdog.Arm(DateTimeOffset.UtcNow);
            CapabilityStatus = _sessionGazeActive
                ? $"{_services.Orchestrator.CapabilityStatus} · gaze live"
                : $"{_services.Orchestrator.CapabilityStatus} · no camera";
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
    private void MarkCurrentStepComplete() => CompleteCurrentStep(CompletionSource.User, null);

    [RelayCommand]
    private void LogProgress()
    {
        var note = ProgressNote.Trim();
        if (note.Length == 0)
        {
            StatusMessage = "Type what you just did or opened, for example \"USACO 2021 Dec Bronze problem 1\".";
            return;
        }

        if (_taskPlanner?.Current?.Progress.CurrentStep is not { } current)
        {
            StatusMessage = "Plan the goal first so progress can be matched to a step.";
            return;
        }

        ProgressNote = string.Empty;
        var match = TaskEvidenceMatcher.Evaluate(current, TaskTitle, note);
        if (match.SuggestsCompletion)
        {
            CompleteCurrentStep(CompletionSource.User, note);
            return;
        }

        AddTimeline("Progress note", note);
        StatusMessage = $"Noted. \"{note}\" did not clearly match \"{current.Title}\" — use Mark step done if it is finished.";
    }

    [RelayCommand]
    private void ConfirmSuggestion()
    {
        if (_taskPlanner?.Current?.Progress.PendingSuggestion is not { } evidence)
        {
            ClearSuggestion();
            return;
        }

        CompleteCurrentStep(CompletionSource.LlmSuggestionConfirmed, evidence);
    }

    [RelayCommand]
    private void DismissSuggestion()
    {
        if (_taskPlanner?.Current?.Progress.PendingSuggestion is { } evidence)
        {
            _dismissedSuggestions.Add(evidence);
            ApplyTaskPlanState(_taskPlanner.DismissSuggestion());
        }
        ClearSuggestion();
        StatusMessage = "Suggestion dismissed. Anchor will not ask about that window again for this step.";
    }

    private void CompleteCurrentStep(CompletionSource source, string? evidence)
    {
        if (_taskPlanner?.Current?.Progress.CurrentStep is null)
        {
            StatusMessage = IsPlanReady
                ? "All planned steps are already complete."
                : "Plan the goal before completing a step.";
            return;
        }

        var completed = _taskPlanner.Current.Progress.CurrentStep.Title;
        var state = source == CompletionSource.LlmSuggestionConfirmed && _taskPlanner.Current.Progress.PendingSuggestion is not null
            ? _taskPlanner.ConfirmSuggestedCompletion(DateTimeOffset.UtcNow)
            : _taskPlanner.MarkCurrentComplete(source, DateTimeOffset.UtcNow);
        if (IsRunning)
        {
            _services.Recording.RecordSubtaskCompleted(completed);
            _services.Orchestrator.UpdateTaskContext(
                state.Progress.CurrentStep?.Title,
                state.Progress.CurrentStep?.CompletionCriterion);
        }
        ApplyTaskPlanState(state);
        _services.Overlays.UpdateGoal(TaskTitle, state.Progress.CurrentStep?.Title, state.ProgressLabel);
        if (IsRunning)
        {
            _services.Overlays.ShowBeacon(pulse: true);
        }
        _dismissedSuggestions.Clear();
        AddTimeline("Step complete", evidence is null ? completed : $"{completed} · {evidence}");
        StatusMessage = state.Progress.CurrentStep is null
            ? "All planned steps are complete. Nice work."
            : $"Step done: {completed}. Next: {state.Progress.CurrentStep.Title}";
    }

    private void ClearSuggestion()
    {
        PendingSuggestion = null;
        HasPendingSuggestion = false;
    }

    private void ConsiderEvidence(string? windowTitle, string? processName)
    {
        if (_taskPlanner?.Current?.Progress.CurrentStep is not { } current
            || string.IsNullOrWhiteSpace(windowTitle)
            || string.Equals(windowTitle, _lastEvidenceTitle, StringComparison.Ordinal)
            || string.Equals(processName, "Anchor", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastEvidenceTitle = windowTitle;
        if (_taskPlanner.Current.Progress.PendingSuggestion is not null || _dismissedSuggestions.Contains(windowTitle))
        {
            return;
        }

        var match = TaskEvidenceMatcher.Evaluate(current, TaskTitle, windowTitle);
        if (!match.SuggestsCompletion)
        {
            return;
        }

        var state = _taskPlanner.SuggestCompletion(windowTitle);
        PendingSuggestion = $"\"{windowTitle}\" looks like progress on \"{current.Title}\". Mark it done?";
        HasPendingSuggestion = true;
        AddTimeline("Progress spotted", $"{windowTitle} · matched {string.Join(", ", match.MatchedTokens)}");
        ApplyTaskPlanState(state);
    }

    /// <summary>
    /// Reads the front window's text with on-device OCR every few seconds, and immediately when a
    /// new window comes to the front, so relevance can be judged from the page itself. Secure
    /// windows are never read.
    /// </summary>
    private async Task<ScreenSnapshot?> ReadScreenAsync(GazeSample? gaze, SensorWindow raw, DateTimeOffset now, TaskContext context)
    {
        if (!_services.ScreenReader.IsAvailable || raw.IsSecureWindow)
        {
            return null;
        }
        var identity = ContextIdentity(context);
        var newWindow = !string.Equals(identity, _lastScreenIdentity, StringComparison.Ordinal);
        if (!newWindow && now - _lastScreenRead < ScreenReadInterval)
        {
            return null;
        }
        _lastScreenRead = now;
        _lastScreenIdentity = identity;
        var snapshot = await _services.ScreenReader.ReadForegroundAsync(gaze, raw.IsSecureWindow);
        ScreenReaderStatus = _services.ScreenReader.Status;
        return snapshot;
    }

    /// <summary>Keeps a snapshot of task-relevant content for the capsule, trail and progress detection.</summary>
    private void AdoptScreen(ScreenSnapshot snapshot, DateTimeOffset now)
    {
        _lastScreen = snapshot;
        RecordTrail(snapshot, now);
        var anchorSource = snapshot.FocusSource switch
        {
            FocusSource.Gaze => "gaze (camera on)",
            FocusSource.Caret => "camera not open · caret",
            FocusSource.Pointer => "camera not open · pointer",
            FocusSource.Viewport => "camera not open · viewport",
            _ => "camera not open · no anchor"
        };
        ScreenAnchor = snapshot.FocusLine is null
            ? $"{snapshot.ProcessName} · {anchorSource} · no text recognised"
            : $"{anchorSource} · \u201c{Truncate(snapshot.FocusLine.Text, 90)}\u201d";
        _services.Trace($"screen process={snapshot.ProcessName} lines={snapshot.Lines.Count} anchor={anchorSource} focus=\"{snapshot.FocusLine?.Text}\"");
    }

    /// <summary>
    /// The text that just appeared where the person is typing, or null when there is nothing new
    /// to judge. Only composing activities are considered: elsewhere the keyboard is shortcuts and
    /// search boxes, where "not a word" means nothing.
    /// </summary>
    /// <summary>
    /// Whether this window's fresh reading of the screen shows different text from the last one,
    /// so typed characters can be seen to have landed. Null when nothing was read this tick.
    /// </summary>
    private bool? ResolveScreenTextChanged(ScreenSnapshot? screen)
    {
        if (screen is null)
        {
            return null;
        }

        var changed = !string.Equals(screen.ContentHash, _lastKeyboardScreenHash, StringComparison.Ordinal);
        _lastKeyboardScreenHash = screen.ContentHash;
        return changed;
    }

    private string? ResolveTypedText(ActivityKind activity, SensorWindow raw, ScreenSnapshot? screen)
    {
        if (activity is not (ActivityKind.Coding or ActivityKind.Writing)
            || raw.IsSecureWindow
            || raw.KeyCount < TypedTextKeyCount
            || screen?.FocusLine is null)
        {
            return null;
        }

        var line = screen.FocusLine.Text;
        if (string.IsNullOrWhiteSpace(line) || string.Equals(line, _lastTypedLine, StringComparison.Ordinal))
        {
            // Unchanged text is text the person is looking at, not text they just produced.
            return null;
        }

        _lastTypedLine = line;
        return line;
    }

    private void RecordTrail(ScreenSnapshot snapshot, DateTimeOffset now)
    {
        var stepId = _taskPlanner?.Current?.Progress.CurrentStep?.Id;
        if (!string.Equals(stepId, _trailStepId, StringComparison.Ordinal))
        {
            _trailStepId = stepId;
            _trailLastHash = null;
            _screenTrail.Clear();
            _stepActiveSince = now;
        }
        if (string.Equals(snapshot.ContentHash, _trailLastHash, StringComparison.Ordinal))
        {
            return;
        }
        _trailLastHash = snapshot.ContentHash;
        var entry = ScreenSnapshotAnalyzer.TrailEntry(snapshot.Lines);
        if (entry.Length == 0 || (_screenTrail.Count > 0 && string.Equals(_screenTrail[^1], entry, StringComparison.Ordinal)))
        {
            return;
        }
        _screenTrail.Add(entry);
        if (_screenTrail.Count > MaxTrailEntries)
        {
            _screenTrail.RemoveAt(0);
        }
    }

    /// <summary>
    /// Asks the task intelligence (DeepSeek when configured, local rules otherwise) whether what is
    /// on screen completes the active step. High-confidence DeepSeek verdicts complete the step
    /// automatically; weaker evidence becomes a one-tap suggestion.
    /// </summary>
    private async Task JudgeScreenProgressAsync(SensorWindow raw)
    {
        if (_screenJudgeInFlight
            || _taskPlanner?.Current?.Progress is not { CurrentStep: { } current } progress
            || progress.PendingSuggestion is not null
            || _lastScreen is not { } screen
            || screen.Lines.Count == 0
            || string.Equals(screen.ContentHash, _lastJudgedScreenHash, StringComparison.Ordinal))
        {
            return;
        }

        _screenJudgeInFlight = true;
        _lastJudgedScreenHash = screen.ContentHash;
        try
        {
            var evidence = new ProgressEvidence(
                TaskTitle,
                progress.Plan.Steps,
                progress.CompletedCount,
                current,
                screen.ProcessName,
                screen.WindowTitle,
                ScreenSnapshotAnalyzer.FlattenText(screen.Lines),
                ActivityClassifier.Infer(screen.ProcessName, screen.WindowTitle, raw.KeyCount, raw.ScrollReversalCount, raw.MouseDistance),
                _screenEvidenceUsed.TakeLast(6).ToArray(),
                _screenTrail.ToArray(),
                _stepActiveSince == DateTimeOffset.MinValue ? TimeSpan.Zero : DateTimeOffset.UtcNow - _stepActiveSince);
            var judgment = await _taskPlanner.JudgeProgressAsync(evidence);
            _services.Trace($"progress source={judgment.Source} completed={judgment.StepCompleted} confidence={judgment.Confidence:0.00} evidence=\"{judgment.Evidence}\"");

            if (_taskPlanner?.Current?.Progress.CurrentStep?.Id != current.Id)
            {
                return;
            }

            if (judgment.StepCompleted && !judgment.IsFallback && judgment.Confidence >= ProgressJudgment.AutoCompleteThreshold)
            {
                ScreenProgressStatus = $"{judgment.Source} saw the step finish ({judgment.Confidence:P0}): {judgment.Evidence}";
                _screenEvidenceUsed.Add(judgment.Evidence);
                AddTimeline("Progress detected on screen", $"{judgment.Source} · {judgment.Evidence}");
                CompleteCurrentStep(CompletionSource.Adapter, judgment.Evidence);
                return;
            }

            if (judgment.StepCompleted && judgment.Confidence >= ProgressJudgment.SuggestThreshold
                && !_dismissedSuggestions.Contains(screen.WindowTitle))
            {
                var state = _taskPlanner.SuggestCompletion(judgment.Evidence);
                PendingSuggestion = $"{judgment.Source}: {judgment.Evidence} Mark \"{current.Title}\" done?";
                HasPendingSuggestion = true;
                ScreenProgressStatus = $"{judgment.Source} thinks the step may be done ({judgment.Confidence:P0}).";
                AddTimeline("Progress spotted on screen", $"{judgment.Source} · {judgment.Evidence}");
                ApplyTaskPlanState(state);
                return;
            }

            ScreenProgressStatus = judgment.Confidence > 0
                ? $"{judgment.Source} · {judgment.Evidence}"
                : $"{judgment.Source} · watching {screen.ProcessName} for \"{current.Title}\"";
        }
        catch (Exception error)
        {
            _services.LogError("screen progress", error);
            ScreenProgressStatus = $"Screen progress check failed: {error.Message}";
        }
        finally
        {
            _screenJudgeInFlight = false;
        }
    }

    private Task<ContextReminder> ComposeReminderAsync(ContextCapsule capsule, CancellationToken cancellationToken) =>
        _taskPlanner is null
            ? Task.FromResult(LocalContextReminder.Compose(capsule))
            : _taskPlanner.ComposeReminderAsync(capsule, cancellationToken);

    private void TraceRelevance(TaskContext context, RelevanceJudgment? relevance, double adapterRelevance)
    {
        var line = relevance is null
            ? $"relevance process={context.ProcessName} title=\"{context.WindowTitle}\" judge=none adapter={adapterRelevance:0.00}"
            : $"relevance process={context.ProcessName} title=\"{context.WindowTitle}\" text={(string.IsNullOrWhiteSpace(context.ScreenExcerpt) ? "none" : context.ScreenExcerpt.Length + "ch")} judge={(relevance.IsFallback ? "local" : "DeepSeek")} class={relevance.Classification} score={relevance.Score:0.00} adapter={adapterRelevance:0.00} reason=\"{relevance.Reason}\"";
        if (string.Equals(line, _lastRelevanceTrace, StringComparison.Ordinal))
        {
            return;
        }
        _lastRelevanceTrace = line;
        _services.Trace(line);
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..(length - 1)].TrimEnd() + "…";

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
    private async Task PreviewToolkitAsync(string feature)
    {
        if (!Enum.TryParse<ToolkitFeature>(feature, ignoreCase: true, out var parsed))
        {
            ToolkitStatus = "Unknown toolkit preview.";
            return;
        }
        var result = await _services.Overlays.PreviewAsync(parsed);
        ToolkitStatus = $"{parsed}: {result.Status}";
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
            if (_services.Recording.IsRecording)
            {
                _recordingTimer.Stop();
                await StopRecordingAsync();
            }
            await _services.Orchestrator.StopAsync();
            _sessionGazeActive = false;
            _services.Watchdog.Signal(Anchor.Infrastructure.Windows.SafetyReleaseReason.Shutdown);
            AddTimeline("Session complete", $"{FocusedDuration} focused · {InterruptionCount} interruptions");
            IsRunning = false;
            _lastSecureWindow = false;
            await ApplyToolkitStateAsync();
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
        _analyzer.Reset();
        Analytics.Reset();
        FocusedDuration = "0m 00s";
        RecoveryDuration = "0m 00s";
        InterruptionCount = 0;
        StatusMessage = "All locally stored session events were deleted.";
    }

    [RelayCommand]
    private async Task RefreshCamerasAsync()
    {
        IsBusy = true;
        GazeStatusMessage = "Asking Windows which cameras are attached…";
        IReadOnlyList<CameraDevice> windowsDevices = [];
        try
        {
            windowsDevices = await WindowsCameraEnumerator.ListAsync();
        }
        catch (Exception error)
        {
            AddTimeline("Camera enumeration", error.Message);
        }

        try
        {
            GazeStatusMessage = windowsDevices.Count == 0
                ? "Windows reports no camera. Starting the vision worker to double-check…"
                : $"Windows sees {Describe(windowsDevices)}. Starting the vision worker (first start can take ~30 s)…";
            if (!await _services.Inference.StartAsync())
            {
                ReplaceCameras(windowsDevices);
                var reason = _services.Inference.LastError ?? "unknown error";
                GazeStatusMessage = windowsDevices.Count == 0
                    ? $"No camera is attached and the vision worker could not start ({reason})."
                    : $"Windows sees {Describe(windowsDevices)}, but the vision worker could not start: {reason}. "
                      + "Run scripts\\build.ps1 to package Anchor.VisionWorker.exe, or create .venv from src\\Anchor.Worker.";
                return;
            }

            var openable = await _services.Inference.ListCamerasAsync();
            var merged = WindowsCameraEnumerator.Merge(windowsDevices, openable);
            ReplaceCameras(merged);
            GazeStatusMessage = openable.Count switch
            {
                0 when windowsDevices.Count == 0 =>
                    "No camera found. Plug in a webcam, then refresh.",
                0 =>
                    $"Windows sees {Describe(windowsDevices)}, but no frames arrived over DirectShow, Media Foundation or auto. Close other apps using the camera "
                    + "(Teams, Zoom, browser tabs), allow desktop apps under Settings > Privacy & security > Camera, "
                    + "then run camera-check.ps1 next to Anchor.exe for a per-backend report.",
                _ => $"Ready · {Describe(merged)} can be opened. Choose one and press Test gaze."
            };
        }
        catch (Exception error)
        {
            ReplaceCameras(windowsDevices);
            _services.LogError("camera check", error);
            GazeStatusMessage = windowsDevices.Count == 0
                ? "No camera found by Windows, and the vision worker's probe did not finish "
                  + $"({error.Message}). Plug in a webcam and refresh, or run camera-check.ps1 next to Anchor.exe."
                : $"Windows sees {Describe(windowsDevices)}, but the vision worker's probe did not finish ({error.Message}). "
                  + "Close other apps using the camera, then refresh or run camera-check.ps1 next to Anchor.exe for a per-backend report.";
        }
        finally
        {
            IsBusy = false;
        }

        static string Describe(IReadOnlyList<CameraDevice> devices) =>
            devices.Count == 1
                ? $"1 camera ({devices[0].Name})"
                : $"{devices.Count} cameras ({string.Join(", ", devices.Select(static item => item.Name))})";
    }

    private void ReplaceCameras(IReadOnlyList<CameraDevice> devices)
    {
        var previous = SelectedCamera?.Index;
        CameraDevices.Clear();
        foreach (var device in devices)
        {
            CameraDevices.Add(device);
        }
        SelectedCamera = CameraDevices.FirstOrDefault(device => device.Index == previous)
            ?? CameraDevices.FirstOrDefault();
    }

    [RelayCommand]
    private async Task StartGazeTestAsync()
    {
        if (IsGazeRunning || IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            if (!await _services.Inference.StartAsync())
            {
                GazeStatusMessage = "The local vision worker could not start.";
                return;
            }
            var configured = await _services.Inference.ConfigureGazeAsync(
                CreateGazeConfiguration(),
                CreateDisplaySignature());
            if (!configured.Accepted)
            {
                GazeStatusMessage = $"Settings rejected: {configured.Error}";
                return;
            }
            var started = await _services.Inference.StartGazeAsync();
            if (!started.Running)
            {
                GazeStatusMessage = $"Camera could not start: {started.Error}";
                return;
            }
            IsGazeRunning = true;
            GazeStatusMessage = started.Calibrated
                ? "Webcam open · gaze uses your saved calibration."
                : "Webcam open · click Calibrate by clicking to map your eyes to this screen.";
            _gazeTimer.Start();
        }
        catch (Exception error)
        {
            GazeStatusMessage = $"Gaze test failed: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyGazeSettingsAsync()
    {
        if (!IsGazeRunning)
        {
            GazeStatusMessage = "Start Test Gaze before applying live adjustments.";
            return;
        }
        var result = await _services.Inference.ConfigureGazeAsync(
            CreateGazeConfiguration(),
            CreateDisplaySignature());
        GazeStatusMessage = result.Accepted
            ? "Gaze adjustments saved."
            : $"Settings rejected: {result.Error}";
    }

    /// <summary>
    /// Opens the full-screen calibration surface. Each click pairs the target position with the gaze
    /// features held at that instant, which is far more reliable than asking the user to hold a pose.
    /// </summary>
    [RelayCommand]
    private async Task CalibrateGazeAsync()
    {
        if (!IsGazeRunning)
        {
            CalibrationStatus = "Open the webcam first — calibration needs live eye tracking.";
            return;
        }
        await _services.Inference.ResetCalibrationAsync();
        var window = new GazeCalibrationWindow
        {
            Capture = (x, y) => _services.Inference.AddCalibrationSampleAsync(x, y),
            Finish = () => _services.Inference.FinishCalibrationAsync(CreateDisplaySignature()),
            Closing = message => CalibrationStatus = message,
        };
        CalibrationStatus = $"Calibrating · click each of the {GazeCalibrationWindow.Targets.Length} dots while looking at it.";
        OverlayWindowHelper.ConfigureBounds(
            window,
            OverlayWindowHelper.GetActiveWorkArea(App.WindowHandle),
            clickThrough: false);
        window.Activate();
    }

    [RelayCommand]
    private async Task ResetCalibrationAsync()
    {
        var progress = await _services.Inference.ResetCalibrationAsync();
        CalibrationStatus = progress.Accepted
            ? "Calibration cleared · gaze falls back to a rough eye-direction estimate."
            : $"Could not clear the calibration: {progress.Error}";
    }

    [RelayCommand]
    private async Task StartWebcamRecordingAsync()
    {
        if (!IsGazeRunning)
        {
            WebcamRecordingStatusText = "Open the webcam before recording.";
            return;
        }
        var status = await _services.Inference.StartWebcamRecordingAsync(WebcamRecordingFolder);
        IsWebcamRecording = status.Recording;
        WebcamRecordingStatusText = status.Accepted
            ? $"Recording to {status.VideoPath}"
            : $"Recording could not start: {status.Error}";
        if (status.Recording)
        {
            _webcamRecordingTimer.Start();
        }
    }

    [RelayCommand]
    private async Task StopWebcamRecordingAsync()
    {
        _webcamRecordingTimer.Stop();
        var status = await _services.Inference.StopWebcamRecordingAsync();
        IsWebcamRecording = false;
        WebcamRecordingStatusText = string.IsNullOrEmpty(status.VideoPath)
            ? $"Recording stopped: {status.Error}"
            : $"Saved {status.FrameCount} frames ({Describe(status.Elapsed)}) to {status.VideoPath}";
    }

    [RelayCommand]
    private void OpenWebcamRecordingFolder()
    {
        Directory.CreateDirectory(WebcamRecordingFolder);
        Process.Start(new ProcessStartInfo(WebcamRecordingFolder) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenEvidenceRecordingFolder()
    {
        Directory.CreateDirectory(EvidenceRecordingFolder);
        Process.Start(new ProcessStartInfo(EvidenceRecordingFolder) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task StartEvidenceRecordingAsync()
    {
        if (IsRecording)
        {
            EvidenceRecordingStatus = "A recording is already running.";
            return;
        }
        EvidenceRecordingStatus = "Starting…";
        var result = await _services.Recording.StartAsync(
            EvidenceRecordingFolder,
            TrialMode.AnchorEnabled,
            "demo",
            fps: 15);
        if (result.Manifest is null)
        {
            EvidenceRecordingStatus = $"Could not start recording: {result.Error}";
            return;
        }
        IsRecording = true;
        RecordingElapsed = "00:00";
        LatestRecordingFiles = result.Manifest.VideoPath;
        RecordingStatus = "REC · evidence clip";
        EvidenceRecordingStatus = IsGazeRunning
            ? $"REC · screen + eyes + rating → {result.Manifest.VideoPath}"
            : $"REC · camera is closed, so the clip shows the screen and the rating only → {result.Manifest.VideoPath}";
        _recordingTimer.Start();
        AddTimeline("Evidence recording started", result.Manifest.TrialId);
    }

    [RelayCommand]
    private async Task StopEvidenceRecordingAsync()
    {
        var path = _services.Recording.CurrentManifest?.VideoPath;
        await StopRecordingAsync();
        EvidenceRecordingStatus = path is null
            ? "Not recording."
            : $"{RecordingStatus} · saved {path}";
    }

    private static string Describe(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1 ? $"{elapsed:mm\\:ss}" : $"{elapsed.TotalSeconds:0.0} s";

    private async void WebcamRecordingTimer_Tick(object? sender, object args)
    {
        var status = await _services.Inference.GetWebcamRecordingAsync();
        IsWebcamRecording = status.Recording;
        if (!status.Recording)
        {
            _webcamRecordingTimer.Stop();
            return;
        }
        WebcamRecordingStatusText = $"Recording · {Describe(status.Elapsed)} · {status.FrameCount} frames → {status.VideoPath}";
    }

    [RelayCommand]
    private async Task StopGazeTestAsync()
    {
        _gazeTimer.Stop();
        _recordingTimer.Stop();
        if (IsWebcamRecording)
        {
            await StopWebcamRecordingAsync();
        }
        await _services.Inference.StopGazeAsync();
        IsGazeRunning = false;
        GazePreviewSource = null;
        GazeCoordinates = "Unavailable";
        GazeConfidenceLabel = "—";
        GazeStatusMessage = "Camera off · saved adjustments will be used next time.";
    }

    [RelayCommand]
    private async Task ChooseRecordingFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) RecordingOutputFolder = folder.Path;
    }

    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        if (!IsRunning)
        {
            RecordingStatus = "Start a planned focus session before recording so task, gaze, and attention stay aligned.";
            return;
        }
        if (IsRecording) return;
        var mode = RecordingModeIndex == 0 ? TrialMode.Baseline : TrialMode.AnchorEnabled;
        RecordingStatus = "Starting local screen capture…";
        var result = await _services.Recording.StartAsync(
            RecordingOutputFolder,
            mode,
            ParticipantCode,
            fps: 15);
        if (result.Manifest is null)
        {
            RecordingStatus = $"Could not start recording: {result.Error}";
            return;
        }
        IsRecording = true;
        RecordingElapsed = "00:00";
        LatestRecordingFiles = result.Manifest.TrialId;
        RecordingStatus = mode == TrialMode.Baseline
            ? "REC · baseline · sensing on, interventions off"
            : "REC · Anchor enabled · interventions included";
        _recordingTimer.Start();
        AddTimeline("Recording started", $"{mode} · {result.Manifest.TrialId}");
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        if (!_services.Recording.IsRecording)
        {
            IsRecording = false;
            return;
        }
        _recordingTimer.Stop();
        var manifest = _services.Recording.CurrentManifest;
        var status = await _services.Recording.StopAsync();
        IsRecording = false;
        RecordingStatus = status.State == StudyRecordingState.Complete
            ? $"Complete · {status.FrameCount} frames · {status.ElapsedSeconds:0.0}s"
            : $"Recording failed: {status.ErrorCode}";
        if (manifest is not null)
        {
            LatestRecordingFiles = $"Video: {manifest.VideoPath}\nSummary: {manifest.SummaryPath}";
        }
        AddTimeline("Recording stopped", RecordingStatus);
    }

    [RelayCommand]
    private async Task CompareRecordingsAsync()
    {
        var firstPath = await PickSummaryAsync("Choose the baseline summary");
        if (firstPath is null) return;
        var secondPath = await PickSummaryAsync("Choose the Anchor-enabled summary");
        if (secondPath is null) return;
        var first = StudySummaryLoader.Load(firstPath);
        var second = StudySummaryLoader.Load(secondPath);
        if (first.Summary is null || second.Summary is null)
        {
            ComparisonReport = $"Cannot compare: {first.ErrorCode ?? second.ErrorCode}";
            return;
        }
        var outcome = RecordingComparison.TryCompare(first.Summary, second.Summary);
        if (outcome.Result is null)
        {
            ComparisonReport = $"Cannot compare: {outcome.ErrorCode}";
            return;
        }
        var result = outcome.Result;
        ComparisonReport =
            $"Gaze coverage: {result.UsableGazeCoverageDelta:+0.0%;-0.0%;0.0%}\n" +
            $"Gaze-away time: {result.GazeAwaySecondsDelta:+0.0;-0.0;0.0}s\n" +
            $"Distracted/low-relevance time: {result.DistractionSecondsDelta:+0.0;-0.0;0.0}s\n" +
            $"Interruptions: {result.InterruptionCountDelta:+#;-#;0}\n" +
            $"Recovery time: {result.RecoverySecondsDelta:+0.0;-0.0;0.0}s\n" +
            $"Subtasks completed: {result.SubtasksCompletedDelta:+#;-#;0}\n\n{result.Disclaimer}";
    }

    [RelayCommand]
    private void OpenRecordingFolder()
    {
        Directory.CreateDirectory(RecordingOutputFolder);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(RecordingOutputFolder);
        Process.Start(start);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _timer.Stop();
        _gazeTimer.Stop();
        _timer.Tick -= Timer_Tick;
        _gazeTimer.Tick -= GazeTimer_Tick;
        _recordingTimer.Tick -= RecordingTimer_Tick;
        _webcamRecordingTimer.Stop();
        _webcamRecordingTimer.Tick -= WebcamRecordingTimer_Tick;
        _services.Overlays.CurrentWindowMarkedRelevant -= Overlays_CurrentWindowMarkedRelevant;
        _services.Overlays.StepMarkedDoneFromBeacon -= Overlays_StepMarkedDoneFromBeacon;
        _services.Overlays.OverlaysCleared -= Overlays_Cleared;
        _services.Overlays.ImageBlur.StatusChanged -= ImageBlur_StatusChanged;
        _services.Overlays.BreakdownProvider = null;
        _services.Overlays.ReminderProvider = null;
        if (IsGazeRunning)
        {
            await _services.Inference.StopGazeAsync();
        }
        if (_services.Recording.IsRecording)
        {
            await _services.Recording.StopAsync();
        }
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
            var raw = _services.Sensors.Sample(
                $"{TaskTitle} {CurrentSubtask}",
                _services.Inference.IsAvailable,
                now);
            _lastSecureWindow = raw.IsSecureWindow;
            await ApplyToolkitStateAsync();
            await RefreshPageContextAsync(now);
            var gaze = _sessionGazeActive
                ? await _services.Inference.ReadGazeAsync()
                : null;
            if (gaze is not null)
            {
                await _services.Overlays.UpdateGazeAsync(gaze);
            }
            var context = _services.Sensors.CreateTaskContext(
                TaskTitle,
                CurrentSubtask,
                _userRelevantTargets.ToArray());
            ConsiderEvidence(context.WindowTitle, context.ProcessName);
            var userMarkedRelevant = TreatCurrentWindowAsRelevant
                || _userRelevantTargets.Contains(ContextIdentity(context));
            // Relevance is judged from what is actually on the page, so the text is read before
            // the verdict; secure windows are never read.
            var screen = await ReadScreenAsync(gaze, raw, now, context);
            var visibleText = screen
                ?? (_lastScreen is { } previous
                    && string.Equals(previous.ProcessName, context.ProcessName, StringComparison.OrdinalIgnoreCase)
                    && now - previous.Timestamp <= ScreenReadInterval * 3
                    ? previous
                    : null);
            if (visibleText is not null && !raw.IsSecureWindow)
            {
                context = context with { ScreenExcerpt = visibleText.Excerpt };
            }
            var relevance = _taskPlanner is null
                ? null
                : await _taskPlanner.JudgeRelevanceAsync(context);
            TraceRelevance(context, relevance, raw.AppRelevance);
            var contextIsSafe = !raw.IsSecureWindow
                && (userMarkedRelevant
                    || relevance?.Classification == RelevanceClass.Relevant
                    || (relevance?.Classification == RelevanceClass.Ambiguous && raw.AppRelevance >= 0.5)
                    || (relevance is null && raw.AppRelevance >= 0.65));
            if (contextIsSafe && screen is not null)
            {
                AdoptScreen(screen, now);
            }
            _services.Overlays.ImageBlur.UpdateScene(
                contextIsSafe,
                context.ProcessName,
                contextIsSafe ? context.WindowTitle : null,
                TaskTitle,
                CurrentSubtask,
                contextIsSafe ? _lastScreen : null);
            if (contextIsSafe)
            {
                _services.Orchestrator.ObserveContext(_services.Sensors.CreateContextObservation(
                    TaskTitle,
                    CurrentSubtask,
                    userMarkedRelevant
                        ? "Marked relevant by the user"
                        : relevance?.Reason ?? "Matched the active task",
                    confidence: Math.Max(raw.AppRelevance, relevance?.Score ?? 0),
                    evidenceTimestamp: now,
                    screen: _lastScreen,
                    keyCount: raw.KeyCount,
                    scrollReversalCount: raw.ScrollReversalCount,
                    mouseDistance: raw.MouseDistance,
                    isScrollBurst: _attentionFusion.LastScrollThrashSustained,
                    isPointerFidget: _attentionFusion.LastAimlessMouseSustained));
                _ = JudgeScreenProgressAsync(raw);
            }
            // Fidgeting with the cursor is movement without work, so it does not count as progress.
            var progressObserved = raw.KeyCount > 0
                || raw.ScrollReversalCount > 0
                || (raw.MouseDistance >= 4 && !_attentionFusion.LastAimlessMouseSustained);
            var activity = ActivityClassifier.Infer(
                context.ProcessName,
                context.WindowTitle,
                raw.KeyCount,
                raw.ScrollReversalCount,
                raw.MouseDistance);
            var fused = _attentionFusion.Apply(AttentionEvidence.At(
                now,
                raw.KeyCount,
                raw.MouseDistance,
                raw.IdleSeconds,
                raw.AppRelevance,
                relevance,
                userMarkedRelevant,
                gaze,
                _services.Sensors.IsGazeOnForegroundWindow(gaze),
                raw.AppSwitchCount,
                raw.ScrollReversalCount,
                progressObserved,
                raw.IsSecureWindow,
                _services.Inference.IsAvailable,
                scrollNotchCount: raw.ScrollNotchCount,
                navigationKeyCount: _services.Sensors.LastNavigationKeyCount,
                activity: activity,
                typedText: ResolveTypedText(activity, raw, visibleText),
                mouseNetDistance: _services.Sensors.LastMouseNetDistance,
                mouseDirectionChanges: _services.Sensors.LastMouseDirectionChanges,
                mouseClickCount: _services.Sensors.LastMouseClickCount,
                keyStrokes: _services.Sensors.LastKeyStrokes,
                screenTextChanged: ResolveScreenTextChanged(screen),
                hasTextCaret: _services.Sensors.LastHasTextCaret));
            var prediction = await _services.Orchestrator.ProcessAsync(fused.Window);
            await _services.Overlays.UpdateAttentionAsync(prediction);
            ApplyPrediction(prediction, AttentionAnalyzer.DescribePlace(context.ProcessName, context.Domain));
        }
        catch (Exception error)
        {
            _services.LogError("sensing tick", error);
            StatusMessage = $"Sensing recovered from an error: {error.Message}";
        }
        finally
        {
            _tickInFlight = false;
        }
    }

    private async void GazeTimer_Tick(object? sender, object e)
    {
        if (_gazeTickInFlight || !IsGazeRunning)
        {
            return;
        }
        _gazeTickInFlight = true;
        try
        {
            var sample = await _services.Inference.ReadGazeAsync();
            GazeConfidenceLabel = $"{sample.Confidence:P0}";
            GazeCoordinates = sample.Available
                ? $"X {sample.X:0.000} · Y {sample.Y:0.000} · yaw {sample.Yaw:0.0}° · pitch {sample.Pitch:0.0}°"
                : $"Unavailable · {sample.UnavailableReason}";
            if (sample.PreviewJpeg.Length > 0)
            {
                GazePreviewSource = await CreateBitmapAsync(sample.PreviewJpeg);
            }
        }
        catch (Exception error)
        {
            GazeStatusMessage = $"Gaze stream recovered from an error: {error.Message}";
        }
        finally
        {
            _gazeTickInFlight = false;
        }
    }

    private async void RecordingTimer_Tick(object? sender, object e)
    {
        if (_recordingTickInFlight || !IsRecording) return;
        _recordingTickInFlight = true;
        try
        {
            var gaze = _sessionGazeActive || IsGazeRunning ? await _services.Inference.ReadGazeAsync() : null;
            var prediction = _services.Orchestrator.LastPrediction
                ?? AttentionPrediction.Create(Anchor.Core.Models.AttentionState.Focused, 0.5, 0, ["awaiting_first_sample"]);
            var status = await _services.Recording.AppendSampleAsync(
                gaze,
                prediction,
                TaskTitle,
                CurrentSubtask);
            RecordingElapsed = TimeSpan.FromSeconds(status.ElapsedSeconds).ToString(@"mm\:ss");
            if (status.State == StudyRecordingState.Failed)
            {
                _recordingTimer.Stop();
                await _services.Recording.StopAsync();
                IsRecording = false;
                RecordingStatus = $"Recording failed: {status.ErrorCode}";
            }
        }
        catch (Exception error)
        {
            _recordingTimer.Stop();
            if (_services.Recording.IsRecording)
            {
                await _services.Recording.StopAsync();
            }
            IsRecording = false;
            RecordingStatus = $"Recording stopped after an error: {error.Message}";
        }
        finally
        {
            _recordingTickInFlight = false;
        }
    }

    private void ApplyPrediction(AttentionPrediction prediction, string? place = null)
    {
        var now = DateTimeOffset.UtcNow;
        _analyzer.Record(now, prediction, place);
        Analytics.Apply(_analyzer.Analyze(now));
        AttentionState = prediction.State.ToString();
        Confidence = $"{prediction.Confidence:P0}";
        ReasonSummary = prediction.ReasonCodes.Count == 0
            ? "No distraction signals"
            : string.Join(" · ", prediction.ReasonCodes.Select(static item => item.Replace('_', ' ')));
        CapabilityStatus = _sessionGazeActive
            ? $"{_services.Orchestrator.CapabilityStatus} · gaze live"
            : $"{_services.Orchestrator.CapabilityStatus} · no camera";

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
        IsPlanComplete = state.Progress.CurrentStep is null;
        if (state.Progress.PendingSuggestion is null)
        {
            ClearSuggestion();
        }
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

    private void Overlays_StepMarkedDoneFromBeacon(object? sender, EventArgs e) =>
        CompleteCurrentStep(CompletionSource.User, "ticked on the beacon");

    private void Overlays_CurrentWindowMarkedRelevant(object? sender, EventArgs e)
    {
        var context = _services.Sensors.CreateTaskContext(TaskTitle, CurrentSubtask);
        _userRelevantTargets.Add(ContextIdentity(context));
        StatusMessage = "This window is now treated as needed for the current task.";
        AddTimeline("Relevance corrected", context.WindowTitle ?? context.ProcessName);
    }

    private async void Overlays_Cleared(object? sender, EventArgs e)
    {
        if (StatusMessage.Contains("preview is visible", StringComparison.Ordinal))
        {
            StatusMessage = "Overlays released.";
        }
        if (ToolkitStatus.Contains("Previewing", StringComparison.Ordinal))
        {
            await ApplyToolkitStateAsync();
        }
    }

    private async Task<(string Step, string Source)> BreakDownRecoveryStepAsync(CancellationToken cancellationToken)
    {
        if (_taskPlanner?.Current?.Progress.CurrentStep is null)
        {
            return ("Choose one visible action that moves the task forward.", "Local fallback");
        }
        try
        {
            var context = _services.Sensors.CreateTaskContext(TaskTitle, CurrentSubtask, _userRelevantTargets.ToArray());
            var step = await _taskPlanner.BreakDownCurrentStepAsync(context, cancellationToken);
            return (step.Title, DeepSeekEnabled ? "DeepSeek breakdown" : "Local fallback");
        }
        catch
        {
            return (_taskPlanner.Current.Progress.CurrentStep.CompletionCriterion, "Local fallback");
        }
    }

    private static string ContextIdentity(TaskContext context) =>
        $"{context.ProcessName}|{context.Domain}|{context.WindowTitle}";

    private static async Task<string?> PickSummaryAsync(string title)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
            CommitButtonText = title
        };
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
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

    private GazeConfiguration CreateGazeConfiguration() => new(
        SelectedCamera?.Index ?? 0,
        GazeMirror,
        checked((int)GazeRotationDegrees),
        GazeOffsetX,
        GazeOffsetY,
        GazeSmoothing,
        GazeSensitivity,
        GazeMinimumConfidence);

    private static string CreateDisplaySignature()
    {
        var width = GetSystemMetrics(0);
        var height = GetSystemMetrics(1);
        var dpi = App.WindowHandle == IntPtr.Zero ? 96u : GetDpiForWindow(App.WindowHandle);
        return $"{width}x{height}@{dpi}";
    }

    private static async Task<ImageSource> CreateBitmapAsync(byte[] jpeg)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(jpeg);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private async Task ApplyToolkitStateAsync()
    {
        try
        {
            var results = await _services.Overlays.SetToolkitStateAsync(ToolkitState.FromPreferences(
                sessionActive: IsRunning,
                gazeSpotlight: GazeSpotlightEnabled,
                peripheralDim: VisualFilterEnabled,
                windowFirewall: WindowFirewallEnabled,
                pointerGuard: PointerGuardEnabled,
                secureWindow: _lastSecureWindow,
                browserImageBlur: BlurImagesEnabled,
                browserFutureTextMask: HideFutureTextEnabled,
                browserAnimationSuppression: SuppressAnimationsEnabled,
                browserClutterRemoval: RemovePageClutterEnabled,
                browserTextSimplification: SimplifyPageTextEnabled,
                taskKeywords: string.Join(' ', TaskEvidenceMatcher.Keywords(TaskTitle, CurrentSubtask))));
            ToolkitStatus = string.Join(" · ", results
                .Where(static result => result.Status != "Off")
                .Select(static result => $"{result.Feature}: {result.Status}"));
            if (string.IsNullOrWhiteSpace(ToolkitStatus))
            {
                ToolkitStatus = IsRunning
                    ? "Desktop tools off"
                    : "Desktop tools arm when a focus session starts";
            }
            await ApplyPageEditsAsync();
            UpdateBrowserStatus();
        }
        catch (Exception error)
        {
            ToolkitStatus = $"Desktop toolkit unavailable: {error.Message}";
        }
    }

    /// <summary>
    /// Pushes the page tools into the live browser over its DevTools endpoint. Nothing happens
    /// until the user opens the focused browser, and turning the tools off restores every page.
    /// </summary>
    private async Task ApplyPageEditsAsync()
    {
        var wantsEdits = IsRunning
            && (BlurImagesEnabled || RemovePageClutterEnabled || SimplifyPageTextEnabled);
        var request = wantsEdits
            ? new PageEditRequest(
                PixelateOffTaskPictures: BlurImagesEnabled,
                DeleteOffTaskBlocks: RemovePageClutterEnabled,
                SimplifySentences: SimplifyPageTextEnabled,
                Threshold: 0.5,
                MaxWords: 28,
                Keywords: TaskEvidenceMatcher.Keywords(TaskTitle, CurrentSubtask).ToArray())
            : PageEditRequest.Off;
        // The tick calls this constantly; the browser is only touched when something changed.
        var fingerprint = ChromeDevToolsBridge.BuildState(request);
        if (string.Equals(fingerprint, _lastPageEdit, StringComparison.Ordinal)
            || !await _services.PageBridge.IsAvailableAsync())
        {
            return;
        }

        _lastPageEdit = fingerprint;
        if (wantsEdits)
        {
            await _services.PageBridge.ApplyAsync(request);
        }
        else
        {
            await _services.PageBridge.ClearAsync();
        }
    }

    /// <summary>
    /// Asks the focused browser where the user is in the page, so the recovery card can name the
    /// site and how far they had read without any extension reporting it.
    /// </summary>
    private async Task RefreshPageContextAsync(DateTimeOffset now)
    {
        if (now - _lastPageContextRead < ScreenReadInterval || !_services.PageBridge.IsConnected)
        {
            return;
        }

        _lastPageContextRead = now;
        foreach (var message in await _services.PageBridge.ReadPageContextAsync())
        {
            _services.BrowserContext.Apply(message);
        }
    }

    /// <summary>Opens (or re-uses) the browser Anchor can edit, so no extension is needed.</summary>
    [RelayCommand]
    private async Task OpenFocusedBrowserAsync()
    {
        var opened = await _services.PageBridge.LaunchAsync();
        if (opened)
        {
            await ApplyPageEditsAsync();
        }

        UpdateBrowserStatus();
    }

    private void UpdateBrowserStatus()
    {
        IsBrowserConnected = _services.PageBridge.IsConnected;
        var wanted = new List<string>();
        if (BlurImagesEnabled) wanted.Add("image blur");
        if (HideFutureTextEnabled) wanted.Add("future-text mask");
        if (SuppressAnimationsEnabled) wanted.Add("animation pause");
        if (RemovePageClutterEnabled) wanted.Add("off-task blocks deleted");
        if (SimplifyPageTextEnabled) wanted.Add("sentences trimmed");
        var features = wanted.Count == 0 ? "no page tools selected" : string.Join(", ", wanted);
        BrowserStatus = IsBrowserConnected
            ? $"{_services.PageBridge.Status} · {features} applied by editing the page itself"
            : "Press Open focused browser and Anchor edits the pages you open in Chrome or Edge directly — no extension to install. Until then, image blur still works on the front window by softening photo-like regions in the captured pixels.";
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

}
