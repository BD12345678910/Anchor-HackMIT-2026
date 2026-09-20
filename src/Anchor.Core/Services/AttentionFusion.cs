using Anchor.Core.Models;

namespace Anchor.Core.Services;

public sealed record AttentionEvidence(
    DateTimeOffset Timestamp,
    int KeyCount,
    double MouseDistance,
    double IdleSeconds,
    double AdapterRelevance,
    RelevanceJudgment? RelevanceJudgment,
    bool UserMarkedRelevant,
    GazeSample? Gaze,
    bool GazeOnTaskRegion,
    int AppSwitchCount,
    int ScrollReversalCount,
    bool ProgressObserved,
    bool IsSecureWindow,
    bool IsWorkerAvailable,
    bool IsManualReport,
    int ScrollNotchCount = 0,
    int NavigationKeyCount = 0,
    ActivityKind Activity = ActivityKind.Unknown,
    string? TypedText = null)
{
    public static AttentionEvidence At(
        DateTimeOffset timestamp,
        int keyCount = 0,
        double mouseDistance = 0,
        double idleSeconds = 0,
        double adapterRelevance = 0.5,
        RelevanceJudgment? relevanceJudgment = null,
        bool userMarkedRelevant = false,
        GazeSample? gaze = null,
        bool gazeOnTaskRegion = true,
        int appSwitchCount = 0,
        int scrollReversalCount = 0,
        bool progressObserved = true,
        bool isSecureWindow = false,
        bool isWorkerAvailable = true,
        bool isManualReport = false,
        int scrollNotchCount = 0,
        int navigationKeyCount = 0,
        ActivityKind activity = ActivityKind.Unknown,
        string? typedText = null) =>
        new(
            timestamp,
            keyCount,
            mouseDistance,
            idleSeconds,
            adapterRelevance,
            relevanceJudgment,
            userMarkedRelevant,
            gaze,
            gazeOnTaskRegion,
            appSwitchCount,
            scrollReversalCount,
            progressObserved,
            isSecureWindow,
            isWorkerAvailable,
            isManualReport,
            scrollNotchCount,
            navigationKeyCount,
            activity,
            typedText);
}

public sealed record AttentionFusionResult(
    SensorWindow Window,
    AttentionPrediction Prediction);

public sealed class AttentionFusion
{
    private static readonly TimeSpan SustainedEvidenceDuration = TimeSpan.FromSeconds(4);

    /// <summary>A flick of the wheel is not a distraction; a couple of seconds of it is.</summary>
    public static readonly TimeSpan ScrollBurstDuration = TimeSpan.FromSeconds(2);

    private readonly AttentionStateMachine _stateMachine = AttentionStateMachine.CreateDefault();
    private readonly ScrollBehaviorAnalyzer _scroll = new();
    private DateTimeOffset? _gazeAwaySince;
    private DateTimeOffset? _noProgressSince;

    /// <summary>How the last few seconds of scrolling looked, for the recovery card.</summary>
    public ScrollBehavior? LastScrollBehavior { get; private set; }

    /// <summary>Whether the text most recently typed read as words or as mashing.</summary>
    public TypingQuality? LastTypingQuality { get; private set; }

    /// <summary>Whether the most recent window held a scroll burst long enough to count.</summary>
    public bool LastScrollThrashSustained { get; private set; }

    public AttentionFusionResult Apply(AttentionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var reasons = new List<string>();
        var relevance = ResolveRelevance(evidence, reasons);
        var gazeAvailable = evidence.Gaze is
        {
            Available: true,
            FacePresent: true,
            Confidence: >= 0.6
        };

        if (gazeAvailable && !evidence.GazeOnTaskRegion)
        {
            _gazeAwaySince ??= evidence.Timestamp;
        }
        else
        {
            _gazeAwaySince = null;
        }

        if (evidence.ProgressObserved)
        {
            _noProgressSince = null;
        }
        else
        {
            _noProgressSince ??= evidence.Timestamp;
        }

        var scroll = _scroll.Observe(
            evidence.Timestamp,
            evidence.ScrollNotchCount,
            evidence.ScrollReversalCount,
            // Page-up/page-down are scrolling, not writing: only real typing means the user is
            // working in the document rather than flicking through it.
            Math.Max(0, evidence.KeyCount - evidence.NavigationKeyCount));
        LastScrollBehavior = scroll;
        var scrollThrashSustained = scroll.IsThrashing
            && scroll.StartedAt is { } burstStart
            && evidence.Timestamp - burstStart >= ScrollBurstDuration;
        LastScrollThrashSustained = scrollThrashSustained;

        LastTypingQuality = evidence.TypedText is null
            ? null
            : TypingQualityAnalyzer.Assess(evidence.TypedText);

        var gazeAwaySustained = IsSustained(_gazeAwaySince, evidence.Timestamp);
        var noProgressSustained = IsSustained(_noProgressSince, evidence.Timestamp);
        var window = SensorWindow.Create(
            keyCount: evidence.KeyCount,
            mouseDistance: evidence.MouseDistance,
            idleSeconds: evidence.IdleSeconds,
            appRelevance: relevance,
            gazePresence: gazeAvailable && evidence.GazeOnTaskRegion ? 1 : 0,
            appSwitchCount: evidence.AppSwitchCount,
            scrollReversalCount: evidence.ScrollReversalCount,
            isSecureWindow: evidence.IsSecureWindow,
            isWorkerAvailable: evidence.IsWorkerAvailable,
            isManualReport: evidence.IsManualReport,
            timestamp: evidence.Timestamp,
            gazeAvailable: gazeAvailable,
            gazeAwaySustained: gazeAwaySustained,
            progressObserved: evidence.ProgressObserved,
            noProgressSustained: noProgressSustained,
            scrollNotchCount: evidence.ScrollNotchCount,
            scrollThrashSustained: scrollThrashSustained,
            activity: evidence.Activity,
            gibberishTyping: LastTypingQuality?.IsGibberish == true);
        var prediction = _stateMachine.Update(window);
        return new AttentionFusionResult(
            window,
            prediction with
            {
                ReasonCodes = prediction.ReasonCodes.Concat(reasons).Distinct().ToArray()
            });
    }

    private static double ResolveRelevance(AttentionEvidence evidence, List<string> reasons)
    {
        if (evidence.IsSecureWindow)
        {
            return 1;
        }
        if (evidence.UserMarkedRelevant)
        {
            reasons.Add("user_relevance_override");
            return 1;
        }
        if (evidence.RelevanceJudgment is { } judgment
            && judgment.ExpiresAt > evidence.Timestamp)
        {
            if (judgment.Classification == RelevanceClass.LikelyDetour)
            {
                reasons.Add("semantic_detour");
            }
            else if (judgment.Classification == RelevanceClass.Relevant)
            {
                reasons.Add("semantic_relevance");
            }
            return Math.Clamp(judgment.Score, 0, 1);
        }
        reasons.Add("adapter_relevance");
        return Math.Clamp(evidence.AdapterRelevance, 0, 1);
    }

    private static bool IsSustained(DateTimeOffset? since, DateTimeOffset now) =>
        since.HasValue && now - since.Value >= SustainedEvidenceDuration;
}
