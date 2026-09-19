using Anchor.Core.Models;
using Anchor.Core.Services;
using Anchor.Infrastructure.Windows;
using Anchor_Desktop.Overlays;
using System.Runtime.InteropServices;

namespace Anchor_Desktop.Services;

public sealed class OverlayPresenter : IInterventionPresenter, IRestrictiveInterventionController
{
    private readonly PointerConfinement _pointer = new();
    private GoalBeaconWindow? _beacon;
    private VisualFilterWindow? _filter;
    private RecoveryCardWindow? _recovery;
    private IntentionGateWindow? _gate;

    public string TaskTitle { get; set; } = "Return to your task";
    public bool EnableVisualFilter { get; set; } = true;
    public bool EnablePointerGuard { get; set; }
    public bool HasRestrictiveOverlay => _filter is not null || _gate is not null || _pointer.IsConfined;

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
        _beacon.SetTask(TaskTitle, pulse);
        _beacon.Activate();
    }

    public void ReleasePointer() => _pointer.Release();

    public void ClearOverlays()
    {
        ReleasePointer();
        Close(ref _beacon);
        Close(ref _filter);
        Close(ref _recovery);
        Close(ref _gate);
    }

    public void ReleaseHooks()
    {
        // The host window owns its native message hook and remains responsive.
    }

    private void ShowFilter()
    {
        _filter ??= new VisualFilterWindow();
        _filter.Activate();
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

    private static void Close<T>(ref T? window) where T : Microsoft.UI.Xaml.Window
    {
        var closing = window;
        window = null;
        closing?.Close();
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
