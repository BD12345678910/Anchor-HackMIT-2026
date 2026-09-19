using Anchor.Core.Models;
using Anchor.Core.Services;
using Anchor.Infrastructure.Worker;

namespace Anchor_Desktop.Services;

public sealed class InferenceEngineAdapter(InferenceWorkerClient client) : IInferenceEngine, IAsyncDisposable
{
    public bool IsAvailable => client.Mode == WorkerMode.Available;

    public Task<bool> StartAsync(CancellationToken cancellationToken = default) =>
        client.StartAsync(cancellationToken);

    public Task<AttentionPrediction> PredictAsync(
        SensorWindow window,
        CancellationToken cancellationToken = default) =>
        client.PredictAsync(window, cancellationToken);

    public Task<IReadOnlyList<CameraDevice>> ListCamerasAsync(
        CancellationToken cancellationToken = default) =>
        client.ListCamerasAsync(cancellationToken);

    public Task<GazeConfigurationResult> ConfigureGazeAsync(
        GazeConfiguration configuration,
        string displaySignature,
        CancellationToken cancellationToken = default) =>
        client.ConfigureGazeAsync(configuration, displaySignature, cancellationToken);

    public Task<GazeStatus> StartGazeAsync(CancellationToken cancellationToken = default) =>
        client.StartGazeAsync(cancellationToken);

    public Task<GazeSample> ReadGazeAsync(CancellationToken cancellationToken = default) =>
        client.ReadGazeAsync(cancellationToken);

    public Task<CalibrationProgress> AddCalibrationSampleAsync(
        double targetX,
        double targetY,
        CancellationToken cancellationToken = default) =>
        client.AddCalibrationSampleAsync(targetX, targetY, cancellationToken);

    public Task<CalibrationResult> FinishCalibrationAsync(
        string displaySignature,
        CancellationToken cancellationToken = default) =>
        client.FinishCalibrationAsync(displaySignature, cancellationToken);

    public Task<GazeStatus> StopGazeAsync(CancellationToken cancellationToken = default) =>
        client.StopGazeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        client.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
