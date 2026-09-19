using Anchor.Core.Models;
using Anchor.Core.Services;
using Anchor.Infrastructure.Worker;

namespace Anchor_Desktop.Services;

public sealed class InferenceEngineAdapter : IInferenceEngine, IAsyncDisposable
{
    private readonly InferenceWorkerClient _client;
    private AttentionStateMachine _local = AttentionStateMachine.CreateDefault();

    public InferenceEngineAdapter(InferenceWorkerClient client)
    {
        _client = client;
    }

    public bool IsAvailable => _client.Mode == WorkerMode.Available;

    public Task<bool> StartAsync(CancellationToken cancellationToken = default) =>
        _client.StartAsync(cancellationToken);

    public async Task<AttentionPrediction> PredictAsync(
        SensorWindow window,
        CancellationToken cancellationToken = default)
    {
        var local = _local.Update(window);
        var remote = await _client.PredictAsync(window, cancellationToken);
        return local with
        {
            Confidence = Math.Max(local.Confidence, remote.Confidence),
            DistractionProbability = Math.Max(
                local.DistractionProbability,
                remote.DistractionProbability),
            ReasonCodes = local.ReasonCodes.Concat(remote.ReasonCodes).Distinct().ToArray()
        };
    }

    public Task<IReadOnlyList<CameraDevice>> ListCamerasAsync(
        CancellationToken cancellationToken = default) =>
        _client.ListCamerasAsync(cancellationToken);

    public Task<GazeConfigurationResult> ConfigureGazeAsync(
        GazeConfiguration configuration,
        string displaySignature,
        CancellationToken cancellationToken = default) =>
        _client.ConfigureGazeAsync(configuration, displaySignature, cancellationToken);

    public Task<GazeStatus> StartGazeAsync(CancellationToken cancellationToken = default) =>
        _client.StartGazeAsync(cancellationToken);

    public Task<GazeSample> ReadGazeAsync(CancellationToken cancellationToken = default) =>
        _client.ReadGazeAsync(cancellationToken);

    public Task<CalibrationProgress> AddCalibrationSampleAsync(
        double targetX,
        double targetY,
        CancellationToken cancellationToken = default) =>
        _client.AddCalibrationSampleAsync(targetX, targetY, cancellationToken);

    public Task<CalibrationResult> FinishCalibrationAsync(
        string displaySignature,
        CancellationToken cancellationToken = default) =>
        _client.FinishCalibrationAsync(displaySignature, cancellationToken);

    public Task<GazeStatus> StopGazeAsync(CancellationToken cancellationToken = default) =>
        _client.StopGazeAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _client.StopAsync(cancellationToken);
        _local = AttentionStateMachine.CreateDefault();
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
