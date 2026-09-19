using Anchor.Core.Models;

namespace Anchor.Core.Services;

public sealed class AttentionStateMachine
{
    private readonly TemporalEvidenceBuffer _evidence = new();
    private int _highDistractionWindows;
    private int _focusedWindows;

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
        var confidence = Math.Clamp((Math.Abs(rawEvidence - 0.5) * 1.6) + (consistency * 0.2), 0, 1);
        return AttentionPrediction.Create(State, confidence, temporal.FiveSecond, reasons);
    }

    private static double CalculateDistractionEvidence(SensorWindow window, List<string> reasons)
    {
        var evidence = (1 - window.AppRelevance) * 0.42;
        if (window.AppRelevance < 0.35)
        {
            reasons.Add("low_task_relevance");
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

        if (window.IsWorkerAvailable)
        {
            evidence += (1 - window.GazePresence) * 0.10;
            if (window.GazePresence < 0.35)
            {
                reasons.Add("gaze_absent");
            }
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
    }
}
