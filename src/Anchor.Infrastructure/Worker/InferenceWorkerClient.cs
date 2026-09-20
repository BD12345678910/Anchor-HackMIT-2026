using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Anchor.Contracts;
using Anchor.Core.Models;
using Anchor.Core.Services;
using Grpc.Core;
using Grpc.Net.Client;

namespace Anchor.Infrastructure.Worker;

public enum WorkerMode
{
    Stopped,
    Starting,
    Available,
    DeterministicFallback
}

public sealed record InferenceWorkerOptions(
    string PythonExecutable,
    string WorkerDirectory,
    TimeSpan StartupTimeout,
    TimeSpan RpcDeadline,
    int MaxRestarts,
    string? SettingsDirectory = null,
    bool StandaloneExecutable = false)
{
    public static InferenceWorkerOptions CreateDefault(string repositoryRoot)
    {
        // Single-file publish extracts to a temp folder, so siblings live next to the process, not BaseDirectory.
        var installDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var packaged = Path.Combine(installDirectory, "Anchor.VisionWorker.exe");
        return File.Exists(packaged)
            ? new(packaged, installDirectory, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(3), 2, StandaloneExecutable: true)
            : new(
                Path.Combine(repositoryRoot, ".venv", "Scripts", "python.exe"),
                Path.Combine(repositoryRoot, "src", "Anchor.Worker"),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(3),
                2);
    }
}

public sealed class InferenceWorkerClient : IAsyncDisposable
{
    private const uint ProtocolVersion = 3;
    private static readonly TimeSpan CameraProbeDeadline = TimeSpan.FromSeconds(25);
    private readonly InferenceWorkerOptions _options;
    private readonly AttentionStateMachine _fallback = AttentionStateMachine.CreateDefault();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Process? _process;
    private GrpcChannel? _channel;
    private InferenceWorker.InferenceWorkerClient? _client;
    private Metadata? _headers;
    private bool _disposed;

    public InferenceWorkerClient(InferenceWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxRestarts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _options = options;
    }

    public WorkerMode Mode { get; private set; } = WorkerMode.Stopped;
    public string? LastError { get; private set; }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (Mode == WorkerMode.Available)
            {
                return true;
            }

