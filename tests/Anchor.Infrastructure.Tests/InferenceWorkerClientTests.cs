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

    [Fact]
    public async Task Real_worker_maps_stuck_phrase_reason_to_stuck_state()
    {
        var root = FindRepositoryRoot();
        var options = new InferenceWorkerOptions(
            PythonExecutable: Path.Combine(root, ".venv", "Scripts", "python.exe"),
            WorkerDirectory: Path.Combine(root, "src", "Anchor.Worker"),
            StartupTimeout: TimeSpan.FromSeconds(5),
            RpcDeadline: TimeSpan.FromSeconds(2),
            MaxRestarts: 1);
        await using var client = new InferenceWorkerClient(options);

        Assert.True(await client.StartAsync(), client.LastError);
        var window = SensorWindow.Create(
            keyCount: 0,
            mouseDistance: 30,
            idleSeconds: 2,
            appRelevance: 0.9,
            scrollReversalCount: 9,
            isWorkerAvailable: true);
        var first = await client.PredictAsync(window);
        var prediction = await client.PredictAsync(window);

        Assert.DoesNotContain("stuck_phrase", first.ReasonCodes);
        Assert.Equal(AttentionState.Stuck, prediction.State);
        Assert.Contains("stuck_phrase", prediction.ReasonCodes);
    }

    [Fact]
    public async Task Real_worker_round_trips_gaze_configuration_and_never_fakes_missing_coordinates()
    {
        var root = FindRepositoryRoot();
        var settingsDirectory = Path.Combine(
            Path.GetTempPath(),
            $"anchor-gaze-{Guid.NewGuid():N}");
        var options = new InferenceWorkerOptions(
            PythonExecutable: Path.Combine(root, ".venv", "Scripts", "python.exe"),
            WorkerDirectory: Path.Combine(root, "src", "Anchor.Worker"),
            StartupTimeout: TimeSpan.FromSeconds(5),
            RpcDeadline: TimeSpan.FromSeconds(2),
            MaxRestarts: 1,
            SettingsDirectory: settingsDirectory);
        await using var client = new InferenceWorkerClient(options);

        Assert.True(await client.StartAsync(), client.LastError);
        var configured = await client.ConfigureGazeAsync(new GazeConfiguration(
            CameraIndex: 2,
            Mirror: false,
            RotationDegrees: 270,
            OffsetX: 0.12,
            OffsetY: -0.08,
            Smoothing: 0.4,
            Sensitivity: 1.35,
            MinimumConfidence: 0.72), "1920x1080@100");
        var sample = await client.ReadGazeAsync();

        Assert.True(configured.Accepted, configured.Error);
        Assert.Equal(2, configured.Configuration?.CameraIndex);
        Assert.Equal(270, configured.Configuration?.RotationDegrees);
        Assert.False(sample.Available);
        Assert.Null(sample.X);
        Assert.Null(sample.Y);
        Assert.False(sample.FacePresent);
    }

    [Fact]
    public async Task Real_worker_records_screen_sample_and_event_then_finalizes_video()
    {
        var root = FindRepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"anchor-recording-{Guid.NewGuid():N}");
        var options = new InferenceWorkerOptions(
            PythonExecutable: Path.Combine(root, ".venv", "Scripts", "python.exe"),
            WorkerDirectory: Path.Combine(root, "src", "Anchor.Worker"),
            StartupTimeout: TimeSpan.FromSeconds(5),
            RpcDeadline: TimeSpan.FromSeconds(3),
            MaxRestarts: 1);
        await using var client = new InferenceWorkerClient(options);

        Assert.True(await client.StartAsync(), client.LastError);
        var started = await client.StartRecordingAsync(output, TrialMode.AnchorEnabled, "TEST01", fps: 10);
        var duplicate = await client.StartRecordingAsync(output, TrialMode.AnchorEnabled, "TEST01", fps: 10);
        Assert.NotNull(started.Manifest);
        Assert.Equal("recording_already_active", duplicate.Error);
        await client.AppendRecordingSampleAsync(new StudyRecordingSample(
            50, 0.5, 0.5, 0.9, true, "focused", 0.1, 0.8, "Test task", "Test step"));
        await client.AppendRecordingEventAsync(new Dictionary<string, object?>
        {
            ["type"] = "intervention",
            ["at_ms"] = 100,
            ["presented"] = true
        });
        await Task.Delay(350);
        var stopped = await client.StopRecordingAsync();

        Assert.Equal(StudyRecordingState.Complete, stopped.State);
        Assert.True(stopped.VideoUsable);
        Assert.True(File.Exists(started.Manifest!.VideoPath));
        Assert.True(File.Exists(started.Manifest.SummaryPath));
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
