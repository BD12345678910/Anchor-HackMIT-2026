namespace Anchor.Core.Models;

public enum TrialMode
{
    Baseline,
    AnchorEnabled
}

public enum StudyRecordingState
{
    Idle,
    Recording,
    Complete,
    Failed
}

public sealed record StudyRecordingManifest(
    string TrialId,
    TrialMode TrialMode,
    string VideoPath,
    string EventsPath,
    string SamplesPath,
    string SummaryPath,
    string ManifestPath);

public sealed record StudyRecordingStatus(
    string TrialId,
    StudyRecordingState State,
    int FrameCount,
    int DroppedFrames,
    double ElapsedSeconds,
    bool VideoUsable,
    string? ErrorCode);

public sealed record StudyRecordingSample(
    long AtMilliseconds,
    double? GazeX,
    double? GazeY,
    double Confidence,
    bool FacePresent,
    string AttentionState,
    double DistractionProbability,
    string Task,
    string Subtask);

public sealed record StudySummary(
    string TrialId,
    TrialMode TrialMode,
    double DurationSeconds,
    double UsableGazeCoverage,
    double GazeAwaySeconds,
    double DistractionSeconds,
    int InterruptionCount,
    double RecoverySeconds,
    int InterventionCount,
    int SchemaVersion = 1,
    string? ManifestPath = null,
    int SubtasksCompleted = 0);
