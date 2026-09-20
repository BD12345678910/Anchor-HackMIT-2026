using Anchor.Core.Models;
using Anchor.Core.Services;
using Anchor.Infrastructure.Windows;
using Anchor.Infrastructure.Browser;
using Anchor_Desktop.Overlays;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Anchor_Desktop.Services;

public sealed class OverlayPresenter : IInterventionPresenter, IRestrictiveInterventionController
{
    private readonly NativeBridgeServer? _browserBridge;
    private readonly PointerConfinement _pointer = new();
    private GoalBeaconWindow? _beacon;
    private VisualFilterWindow? _filter;
    private RecoveryCardWindow? _recovery;
    private IntentionGateWindow? _gate;
    private GazeSpotlightWindow? _spotlight;
    private WindowFirewallWindow? _firewall;
    private ToolkitState _toolkitState = ToolkitState.Off;
    private (double X, double Y, DateTimeOffset At)? _lastSpotlight;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewTimer;
    private int _focusedTicks;

    private static readonly TimeSpan PreviewDuration = TimeSpan.FromSeconds(8);

    public OverlayPresenter(NativeBridgeServer? browserBridge = null)
    {
        _browserBridge = browserBridge;
    }

    public string TaskTitle { get; set; } = "Return to your task";
    public string CurrentSubtask { get; set; } = "Choose the smallest next action";
    public string ProgressLabel { get; set; } = "0 of 1";
    public bool ReducedMotion { get; set; }
    public Func<CancellationToken, Task<(string Step, string Source)>>? BreakdownProvider { get; set; }
    public event EventHandler? CurrentWindowMarkedRelevant;
    public bool EnableVisualFilter { get; set; } = true;
    public bool EnablePointerGuard { get; set; }
    public bool HasRestrictiveOverlay =>
        _filter is not null
        || _gate is not null
        || _spotlight is not null
        || _firewall is not null
        || _pointer.IsConfined;
    public bool HasAnyOverlay => HasRestrictiveOverlay || _recovery is not null || _beacon is not null;

    /// <summary>Raised after every overlay has been closed, so bound status text can stop claiming one is showing.</summary>
    public event EventHandler? OverlaysCleared;
    public ToolkitState CurrentToolkitState => _toolkitState;