            for (var attempt = 0; attempt <= _options.MaxRestarts; attempt++)
            {
                await StopResourcesAsync();
                Mode = WorkerMode.Starting;
                try
                {
                    await StartAttemptAsync(cancellationToken);
                    Mode = WorkerMode.Available;
                    LastError = null;
                    return true;
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    LastError = error.Message;
                    await StopResourcesAsync();
                    if (attempt < _options.MaxRestarts)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken);
                    }
                }
            }

            Mode = WorkerMode.DeterministicFallback;
            return false;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<AttentionPrediction> PredictAsync(
        SensorWindow window,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(window);

        if (Mode != WorkerMode.Available || _client is null || _headers is null)
        {
            return Fallback(window);
        }

        try
        {
            var request = new PredictAttentionRequest
            {
                SensorWindow = new SensorWindowMessage
                {
                    TimestampUnixMs = window.Timestamp.ToUnixTimeMilliseconds(),
                    KeyCount = checked((uint)window.KeyCount),
                    MouseDistance = window.MouseDistance,
                    IdleSeconds = window.IdleSeconds,
                    AppRelevance = window.AppRelevance,
                    GazePresence = window.GazePresence,
                    AppSwitchCount = checked((uint)window.AppSwitchCount),
                    ScrollReversalCount = checked((uint)window.ScrollReversalCount),
                    GazeAvailable = window.GazeAvailable,
                    ManualReport = window.IsManualReport
                }
            };
            var call = _client.PredictAttentionAsync(
                request,
                _headers,
                DateTime.UtcNow + _options.RpcDeadline,
                cancellationToken);
            var reply = await call.ResponseAsync;
            var state = reply.ReasonCodes.Contains("stuck_phrase", StringComparer.Ordinal)
                ? AttentionState.Stuck
                : reply.DistractionProbability switch
                {
                    >= 0.72 => AttentionState.Distracted,
                    >= 0.45 => AttentionState.Drifting,
                    _ => AttentionState.Focused
                };
            return AttentionPrediction.Create(
                state,
                reply.Confidence,
                reply.DistractionProbability,
                reply.ReasonCodes);
        }
        catch (Exception error) when (error is RpcException or IOException or TimeoutException)
        {
            LastError = error.Message;
            Mode = WorkerMode.DeterministicFallback;
            await StopResourcesAsync();
            return Fallback(window);
        }
    }

    public async Task<IReadOnlyList<CameraDevice>> ListCamerasAsync(
        CancellationToken cancellationToken = default)
    {
        if (_client is null || _headers is null || Mode != WorkerMode.Available)
        {
            throw new InvalidOperationException(LastError is null
                ? "The vision worker is not running."
                : $"The vision worker is not running: {LastError}");
        }

        var probeDeadline = _options.RpcDeadline < CameraProbeDeadline ? CameraProbeDeadline : _options.RpcDeadline;
        var call = _client.ListCamerasAsync(
            new ListCamerasRequest(),
            _headers,
            DateTime.UtcNow + probeDeadline,
            cancellationToken);
        var reply = await call.ResponseAsync;
        if (reply.Unavailable is { } unavailable)
        {
            throw new InvalidOperationException(unavailable.Reason);
        }

        return reply.Devices
            .Select(static item => new CameraDevice(checked((int)item.Index), item.Name))
            .ToArray();
    }

    public async Task<GazeConfigurationResult> ConfigureGazeAsync(
        GazeConfiguration configuration,
        string displaySignature,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (_client is null || _headers is null || Mode != WorkerMode.Available)
        {
            return new GazeConfigurationResult(false, "worker unavailable", null);
        }

        var call = _client.ConfigureGazeAsync(
            new ConfigureGazeRequest
            {
                Configuration = ToMessage(configuration),
                DisplaySignature = displaySignature ?? string.Empty
            },
            _headers,
            DateTime.UtcNow + _options.RpcDeadline,
            cancellationToken);
        var reply = await call.ResponseAsync;
        return new GazeConfigurationResult(
            reply.Accepted,
            reply.Error,
            reply.Accepted ? FromMessage(reply.Configuration) : null);
    }

    public async Task<GazeStatus> StartGazeAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null || _headers is null || Mode != WorkerMode.Available)
        {
            return new GazeStatus(false, "worker unavailable");
        }
        var call = _client.StartGazeAsync(
            new StartGazeRequest(),
            _headers,
            DateTime.UtcNow + _options.RpcDeadline,
            cancellationToken);
        var reply = await call.ResponseAsync;
        return new GazeStatus(reply.Running, reply.Error);
    }

    public async Task<GazeSample> ReadGazeAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null || _headers is null || Mode != WorkerMode.Available)
        {
            return GazeSample.Unavailable("worker unavailable");
        }
        var call = _client.ReadGazeAsync(
            new ReadGazeRequest(),
            _headers,
            DateTime.UtcNow + _options.RpcDeadline,
            cancellationToken);
        var reply = await call.ResponseAsync;
        var point = reply.OutcomeCase == GazeSampleReply.OutcomeOneofCase.Point
            ? reply.Point
            : null;
        var unavailableReason = reply.OutcomeCase == GazeSampleReply.OutcomeOneofCase.Unavailable
            ? reply.Unavailable.Reason
            : string.Empty;
        var timestamp = reply.TimestampUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(reply.TimestampUnixMs)
            : DateTimeOffset.MinValue;
        return new GazeSample(
            point?.X,
            point?.Y,
            reply.Confidence,
            reply.FacePresent,
            timestamp,
            reply.Yaw,
            reply.Pitch,
            reply.Roll,
            reply.PreviewJpeg.ToByteArray(),
            unavailableReason);
    }

    public async Task<CalibrationProgress> AddCalibrationSampleAsync(
        double targetX,
        double targetY,
        CancellationToken cancellationToken = default)
    {
        if (_client is null || _headers is null || Mode != WorkerMode.Available)
        {
            return new CalibrationProgress(false, 0, "worker unavailable");
        }
        var call = _client.AddCalibrationSampleAsync(
            new AddCalibrationSampleRequest { TargetX = targetX, TargetY = targetY },
            _headers,
            DateTime.UtcNow + _options.RpcDeadline,
            cancellationToken);
        var reply = await call.ResponseAsync;
        return new CalibrationProgress(reply.Accepted, checked((int)reply.SampleCount), reply.Error);
    }

    public async Task<CalibrationResult> FinishCalibrationAsync(
        string displaySignature,
        CancellationToken cancellationToken = default)
    {
        if (_client is null || _headers is null || Mode != WorkerMode.Available)
        {
            return new CalibrationResult(false, 0, 0, 0, "worker unavailable");
        }
        var call = _client.FinishCalibrationAsync(
            new FinishCalibrationRequest { DisplaySignature = displaySignature ?? string.Empty },
            _headers,
            DateTime.UtcNow + _options.RpcDeadline,
            cancellationToken);
        var reply = await call.ResponseAsync;
        return new CalibrationResult(
            reply.Accepted,
            checked((int)reply.SampleCount),
            checked((int)reply.InlierCount),
            reply.MedianError,
            reply.Error);
    }

    public async Task<GazeStatus> StopGazeAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null || _headers is null || Mode != WorkerMode.Available)
        {
            return new GazeStatus(false, "worker unavailable");
        }
        var call = _client.StopGazeAsync(
            new StopGazeRequest(),
            _headers,
            DateTime.UtcNow + _options.RpcDeadline,
            cancellationToken);
        var reply = await call.ResponseAsync;
        return new GazeStatus(reply.Running, reply.Error);
    }

    public async Task<(StudyRecordingManifest? Manifest, string? Error)> StartRecordingAsync(
        string outputDirectory,
        TrialMode trialMode,
        string participantCode,
        int displayIndex = 1,
        int fps = 15,
        CancellationToken cancellationToken = default)
    {
        if (_client is null || _headers is null || Mode != WorkerMode.Available)
        {
            return (null, "worker_unavailable");
        }
        var call = _client.StartRecordingAsync(
            new StartRecordingRequest
            {
                OutputDirectory = outputDirectory,
                TrialMode = trialMode == TrialMode.Baseline ? "baseline" : "anchor_enabled",
                ParticipantCode = participantCode,
                DisplayIndex = checked((uint)Math.Max(1, displayIndex)),
                Fps = checked((uint)Math.Clamp(fps, 1, 60))
            },
            _headers,
            DateTime.UtcNow + TimeSpan.FromSeconds(10),
            cancellationToken);
        var reply = await call.ResponseAsync;
        if (!reply.Accepted)
        {
            return (null, reply.Error);
        }
        return (new StudyRecordingManifest(
            reply.TrialId,
            trialMode,
            reply.VideoPath,
            reply.EventsPath,
            reply.SamplesPath,
            reply.SummaryPath,
            reply.ManifestPath), null);
    }

    public Task<StudyRecordingStatus> AppendRecordingEventAsync(
        IReadOnlyDictionary<string, object?> item,
        CancellationToken cancellationToken = default) =>
        RecordingStatusCallAsync(
            (client, headers, deadline, token) => client.AppendRecordingEventAsync(
                new AppendRecordingEventRequest { Json = JsonSerializer.Serialize(item) },
                headers,
                deadline,
                token).ResponseAsync,
            cancellationToken);

    public Task<StudyRecordingStatus> AppendRecordingSampleAsync(
        StudyRecordingSample sample,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["at_ms"] = sample.AtMilliseconds,
            ["gaze_x"] = sample.GazeX,
            ["gaze_y"] = sample.GazeY,
            ["confidence"] = sample.Confidence,
            ["face_present"] = sample.FacePresent,
            ["attention_state"] = sample.AttentionState,
            ["distraction_probability"] = sample.DistractionProbability,
            ["task"] = sample.Task,
            ["subtask"] = sample.Subtask
        });
        return RecordingStatusCallAsync(
            (client, headers, deadline, token) => client.AppendRecordingSampleAsync(
                new AppendRecordingSampleRequest { Json = json },
                headers,
                deadline,
                token).ResponseAsync,
            cancellationToken);
    }

    public Task<StudyRecordingStatus> GetRecordingStatusAsync(
        CancellationToken cancellationToken = default) =>
        RecordingStatusCallAsync(
            (client, headers, deadline, token) => client.GetRecordingStatusAsync(
                new GetRecordingStatusRequest(), headers, deadline, token).ResponseAsync,
            cancellationToken);

    public Task<StudyRecordingStatus> StopRecordingAsync(
        CancellationToken cancellationToken = default) =>
        RecordingStatusCallAsync(
            (client, headers, deadline, token) => client.StopRecordingAsync(
                new StopRecordingRequest(), headers, deadline, token).ResponseAsync,
            cancellationToken,
            TimeSpan.FromSeconds(10));

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null && _headers is not null && Mode == WorkerMode.Available)
            {
                try
                {
                    var call = _client.ShutdownAsync(
                        new ShutdownRequest(),
                        _headers,
                        DateTime.UtcNow + _options.RpcDeadline,
                        cancellationToken);
                    await call.ResponseAsync;
                }
                catch (RpcException)
                {
                    // The fail-open cleanup below still terminates the worker.
                }
            }

            await StopResourcesAsync();
            Mode = WorkerMode.Stopped;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync();
        _disposed = true;
        _lifecycle.Dispose();
    }

    private async Task StartAttemptAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.PythonExecutable))
        {
            throw new FileNotFoundException("Python worker executable was not found.", _options.PythonExecutable);
        }

        if (!Directory.Exists(_options.WorkerDirectory))
        {
            throw new DirectoryNotFoundException($"Worker directory was not found: {_options.WorkerDirectory}");
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PythonExecutable,
            WorkingDirectory = _options.WorkerDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (!_options.StandaloneExecutable)
        {
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("anchor_worker");
        }
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("--token");
        startInfo.ArgumentList.Add(token);
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        if (!_options.StandaloneExecutable)
        {
            startInfo.Environment["PYTHONPATH"] = _options.WorkerDirectory;
        }
        if (!string.IsNullOrWhiteSpace(_options.SettingsDirectory))
        {
            startInfo.Environment["ANCHOR_SETTINGS_DIR"] = _options.SettingsDirectory;
        }

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Worker process could not be started.");

        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(_options.StartupTimeout);
        var line = await _process.StandardOutput.ReadLineAsync(startupTimeout.Token)
            ?? throw new InvalidOperationException(await ReadStartupFailureAsync(_process));
        var readiness = JsonSerializer.Deserialize<WorkerReadiness>(
            line,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Worker readiness message was empty.");
        if (!string.Equals(readiness.Status, "ready", StringComparison.Ordinal)
            || !string.Equals(readiness.Host, "127.0.0.1", StringComparison.Ordinal)
            || readiness.Port is <= 0 or > 65_535
            || readiness.ProtocolVersion != ProtocolVersion)
        {
            throw new InvalidOperationException("Worker readiness message failed validation.");
        }

        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{readiness.Port}");
        _client = new InferenceWorker.InferenceWorkerClient(_channel);
        _headers = new Metadata { { "x-anchor-token", token } };
        var healthCall = _client.HealthAsync(
            new HealthRequest { ProtocolVersion = ProtocolVersion },
            _headers,
            DateTime.UtcNow + _options.RpcDeadline,
            cancellationToken);
        var health = await healthCall.ResponseAsync;
        if (health.ProtocolVersion != ProtocolVersion)
        {
            throw new InvalidOperationException(
                $"Worker protocol {health.ProtocolVersion} is incompatible with host protocol {ProtocolVersion}.");
        }
    }

    private AttentionPrediction Fallback(SensorWindow window)
    {
        var prediction = _fallback.Update(window with { IsWorkerAvailable = false });
        return prediction.ReasonCodes.Contains("worker_unavailable", StringComparer.Ordinal)
            ? prediction
            : prediction with { ReasonCodes = [.. prediction.ReasonCodes, "worker_unavailable"] };
    }

    private async Task<StudyRecordingStatus> RecordingStatusCallAsync(
        Func<InferenceWorker.InferenceWorkerClient, Metadata, DateTime, CancellationToken, Task<RecordingStatusReply>> call,
        CancellationToken cancellationToken,
        TimeSpan? deadline = null)
    {
        if (_client is null || _headers is null || Mode != WorkerMode.Available)
        {
            return new StudyRecordingStatus("", StudyRecordingState.Failed, 0, 0, 0, false, "worker_unavailable");
        }
        var reply = await call(
            _client,
            _headers,
            DateTime.UtcNow + (deadline ?? _options.RpcDeadline),
            cancellationToken);
        return new StudyRecordingStatus(
            reply.TrialId,
            reply.Status switch
            {
                "recording" => StudyRecordingState.Recording,
                "complete" => StudyRecordingState.Complete,
                "failed" => StudyRecordingState.Failed,
                _ => StudyRecordingState.Idle
            },
            checked((int)reply.FrameCount),
            checked((int)reply.DroppedFrames),
            reply.ElapsedSeconds,
            reply.VideoUsable,
            string.IsNullOrWhiteSpace(reply.ErrorCode) ? null : reply.ErrorCode);
    }

    private async Task StopResourcesAsync()
    {
        _channel?.Dispose();
        _channel = null;
        _client = null;
        _headers = null;

        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _process.WaitForExitAsync(wait.Token);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static async Task<string> ReadStartupFailureAsync(Process process)
    {
        var error = await process.StandardError.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(error)
            ? "Worker exited before reporting readiness."
            : $"Worker exited before reporting readiness: {error.Trim()}";
    }

    private static GazeConfigurationMessage ToMessage(GazeConfiguration configuration) => new()
    {
        CameraIndex = checked((uint)configuration.CameraIndex),
        Mirror = configuration.Mirror,
        RotationDegrees = configuration.RotationDegrees,
        OffsetX = configuration.OffsetX,
        OffsetY = configuration.OffsetY,
        Smoothing = configuration.Smoothing,
        Sensitivity = configuration.Sensitivity,
        MinConfidence = configuration.MinimumConfidence
    };

    private static GazeConfiguration FromMessage(GazeConfigurationMessage message) => new(
        checked((int)message.CameraIndex),
        message.Mirror,
        message.RotationDegrees,
        message.OffsetX,
        message.OffsetY,
        message.Smoothing,
        message.Sensitivity,
        message.MinConfidence);

    private sealed record WorkerReadiness(
        string Status,
        string Host,
        int Port,
        uint ProtocolVersion);
}
