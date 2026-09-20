using Anchor.Core.Models;

namespace Anchor.Core.Services;

public sealed class AttentionStateMachine
{
    private readonly TemporalEvidenceBuffer _evidence = new();
    private int _highDistractionWindows;
    private int _focusedWindows;
    private int _stuckWindows;
    private int _scrollThrashWindows;

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

        // Flicking through a document is its own kind of lost: the right file is in front, but
        // nothing is being read. It is reported separately from an off-task window so the recovery
        // card can name the page the user left behind.
        if (window.ScrollThrashSustained && window.IdleSeconds < 5)
        {
            _scrollThrashWindows++;
            _highDistractionWindows = 0;
            _focusedWindows = 0;
            _stuckWindows = 0;
            reasons.Add("scroll_thrash");
            State = _scrollThrashWindows >= 2 ? AttentionState.Stuck : AttentionState.Drifting;
            return AttentionPrediction.Create(
                State,
                _scrollThrashWindows >= 2 ? 0.86 : 0.64,
                temporal.FiveSecond,
                reasons);
        }

        _scrollThrashWindows = 0;

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

        // Stillness is not evidence. Someone reading a difficult page, watching a lecture or
        // thinking through a proof produces no input at all, and that is what focus looks like.
        // Long stillness is only reported once another signal already says the person is gone.
        if (window.IdleSeconds >= UnattendedSeconds
            && window.GazeAvailable
            && window.GazePresence < 0.2)
        {
            reasons.Add("away_from_screen");
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

        // Distance on its own says nothing: dragging a window across two monitors covers more
        // ground than any amount of fidgeting. What counts is travel that arrives nowhere, seen
        // for a few seconds, and sheer excess of it — never its absence.
        var excessMotion = Math.Clamp((window.MouseDistance - RestlessMousePixels) / RestlessMousePixels, 0, 1);
        if (window.AimlessMouseSustained)
        {
            evidence += 0.2 + (excessMotion * 0.1);
            reasons.Add("pointer_wandering");
        }
        else
        {
            evidence += excessMotion * excessMotion * 0.12;
        }

        if (window.MouseClickCount >= 12 && window.KeyCount < MouseBehaviorAnalyzer.TypingKeyCount)
        {
            evidence += 0.1;
            reasons.Add("click_mashing");
        }

        if (window.GazeAvailable && window.GazeAwaySustained)
        {
            evidence += (1 - window.GazePresence) * 0.18;
            reasons.Add("gaze_away_sustained");
        }

        if (window.NoProgressSustained && !IsComposing(window.Activity))
        {
            evidence += 0.12;
            reasons.Add("no_progress_sustained");
        }

        // Characters going nowhere: typed into a page that accepts none, or a run of keys that do
        // nothing. The shape of the keystrokes says this even where no text can be read back.
        if (window.RandomTypingSustained)
        {
            evidence += 0.2;
            reasons.Add("random_typing");
        }

        // Text arriving with no words in it is the opposite of thinking: keyboard mashing, a held
        // key, or typing into the wrong place entirely.
        if (window.GibberishTyping)
        {
            evidence += 0.3;
            reasons.Add("gibberish_typing");
        }
        else if (!window.RandomTypingSustained && window.KeyCount > 0 && window.AppRelevance >= 0.5)
        {
            evidence -= Math.Clamp(window.KeyCount / 10d, 0, 1) * 0.08;
        }

        return Math.Clamp(evidence, 0, 1);
    }

    /// <summary>Pointer travel a window of ordinary work stays under.</summary>
    private const double RestlessMousePixels = 900;

    /// <summary>Stillness long enough to be worth naming, once gaze agrees nobody is there.</summary>
    private const double UnattendedSeconds = 120;

    /// <summary>Seconds of stillness that mean nothing for this kind of work.</summary>
    public static double ThinkingTolerance(ActivityKind activity) => activity switch
    {
        ActivityKind.Coding or ActivityKind.Writing or ActivityKind.ProblemSolving => 30,
        ActivityKind.Reading => 8,
        ActivityKind.Watching => 20,
        _ => 0
    };

    private static bool IsComposing(ActivityKind activity) =>
        activity is ActivityKind.Coding or ActivityKind.Writing or ActivityKind.ProblemSolving;

    private void ResetCounters()
    {
        _highDistractionWindows = 0;
        _focusedWindows = 0;
        _stuckWindows = 0;
        _scrollThrashWindows = 0;
    }
}
