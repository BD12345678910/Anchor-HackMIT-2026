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
    int MaxRestarts)
{
    public static InferenceWorkerOptions CreateDefault(string repositoryRoot) => new(
        Path.Combine(repositoryRoot, ".venv", "Scripts", "python.exe"),
        Path.Combine(repositoryRoot, "src", "Anchor.Worker"),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(2),
        2);
}

public sealed class InferenceWorkerClient : IAsyncDisposable
{
    private const uint ProtocolVersion = 1;
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
                    GazeAvailable = window.IsWorkerAvailable,
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
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("anchor_worker");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("--token");
        startInfo.ArgumentList.Add(token);
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PYTHONPATH"] = _options.WorkerDirectory;

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

    private sealed record WorkerReadiness(
        string Status,
        string Host,
        int Port,
        uint ProtocolVersion);
}
