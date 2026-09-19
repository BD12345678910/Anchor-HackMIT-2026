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

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        client.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
