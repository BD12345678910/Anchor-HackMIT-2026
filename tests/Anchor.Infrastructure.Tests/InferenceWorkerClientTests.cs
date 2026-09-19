using Anchor.Core.Models;
using Anchor.Infrastructure.Worker;

namespace Anchor.Infrastructure.Tests;

public sealed class InferenceWorkerClientTests
{
    [Fact]
    public async Task Missing_worker_enters_deterministic_degraded_mode()
    {
        var options = new InferenceWorkerOptions(
            PythonExecutable: Path.Combine(Path.GetTempPath(), "missing-anchor-python.exe"),
            WorkerDirectory: Path.GetTempPath(),
            StartupTimeout: TimeSpan.FromMilliseconds(200),
            RpcDeadline: TimeSpan.FromMilliseconds(200),
            MaxRestarts: 0);
        await using var client = new InferenceWorkerClient(options);

        var started = await client.StartAsync();
        var prediction = await client.PredictAsync(SensorWindow.Create(
            keyCount: 5,
            mouseDistance: 10,
            idleSeconds: 0,
            appRelevance: 0.95));

        Assert.False(started);
        Assert.Equal(WorkerMode.DeterministicFallback, client.Mode);
        Assert.InRange(prediction.DistractionProbability, 0, 1);
        Assert.Contains("worker_unavailable", prediction.ReasonCodes);
    }

    [Fact]
    public async Task Real_worker_authenticates_reports_health_and_predicts()
    {
        var root = FindRepositoryRoot();
        var options = new InferenceWorkerOptions(
            PythonExecutable: Path.Combine(root, ".venv", "Scripts", "python.exe"),
            WorkerDirectory: Path.Combine(root, "src", "Anchor.Worker"),
            StartupTimeout: TimeSpan.FromSeconds(5),
            RpcDeadline: TimeSpan.FromSeconds(2),
            MaxRestarts: 1);
        await using var client = new InferenceWorkerClient(options);

        var started = await client.StartAsync();
        var prediction = await client.PredictAsync(SensorWindow.Create(
            keyCount: 10,
            mouseDistance: 0,
            idleSeconds: 0,
            appRelevance: 1,
            isManualReport: true));

        Assert.True(started, client.LastError);
        Assert.Equal(WorkerMode.Available, client.Mode);
        Assert.Equal(1, prediction.DistractionProbability);
        Assert.Contains("manual_report", prediction.ReasonCodes);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Anchor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate Anchor.slnx.");
    }
}
