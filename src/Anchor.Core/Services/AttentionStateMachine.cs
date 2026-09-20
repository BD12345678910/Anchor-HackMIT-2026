using Anchor.Core.Models;

namespace Anchor.Core.Services;

public sealed class AttentionStateMachine
{
    private readonly TemporalEvidenceBuffer _evidence = new();
    private int _highDistractionWindows;
    private int _focusedWindows;
    private int _stuckWindows;

    private AttentionStateMachine()
    {
    }

    public AttentionState State { get; private set; } = AttentionState.Unknown;

    public static AttentionStateMachine CreateDefault() => new();

    public AttentionPrediction Update(SensorWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.IsSecureWindow)
        {
            State = AttentionState.Unknown;
            ResetCounters();
            return AttentionPrediction.Create(State, 1, 0, ["secure_window"]);
        }

        if (window.IsManualReport)
        {
            State = AttentionState.Distracted;
            _highDistractionWindows = 3;
            _focusedWindows = 0;
            return AttentionPrediction.Create(State, 1, 1, ["manual_report"]);
        }

        var reasons = new List<string>();
        var rawEvidence = CalculateDistractionEvidence(window, reasons);
        var temporal = _evidence.Add(rawEvidence);

        if (!window.IsWorkerAvailable)
        {
            reasons.Add("worker_unavailable");
        }

        if (window.AppRelevance >= 0.55
            && window.ScrollReversalCount >= 8
            && window.IdleSeconds < 8)
        {
            _stuckWindows++;
            _highDistractionWindows = 0;
            _focusedWindows = 0;
            reasons.Add("stuck_phrase");
            State = _stuckWindows >= 2 ? AttentionState.Stuck : AttentionState.Drifting;
            return AttentionPrediction.Create(
                State,
                _stuckWindows >= 2 ? 0.84 : 0.62,
                temporal.FiveSecond,
                reasons);
        }

        _stuckWindows = 0;

        if (rawEvidence >= 0.72)
        {
            _highDistractionWindows++;
            _focusedWindows = 0;
            State = _highDistractionWindows >= 3
                ? AttentionState.Distracted
                : AttentionState.Drifting;
        }
        else if (rawEvidence <= 0.35)
        {
            _focusedWindows++;
            _highDistractionWindows = 0;
            State = State == AttentionState.Distracted && _focusedWindows < 3
                ? AttentionState.Recovering
                : AttentionState.Focused;
        }
        else
        {
            _highDistractionWindows = 0;
            _focusedWindows = 0;
            State = AttentionState.Drifting;
        }

        var consistency = 1 - Math.Abs(temporal.OneSecond - temporal.FiveSecond);
        var confidence = State switch
        {
            AttentionState.Distracted => 0.55 + (0.25 * rawEvidence) + (0.05 * Math.Min(_highDistractionWindows, 4)),
            AttentionState.Drifting => 0.4 + (0.4 * rawEvidence),
            AttentionState.Recovering => 0.7,
            _ => (Math.Abs(rawEvidence - 0.5) * 1.6) + (consistency * 0.2)
        };
        return AttentionPrediction.Create(State, Math.Clamp(confidence, 0, 1), temporal.FiveSecond, reasons);
    }

    private static double CalculateDistractionEvidence(SensorWindow window, List<string> reasons)
    {
        var evidence = (1 - window.AppRelevance) * 0.52;
        if (window.AppRelevance < 0.35)
        {
            reasons.Add("low_task_relevance");
        }
        if (window.AppRelevance < 0.2)
        {
            evidence += 0.24;
            reasons.Add("off_task_window");
        }

        evidence += Math.Clamp(window.IdleSeconds / 15, 0, 1) * 0.16;
        if (window.IdleSeconds >= 5)
        {
            reasons.Add("idle_pause");
        }

        evidence += Math.Clamp(window.AppSwitchCount / 4d, 0, 1) * 0.14;
        if (window.AppSwitchCount >= 3)
        {
            reasons.Add("rapid_switching");
        }

        evidence += Math.Clamp(window.ScrollReversalCount / 5d, 0, 1) * 0.10;
        if (window.ScrollReversalCount >= 4)
        {
            reasons.Add("scroll_loop");
        }

        evidence += Math.Clamp(window.MouseDistance / 250, 0, 1) * 0.08;
        if (window.MouseDistance >= 200)
        {
            reasons.Add("pointer_wandering");
        }

        if (window.GazeAvailable && window.GazeAwaySustained)
        {
            evidence += (1 - window.GazePresence) * 0.18;
            reasons.Add("gaze_away_sustained");
        }

        if (window.NoProgressSustained)
        {
            evidence += 0.12;
            reasons.Add("no_progress_sustained");
        }

        if (window.KeyCount > 0 && window.AppRelevance >= 0.5)
        {
            evidence -= Math.Clamp(window.KeyCount / 10d, 0, 1) * 0.08;
        }

        return Math.Clamp(evidence, 0, 1);
    }

    private void ResetCounters()
    {
        _highDistractionWindows = 0;
        _focusedWindows = 0;
        _stuckWindows = 0;
    }
}
