using Anchor.Core.Models;
using Anchor.Core.Services;
using Anchor.Infrastructure.Windows;
using Anchor_Desktop.Overlays;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Anchor_Desktop.Services;

public sealed class OverlayPresenter : IInterventionPresenter, IRestrictiveInterventionController
{
    private readonly PointerConfinement _pointer = new();
    private GoalBeaconWindow? _beacon;
    private VisualFilterWindow? _filter;
    private RecoveryCardWindow? _recovery;
    private IntentionGateWindow? _gate;
    private GazeSpotlightWindow? _spotlight;
    private WindowFirewallWindow? _firewall;
    private ToolkitState _toolkitState = ToolkitState.Off;
    private (double X, double Y, DateTimeOffset At)? _lastSpotlight;

    public string TaskTitle { get; set; } = "Return to your task";
    public string CurrentSubtask { get; set; } = "Choose the smallest next action";
    public string ProgressLabel { get; set; } = "0 of 1";
    public bool ReducedMotion { get; set; }
    public bool EnableVisualFilter { get; set; } = true;
    public bool EnablePointerGuard { get; set; }
    public bool HasRestrictiveOverlay =>
        _filter is not null
        || _gate is not null
        || _spotlight is not null
        || _firewall is not null
        || _pointer.IsConfined;
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
                ShowFilter();
                results.Add(new(ToolkitFeature.PeripheralDim, "Active", true));
            }
            else
            {
                Close(ref _filter);
                results.Add(new(ToolkitFeature.PeripheralDim, "Off", false));
            }

            if (state.WindowFirewall)
            {
                var visible = ShowFirewall();
                results.Add(new(
                    ToolkitFeature.WindowFirewall,
                    visible ? "Active" : "Suppressed for Anchor or system window",
                    visible));
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
                state.GazeSpotlight ? "Active · waiting for confident gaze" : "Off",
                _spotlight is not null));
            results.Add(new(
                ToolkitFeature.PointerGuard,
                state.PointerGuard ? "Active for intention gates" : "Off",
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
            result ??= new ToolkitApplyResult(feature, "Previewing", true);
        });
        return result!;
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
        _beacon.Activate();
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
        ReleasePointer();
        Close(ref _beacon);
        Close(ref _filter);
        Close(ref _recovery);
        Close(ref _gate);
        Close(ref _spotlight);
        Close(ref _firewall);
        _lastSpotlight = null;
    }

    public void ReleaseHooks()
    {
        // The host window owns its native message hook and remains responsive.
    }

    private void ShowFilter()
    {
        if (_filter is null)
        {
            var window = new VisualFilterWindow();
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_filter, window))
                {
                    _filter = null;
                }
            };
            _filter = window;
        }
        _filter.Activate();
        OverlayWindowHelper.Configure(_filter, 0, 0, clickThrough: true, fullScreen: true);
    }

    private void ShowRecovery(ContextCapsule? capsule, string reason)
    {
        Close(ref _recovery);
        _recovery = new RecoveryCardWindow();
        _recovery.SetContext(
            TaskTitle,
            capsule?.LastAction ?? "You were working on this task.",
            capsule?.NextAction ?? "Choose the smallest next action.",
            reason);
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
        _recovery.Activate();
        OverlayWindowHelper.Center(_recovery, 720, 380);
    }

    private void ShowGate(string reason)
    {
        Close(ref _gate);
        _gate = new IntentionGateWindow();
        _gate.SetPrompt(TaskTitle, reason);
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
        _gate.LostFocus += (_, _) =>
        {
            if (_gate is not null)
            {
                App.Services.Watchdog.Signal(SafetyReleaseReason.FocusLost);
            }
        };
        _gate.Activate();
        OverlayWindowHelper.Center(_gate, 540, 320);
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
        if (EnableVisualFilter)
        {
            ShowFilter();
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
        _spotlight.Activate();
        OverlayWindowHelper.ConfigureBounds(_spotlight, work, clickThrough: true);
        _spotlight.SetAperture(normalizedX, normalizedY, 220, work.Width, work.Height);
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
        _firewall.Activate();
        OverlayWindowHelper.ConfigureBounds(
            _firewall,
            new RectInt32(clipped.X, clipped.Y, clipped.Width, clipped.Height),
            clickThrough: true);
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