    public async Task<IReadOnlyList<ToolkitApplyResult>> SetToolkitStateAsync(
        ToolkitState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<ToolkitApplyResult>();
        await App.DispatcherQueue.EnqueueAsync(() =>
        {
            _toolkitState = state;
            _browserBridge?.UpdateSnapshot("toolkitState", System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "toolkitState",
                imageBlur = state.BrowserImageBlur,
                futureTextMask = state.BrowserFutureTextMask,
                suppressAnimations = state.BrowserAnimationSuppression,
                gazeSpotlight = state.GazeSpotlight,
                peripheralDim = state.PeripheralDim,
                windowFirewall = state.WindowFirewall,
                pointerGuard = state.PointerGuard,
                secureWindow = state.SecureWindow
            }));
            EnableVisualFilter = state.PeripheralDim;
            EnablePointerGuard = state.PointerGuard;
            if (state.SecureWindow)
            {
                Close(ref _filter);
                Close(ref _spotlight);
                Close(ref _firewall);
                results.Add(new(ToolkitFeature.PeripheralDim, "Suppressed for secure window", false));
                results.Add(new(ToolkitFeature.GazeSpotlight, "Suppressed for secure window", false));
                results.Add(new(ToolkitFeature.WindowFirewall, "Suppressed for secure window", false));
                return;
            }

            if (state.PeripheralDim)
            {
                results.Add(new(
                    ToolkitFeature.PeripheralDim,
                    _filter is not null ? "Dimming the desktop now" : "Armed · dims the desktop when distraction is confirmed",
                    _filter is not null));
            }
            else
            {
                Close(ref _filter);
                results.Add(new(ToolkitFeature.PeripheralDim, "Off", false));
            }

            if (state.WindowFirewall)
            {
                results.Add(new(
                    ToolkitFeature.WindowFirewall,
                    _firewall is not null ? "Shading the off-task window now" : "Armed · shades off-task windows while drifting",
                    _firewall is not null));
            }
            else
            {
                Close(ref _firewall);
                results.Add(new(ToolkitFeature.WindowFirewall, "Off", false));
            }

            if (!state.GazeSpotlight)
            {
                Close(ref _spotlight);
                _lastSpotlight = null;
            }
            results.Add(new(
                ToolkitFeature.GazeSpotlight,
                state.GazeSpotlight
                    ? (_spotlight is not null ? "Following your gaze" : "Armed · needs a live camera (Test gaze)")
                    : "Off",
                _spotlight is not null));
            results.Add(new(
                ToolkitFeature.PointerGuard,
                state.PointerGuard ? "Armed · confines the pointer during intention gates" : "Off",
                _pointer.IsConfined));
        });
        return results;
    }

    public async Task<ToolkitApplyResult> PreviewAsync(
        ToolkitFeature feature,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ToolkitApplyResult? result = null;
        await App.DispatcherQueue.EnqueueAsync(() =>
        {
            switch (feature)
            {
                case ToolkitFeature.GazeSpotlight:
                    ShowSpotlight(0.5, 0.5);
                    break;
                case ToolkitFeature.PeripheralDim:
                    ShowFilter();
                    break;
                case ToolkitFeature.WindowFirewall:
                    if (!ShowFirewall())
                    {
                        result = new(feature, "Suppressed for Anchor or system window", false);
                    }
                    break;
                case ToolkitFeature.IntentionGate:
                    ShowGate("preview");
                    break;
                case ToolkitFeature.ContextReminder:
                    ShowRecovery(null, "preview");
                    break;
                case ToolkitFeature.PointerGuard:
                    EnablePointerGuard = true;
                    ShowGate("pointer guard preview");
                    break;
            }
            var timed = feature is ToolkitFeature.GazeSpotlight or ToolkitFeature.PeripheralDim or ToolkitFeature.WindowFirewall;
            if (timed)
            {
                SchedulePreviewClear();
            }
            result ??= new ToolkitApplyResult(
                feature,
                timed
                    ? $"Previewing for {PreviewDuration.TotalSeconds:0} s · Esc clears it now"
                    : "Previewing · use the card's buttons or Esc to close it",
                true);
        });
        return result!;
    }

    /// <summary>Drives the armed desktop tools from the fused attention state.</summary>
    public Task UpdateAttentionAsync(AttentionPrediction prediction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        cancellationToken.ThrowIfCancellationRequested();
        return App.DispatcherQueue.EnqueueAsync(() =>
        {
            if (_toolkitState.SecureWindow)
            {
                return;
            }

            var offTask = prediction.ReasonCodes.Contains("low_task_relevance", StringComparer.Ordinal)
                || prediction.ReasonCodes.Contains("off_task_window", StringComparer.Ordinal);
            var gateInFront = _gate is not null && OverlayWindowHelper.IsForeground(_gate);
            App.Services.Trace(
                $"attention {prediction.State} {prediction.Confidence:0.00} [{string.Join(',', prediction.ReasonCodes)}] " +
                $"offTask={offTask} ticks={_focusedTicks} gate={_gate is not null} gateFront={gateInFront} filter={_filter is not null} preview={_previewTimer is not null}");
            switch (prediction.State)
            {
                case AttentionState.Drifting when offTask:
                case AttentionState.Distracted when offTask:
                    _focusedTicks = 0;
                    if (_toolkitState.WindowFirewall)
                    {
                        ShowFirewall();
                    }
                    if (prediction.State == AttentionState.Distracted && EnableVisualFilter && _gate is null)
                    {
                        ShowFilter();
                    }
                    break;
                case AttentionState.Focused:
                case AttentionState.Recovering:
                case AttentionState.Drifting:
                case AttentionState.Distracted:
                    // Off-task treatments end once the foreground is task-relevant again, even if the
                    // user is merely idle there. A gate the user is looking at stays; one left in the
                    // background is dismissed, since Windows may never have let it take focus.
                    if (++_focusedTicks >= 2 && _previewTimer is null && !gateInFront)
                    {
                        ReleasePointer();
                        Close(ref _gate);
                        Close(ref _firewall);
                        Close(ref _filter);
                        if (!HasAnyOverlay)
                        {
                            OverlaysCleared?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    break;
                default:
                    _focusedTicks = 0;
                    break;
            }
        });
    }

    private void SchedulePreviewClear()
    {
        _previewTimer?.Stop();
        _previewTimer ??= App.DispatcherQueue.CreateTimer();
        _previewTimer.IsRepeating = false;
        _previewTimer.Interval = PreviewDuration;
        _previewTimer.Tick -= PreviewTimer_Tick;
        _previewTimer.Tick += PreviewTimer_Tick;
        _previewTimer.Start();
    }

    private void PreviewTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        sender.Tick -= PreviewTimer_Tick;
        _previewTimer = null;
        if (_gate is null)
        {
            Close(ref _filter);
        }
        Close(ref _spotlight);
        Close(ref _firewall);
        _lastSpotlight = null;
        if (!HasAnyOverlay)
        {
            OverlaysCleared?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task UpdateGazeAsync(
        GazeSample sample,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        cancellationToken.ThrowIfCancellationRequested();
        return App.DispatcherQueue.EnqueueAsync(() =>
        {
            if (!_toolkitState.GazeSpotlight
                || _toolkitState.SecureWindow
                || !sample.Available
                || sample.Confidence < 0.6
                || sample.X is null
                || sample.Y is null)
            {
                Close(ref _spotlight);
                _lastSpotlight = null;
                return;
            }

            var x = sample.X.Value;
            var y = sample.Y.Value;
            if (_lastSpotlight is { } previous)
            {
                var elapsed = Math.Clamp((sample.Timestamp - previous.At).TotalSeconds, 0.016, 0.25);
                var maximumDelta = 0.75 * elapsed;
                x = MoveToward(previous.X, x, maximumDelta);
                y = MoveToward(previous.Y, y, maximumDelta);
            }
            _lastSpotlight = (x, y, sample.Timestamp);
            ShowSpotlight(x, y);
        });
    }

    public Task PresentAsync(
        InterventionDecision decision,
        ContextCapsule? capsule,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return App.DispatcherQueue.EnqueueAsync(() =>
        {
            switch (decision.Kind)
            {
                case InterventionKind.BeaconPulse:
                    ShowBeacon(pulse: true);
                    break;
                case InterventionKind.VisualFilter:
                    if (EnableVisualFilter)
                    {
                        ShowFilter();
                    }
                    break;
                case InterventionKind.IntentionGate:
                    ShowGate(decision.ReasonCode);
                    break;
                case InterventionKind.RecoveryCard:
                    ShowRecovery(capsule, decision.ReasonCode);
                    break;
                case InterventionKind.BreakSuggestion:
                    ShowRecovery(capsule, "A short reset may help");
                    break;
            }
        });
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return App.DispatcherQueue.EnqueueAsync(ClearOverlays);
    }

    public void ShowBeacon(bool pulse = false)
    {
        _beacon ??= new GoalBeaconWindow();
        _beacon.SetGoal(TaskTitle, CurrentSubtask, ProgressLabel, pulse, ReducedMotion);
        OverlayWindowHelper.Configure(_beacon, 500, 118, clickThrough: true);
    }

    public void UpdateGoal(string goal, string? currentSubtask, string progressLabel)
    {
        TaskTitle = goal;
        CurrentSubtask = string.IsNullOrWhiteSpace(currentSubtask)
            ? "Task complete"
            : currentSubtask;
        ProgressLabel = progressLabel;
        if (_beacon is not null)
        {
            ShowBeacon();
        }
    }

    public void ReleasePointer() => _pointer.Release();

    public void ClearOverlays()
    {
        _previewTimer?.Stop();
        _previewTimer = null;
        _focusedTicks = 0;
        ReleasePointer();
        Close(ref _beacon);
        Close(ref _filter);
        Close(ref _recovery);
        Close(ref _gate);
        Close(ref _spotlight);
        Close(ref _firewall);
        _lastSpotlight = null;
        OverlaysCleared?.Invoke(this, EventArgs.Empty);
    }

    public void ReleaseHooks()
    {
        // The host window owns its native message hook and remains responsive.
    }

    private void ShowFilter()
    {
        var filter = _filter;
        if (filter is null)
        {
            var window = new VisualFilterWindow();
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_filter, window))
                {
                    _filter = null;
                }
            };
            _filter = filter = window;
        }
        OverlayWindowHelper.Configure(filter, 0, 0, clickThrough: true, fullScreen: true);
        OverlayWindowHelper.MakeTranslucent(filter, 0x70);
    }

    private void ShowRecovery(ContextCapsule? capsule, string reason)
    {
        Close(ref _recovery);
        _recovery = new RecoveryCardWindow();
        _recovery.SetContext(
            TaskTitle,
            capsule?.CurrentSubtask ?? CurrentSubtask,
            capsule?.LastAction ?? "You were working on this task.",
            capsule?.NextAction ?? "Choose the smallest next action.",
            reason,
            capsule?.RelevanceReason ?? string.Empty,
            capsule?.EvidenceTimestamp,
            capsule?.IsEstimatedContext ?? true,
            capsule?.RestoreTarget);
        _recovery.ReturnedToTask += (_, _) =>
        {
            App.Services.Orchestrator.RecordInterventionResponse(InterventionResponse.ReturnedToTask);
            Close(ref _recovery);
            Close(ref _filter);
        };
        _recovery.Dismissed += (_, _) =>
        {
            App.Services.Orchestrator.RecordInterventionResponse(InterventionResponse.Dismissed);
            Close(ref _recovery);
        };
        _recovery.BreakRequested += (_, _) =>
        {
            App.Services.Orchestrator.RecordInterventionResponse(InterventionResponse.DeliberateBreak);
            Close(ref _recovery);
            Close(ref _filter);
        };
        _recovery.SmallerStepRequested += async (_, _) =>
        {
            var active = _recovery;
            if (active is null) return;
            try
            {
                var result = BreakdownProvider is null
                    ? ($"open the task window, find the saved place, then {(capsule?.NextAction ?? "take one small action").ToLowerInvariant()}", "Local fallback")
                    : await BreakdownProvider(CancellationToken.None);
                if (ReferenceEquals(_recovery, active)) active.SetSmallerStep(result.Item1, result.Item2);
            }
            catch (Exception error)
            {
                if (ReferenceEquals(_recovery, active))
                {
                    active.SetSmallerStep(
                        capsule?.NextAction ?? "Return to the active step and do its first visible action.",
                        $"Local fallback · {error.GetType().Name}");
                }
            }
        };
        _recovery.Activate();
        OverlayWindowHelper.Center(_recovery, 860, 500);
    }

    private void ShowGate(string reason)
    {
        Close(ref _gate);
        _gate = new IntentionGateWindow();
        _gate.SetPrompt(TaskTitle, CurrentSubtask, reason);
        _gate.ReturnedToTask += (_, _) =>
        {
            App.Services.Orchestrator.RecordInterventionResponse(InterventionResponse.ReturnedToTask);
            ReleasePointer();
            Close(ref _gate);
            Close(ref _filter);
        };
        _gate.ContinuedAnyway += (_, _) =>
        {
            App.Services.Orchestrator.RecordInterventionResponse(InterventionResponse.Dismissed);
            ReleasePointer();
            Close(ref _gate);
        };
        _gate.ParkedForLater += (_, _) =>
        {
            App.Services.Orchestrator.RecordInterventionResponse(InterventionResponse.Snoozed);
            ReleasePointer();
            Close(ref _gate);
            Close(ref _filter);
        };
        _gate.Disabled += (_, _) =>
        {
            App.Services.Orchestrator.RecordInterventionResponse(InterventionResponse.Disabled);
            ClearOverlays();
        };
        _gate.NeededForTask += (_, _) =>
        {
            App.Services.Orchestrator.RecordInterventionResponse(InterventionResponse.NeededForTask);
            CurrentWindowMarkedRelevant?.Invoke(this, EventArgs.Empty);
            ReleasePointer();
            Close(ref _gate);
            Close(ref _filter);
        };
        _gate.DeliberateBreak += (_, _) =>
        {
            App.Services.Orchestrator.RecordInterventionResponse(InterventionResponse.DeliberateBreak);
            ReleasePointer();
            Close(ref _gate);
            Close(ref _filter);
        };
        _gate.LostFocus += (_, _) =>
        {
            if (_gate is not null)
            {
                App.Services.Watchdog.Signal(SafetyReleaseReason.FocusLost);
            }
        };
        if (EnableVisualFilter)
        {
            ShowFilter();
        }
        _gate.Activate();
        OverlayWindowHelper.Center(_gate, 780, 430);
        if (EnablePointerGuard)
        {
            var width = GetSystemMetrics(0);
            var height = GetSystemMetrics(1);
            _pointer.Confine(new ScreenRect(
                Math.Max(0, (width - 620) / 2),
                Math.Max(0, (height - 420) / 2),
                Math.Min(width, (width + 620) / 2),
                Math.Min(height, (height + 420) / 2)));
        }
    }

    private void ShowSpotlight(double normalizedX, double normalizedY)
    {
        var foreground = GetForegroundWindow();
        var work = OverlayWindowHelper.GetActiveWorkArea(
            foreground != IntPtr.Zero ? foreground : App.WindowHandle);
        if (_spotlight is null)
        {
            var window = new GazeSpotlightWindow();
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_spotlight, window))
                {
                    _spotlight = null;
                }
            };
            _spotlight = window;
        }
        _spotlight.SetAperture(normalizedX, normalizedY, 220, work);
    }

    private bool ShowFirewall()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == App.WindowHandle || !GetWindowRect(foreground, out var native))
        {
            Close(ref _firewall);
            return false;
        }
        var work = OverlayWindowHelper.GetActiveWorkArea(foreground);
        var clipped = OverlayGeometry.PlaceFirewall(
            new OverlayWorkArea(work.X, work.Y, work.Width, work.Height, 1),
            new OverlayBounds(
                native.Left,
                native.Top,
                Math.Max(0, native.Right - native.Left),
                Math.Max(0, native.Bottom - native.Top)),
            _toolkitState.SecureWindow);
        if (clipped is null)
        {
            Close(ref _firewall);
            return false;
        }
        if (_firewall is null)
        {
            var window = new WindowFirewallWindow();
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_firewall, window))
                {
                    _firewall = null;
                }
            };
            _firewall = window;
        }
        var firewall = _firewall;
        OverlayWindowHelper.ConfigureBounds(
            firewall,
            new RectInt32(clipped.X, clipped.Y, clipped.Width, clipped.Height),
            clickThrough: true);
        OverlayWindowHelper.MakeTranslucent(firewall, 0xB4);
        return true;
    }

    private static double MoveToward(double current, double target, double maximumDelta) =>
        current + Math.Clamp(target - current, -maximumDelta, maximumDelta);

    private static void Close<T>(ref T? window) where T : Microsoft.UI.Xaml.Window
    {
        var closing = window;
        window = null;
        closing?.Close();
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
