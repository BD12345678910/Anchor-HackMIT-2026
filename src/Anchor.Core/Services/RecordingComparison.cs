using System.Text.Json;
using Anchor.Core.Models;

namespace Anchor.Core.Services;

public sealed record RecordingComparisonResult(
    string BaselineTrialId,
    string EnabledTrialId,
    double UsableGazeCoverageDelta,
    double GazeAwaySecondsDelta,
    double DistractionSecondsDelta,
    int InterruptionCountDelta,
    double RecoverySecondsDelta,
    int InterventionCountDelta,
    int SubtasksCompletedDelta,
    string Disclaimer);

public sealed record RecordingComparisonOutcome(
    RecordingComparisonResult? Result,
    string? ErrorCode);

public static class RecordingComparison
{
    public static RecordingComparisonResult Compare(StudySummary first, StudySummary second)
    {
        var outcome = TryCompare(first, second);
        return outcome.Result
            ?? throw new InvalidOperationException(outcome.ErrorCode ?? "comparison_failed");
    }

    public static RecordingComparisonOutcome TryCompare(StudySummary first, StudySummary second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.TrialMode == second.TrialMode)
        {
            return new(null, "one_baseline_and_one_enabled_required");
        }
        if (first.DurationSeconds <= 0 || second.DurationSeconds <= 0)
        {
            return new(null, "invalid_duration");
        }
        if (!MetricsAreFinite(first) || !MetricsAreFinite(second))
        {
            return new(null, "missing_metric");
        }
        if (first.SchemaVersion != second.SchemaVersion)
        {
            return new(null, "metric_schema_mismatch");
        }
        var baseline = first.TrialMode == TrialMode.Baseline ? first : second;
        var enabled = first.TrialMode == TrialMode.AnchorEnabled ? first : second;
        return new(new RecordingComparisonResult(
            baseline.TrialId,
            enabled.TrialId,
            enabled.UsableGazeCoverage - baseline.UsableGazeCoverage,
            enabled.GazeAwaySeconds - baseline.GazeAwaySeconds,
            enabled.DistractionSeconds - baseline.DistractionSeconds,
            enabled.InterruptionCount - baseline.InterruptionCount,
            enabled.RecoverySeconds - baseline.RecoverySeconds,
            enabled.InterventionCount - baseline.InterventionCount,
            enabled.SubtasksCompleted - baseline.SubtasksCompleted,
            "Observational comparison only. This single-session result is not a diagnosis or a clinical effectiveness claim."), null);
    }

    private static bool MetricsAreFinite(StudySummary item) =>
        double.IsFinite(item.DurationSeconds)
        && double.IsFinite(item.UsableGazeCoverage)
        && double.IsFinite(item.GazeAwaySeconds)
        && double.IsFinite(item.DistractionSeconds)
        && double.IsFinite(item.RecoverySeconds);
}

public sealed record StudySummaryLoadResult(StudySummary? Summary, string? ErrorCode);

public static class StudySummaryLoader
{
    public static StudySummaryLoadResult Load(string summaryPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(summaryPath);
            using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
            var root = document.RootElement;
            var manifestPath = fullPath.EndsWith(".summary.json", StringComparison.OrdinalIgnoreCase)
                ? fullPath[..^".summary.json".Length] + ".manifest.json"
                : string.Empty;
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                return new(null, "associated_manifest_missing");
            }
            var mode = ReadString(root, "trialMode") switch
            {
                "baseline" => TrialMode.Baseline,
                "anchor_enabled" => TrialMode.AnchorEnabled,
                _ => (TrialMode?)null
            };
            if (mode is null) return new(null, "invalid_trial_mode");
            var summary = new StudySummary(
                ReadString(root, "trialId"),
                mode.Value,
                ReadDouble(root, "durationSeconds"),
                ReadDouble(root, "usableGazeCoverage"),
                ReadDouble(root, "gazeAwaySeconds"),
                ReadDouble(root, "distractionSeconds"),
                ReadInt(root, "interruptionCount"),
                ReadDouble(root, "recoverySeconds"),
                ReadInt(root, "interventionCount"),
                ReadInt(root, "schemaVersion"),
                manifestPath,
                ReadInt(root, "subtasksCompleted"));
            if (summary.SchemaVersion <= 0) return new(null, "missing_metric_schema");
            return new(summary, null);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(null, "summary_unreadable");
        }
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var item) ? item.GetString() ?? string.Empty : string.Empty;

    private static double ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var item) && item.TryGetDouble(out var value) ? value : double.NaN;

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var item) && item.TryGetInt32(out var value) ? value : 0;
}
