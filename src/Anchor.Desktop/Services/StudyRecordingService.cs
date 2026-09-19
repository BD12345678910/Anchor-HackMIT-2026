using System.Diagnostics;
using Anchor.Core.Models;

namespace Anchor_Desktop.Services;

public sealed class StudyRecordingService : IAsyncDisposable
{
    private readonly InferenceEngineAdapter _inference;
    private readonly Anchor.Core.Services.SessionOrchestrator _orchestrator;
    private readonly Stopwatch _elapsed = new();

    public StudyRecordingService(
        InferenceEngineAdapter inference,
        Anchor.Core.Services.SessionOrchestrator orchestrator)
    {
        _inference = inference;
        _orchestrator = orchestrator;
    }

    public StudyRecordingManifest? CurrentManifest { get; private set; }
    public TrialMode? CurrentMode { get; private set; }
    public bool IsRecording => CurrentManifest is not null;

    public async Task<(StudyRecordingManifest? Manifest, string? Error)> StartAsync(
        string outputDirectory,
        TrialMode mode,
        string participantCode,
        int displayIndex = 1,
        int fps = 15,
        CancellationToken cancellationToken = default)
    {
        if (IsRecording) return (null, "recording_already_active");
        if (!_inference.IsAvailable && !await _inference.StartAsync(cancellationToken))
        {
            return (null, "worker_unavailable");
        }
        var result = await _inference.StartRecordingAsync(
            Path.GetFullPath(outputDirectory), mode, participantCode, displayIndex, fps, cancellationToken);
        if (result.Manifest is null) return result;
        CurrentManifest = result.Manifest;
        CurrentMode = mode;
        _orchestrator.InterventionsEnabled = mode == TrialMode.AnchorEnabled;
        _elapsed.Restart();
        return result;
    }

    public Task<StudyRecordingStatus> AppendSampleAsync(
        GazeSample? gaze,
        AttentionPrediction prediction,
        string task,
        string subtask,
        CancellationToken cancellationToken = default)
    {
        if (!IsRecording)
        {
            return Task.FromResult(new StudyRecordingStatus("", StudyRecordingState.Idle, 0, 0, 0, false, null));
        }
        return _inference.AppendRecordingSampleAsync(new StudyRecordingSample(
            _elapsed.ElapsedMilliseconds,
            gaze?.X,
            gaze?.Y,
            gaze?.Confidence ?? 0,
            gaze?.FacePresent ?? false,
            prediction.State.ToString().ToLowerInvariant(),
            prediction.DistractionProbability,
            task,
            subtask), cancellationToken);
    }

    public Task<StudyRecordingStatus> AppendEventAsync(
        string type,
        bool presented,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsRecording)
        {
            return Task.FromResult(new StudyRecordingStatus("", StudyRecordingState.Idle, 0, 0, 0, false, null));
        }
        return _inference.AppendRecordingEventAsync(new Dictionary<string, object?>
        {
            ["type"] = type,
            ["at_ms"] = _elapsed.ElapsedMilliseconds,
            ["presented"] = presented,
            ["detail"] = detail
        }, cancellationToken);
    }

    public void RecordIntervention(InterventionDecision decision, ContextCapsule? capsule) =>
        _ = AppendEventSafelyAsync(decision, capsule);

    public void RecordSubtaskCompleted(string title) =>
        _ = AppendSimpleEventSafelyAsync("subtask_completed", title);

    public Task<StudyRecordingStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        _inference.GetRecordingStatusAsync(cancellationToken);

    public async Task<StudyRecordingStatus> StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRecording)
        {
            return new StudyRecordingStatus("", StudyRecordingState.Idle, 0, 0, 0, false, null);
        }
        var status = await _inference.StopRecordingAsync(cancellationToken);
        _elapsed.Reset();
        CurrentManifest = null;
        CurrentMode = null;
        _orchestrator.InterventionsEnabled = true;
        return status;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRecording) await StopAsync();
    }

    private async Task AppendEventSafelyAsync(InterventionDecision decision, ContextCapsule? capsule)
    {
        try
        {
            await AppendEventAsync(
                "intervention",
                presented: true,
                $"{decision.Kind}:{decision.ReasonCode}:{capsule?.Reason}");
        }
        catch
        {
            // Recording degradation must never block or crash an intervention.
        }
    }

    private async Task AppendSimpleEventSafelyAsync(string type, string detail)
    {
        try { await AppendEventAsync(type, presented: false, detail); }
        catch { }
    }
}
