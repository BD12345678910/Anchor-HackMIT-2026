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
    bool IsManualReport)
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
        bool isManualReport = false) =>
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
            isManualReport);
}

public sealed record AttentionFusionResult(
    SensorWindow Window,
    AttentionPrediction Prediction);

public sealed class AttentionFusion
{
    private static readonly TimeSpan SustainedEvidenceDuration = TimeSpan.FromSeconds(4);
    private readonly AttentionStateMachine _stateMachine = AttentionStateMachine.CreateDefault();
    private DateTimeOffset? _gazeAwaySince;
    private DateTimeOffset? _noProgressSince;

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
            noProgressSustained: noProgressSustained);
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
