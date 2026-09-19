using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class RecordingComparisonTests
{
    [Fact]
    public void Compare_reports_directional_differences_without_clinical_claims()
    {
        var baseline = new StudySummary("b", TrialMode.Baseline, 600, .60, 180, 240, 8, 40, 1);
        var enabled = new StudySummary("a", TrialMode.AnchorEnabled, 600, .85, 60, 90, 4, 15, 3);

        var result = RecordingComparison.Compare(baseline, enabled);

        Assert.Equal(0.25, result.UsableGazeCoverageDelta, 3);
        Assert.Equal(-120, result.GazeAwaySecondsDelta);
        Assert.Equal(-150, result.DistractionSecondsDelta);
        Assert.Equal(-4, result.InterruptionCountDelta);
        Assert.Equal(-25, result.RecoverySecondsDelta);
        Assert.Contains("observational", result.Disclaimer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_baselines_are_rejected()
    {
        var first = Summary("a", TrialMode.Baseline);
        var second = Summary("b", TrialMode.Baseline);

        var outcome = RecordingComparison.TryCompare(first, second);

        Assert.Null(outcome.Result);
        Assert.Equal("one_baseline_and_one_enabled_required", outcome.ErrorCode);
    }

    [Fact]
    public void Zero_duration_is_rejected()
    {
        var baseline = Summary("a", TrialMode.Baseline) with { DurationSeconds = 0 };

        var outcome = RecordingComparison.TryCompare(baseline, Summary("b", TrialMode.AnchorEnabled));

        Assert.Null(outcome.Result);
        Assert.Equal("invalid_duration", outcome.ErrorCode);
    }

    [Fact]
    public void Metric_schema_mismatch_is_rejected()
    {
        var baseline = Summary("a", TrialMode.Baseline) with { SchemaVersion = 1 };
        var enabled = Summary("b", TrialMode.AnchorEnabled) with { SchemaVersion = 2 };

        var outcome = RecordingComparison.TryCompare(baseline, enabled);

        Assert.Null(outcome.Result);
        Assert.Equal("metric_schema_mismatch", outcome.ErrorCode);
    }

    private static StudySummary Summary(string id, TrialMode mode) =>
        new(id, mode, 60, .8, 10, 12, 2, 5, 1);
}
