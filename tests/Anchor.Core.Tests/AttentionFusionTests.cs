using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class AttentionFusionTests
{
    [Fact]
    public void One_gaze_away_sample_never_confirms_distraction()
    {
        var fusion = new AttentionFusion();
        var timestamp = DateTimeOffset.Parse("2026-09-20T02:00:00Z");

        var result = fusion.Apply(AttentionEvidence.At(
            timestamp,
            adapterRelevance: 0.9,
            gaze: ConfidentGaze(timestamp),
            gazeOnTaskRegion: false,
            idleSeconds: 0,
            progressObserved: true));

        Assert.NotEqual(AttentionState.Distracted, result.Prediction.State);
        Assert.DoesNotContain("gaze_away_sustained", result.Prediction.ReasonCodes);
    }

    [Fact]
    public void Sustained_combined_evidence_confirms_distraction_only_over_time()
    {
        var fusion = new AttentionFusion();
        var start = DateTimeOffset.Parse("2026-09-20T02:00:00Z");
        var predictions = Enumerable.Range(0, 6)
            .Select(index =>
            {
                var timestamp = start.AddSeconds(index * 2);
                return fusion.Apply(AttentionEvidence.At(
                    timestamp,
                    adapterRelevance: 0.1,
                    gaze: ConfidentGaze(timestamp),
                    gazeOnTaskRegion: false,
                    idleSeconds: 0,
                    appSwitchCount: 2,
                    progressObserved: false));
            })
            .ToArray();

        Assert.NotEqual(AttentionState.Distracted, predictions[0].Prediction.State);
        Assert.Equal(AttentionState.Distracted, predictions[^1].Prediction.State);
        Assert.Contains("gaze_away_sustained", predictions[^1].Prediction.ReasonCodes);
        Assert.Contains("no_progress_sustained", predictions[^1].Prediction.ReasonCodes);
    }

    [Fact]
    public void User_marked_relevant_overrides_llm_detour_judgment()
    {
        var fusion = new AttentionFusion();
        var timestamp = DateTimeOffset.Parse("2026-09-20T02:00:00Z");
        var judgment = new RelevanceJudgment(
            0.1,
            RelevanceClass.LikelyDetour,
            "Unrelated browsing",
            false,
            timestamp.AddMinutes(5));

        var result = fusion.Apply(AttentionEvidence.At(
            timestamp,
            adapterRelevance: 0.2,
            relevanceJudgment: judgment,
            userMarkedRelevant: true));

        Assert.Equal(1.0, result.Window.AppRelevance);
        Assert.Contains("user_relevance_override", result.Prediction.ReasonCodes);
        Assert.DoesNotContain("semantic_detour", result.Prediction.ReasonCodes);
    }

    [Fact]
    public void No_camera_is_neutral_and_never_emits_gaze_absent()
    {
        var fusion = new AttentionFusion();

        var result = fusion.Apply(AttentionEvidence.At(
            DateTimeOffset.Parse("2026-09-20T02:00:00Z"),
            adapterRelevance: 0.9,
            gaze: null,
            gazeOnTaskRegion: false));

        Assert.False(result.Window.GazeAvailable);
        Assert.DoesNotContain("gaze_absent", result.Prediction.ReasonCodes);
        Assert.DoesNotContain("gaze_away_sustained", result.Prediction.ReasonCodes);
    }

    private static GazeSample ConfidentGaze(DateTimeOffset timestamp) => new(
        0.95,
        0.5,
        0.9,
        true,
        timestamp,
        0,
        0,
        0,
        [],
        string.Empty);
}

