using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace Anchor.Infrastructure.Browser;

public sealed class NativeBridgeServer : IAsyncDisposable
{
    private readonly string _endpointFile;
    private readonly CancellationTokenSource _stop = new();
    private readonly NativeBridgeSnapshotStore _snapshots = new();
    private Task? _listener;

    public NativeBridgeServer(string endpointFile)
    {
        _endpointFile = endpointFile;
        PipeName = $"anchor-{Guid.NewGuid():N}";
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    public string PipeName { get; }
    public string Token { get; }
    public event EventHandler<string>? MessageReceived;

    public void Start()
    {
        if (_listener is not null) return;
        var directory = Path.GetDirectoryName(_endpointFile);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(_endpointFile, JsonSerializer.Serialize(new { pipeName = PipeName, token = Token }));
        _listener = ListenAsync(_stop.Token);
    }

    public void UpdateSnapshot(string type, string json) => _snapshots.Update(type, json);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_listener is not null)
        {
            try { await _listener; } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
        try { File.Delete(_endpointFile); } catch (IOException) { }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken);
            try
            {
                var hello = await NativeMessageFraming.ReadAsync(pipe, cancellationToken);
                using var document = JsonDocument.Parse(hello ?? "{}");
                if (!document.RootElement.TryGetProperty("token", out var supplied)
                    || !CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(supplied.GetString() ?? string.Empty),
                        System.Text.Encoding.UTF8.GetBytes(Token)))
                {
                    continue;
                }
                var initial = _snapshots.GetSnapshot();
                foreach (var snapshot in initial.Messages)
                {
                    await NativeMessageFraming.WriteAsync(pipe, snapshot, cancellationToken);
                }
                using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var receive = ReceiveAsync(pipe, session.Token);
                var send = SendSnapshotsAsync(pipe, initial.Revision, session.Token);
                await Task.WhenAny(receive, send);
                await session.CancelAsync();
                try { await Task.WhenAll(receive, send); } catch (OperationCanceledException) { }
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
            {
                // A broken adapter never affects the desktop host; the next client can reconnect.
            }
        }
    }

    private async Task ReceiveAsync(Stream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await NativeMessageFraming.ReadAsync(pipe, cancellationToken);
            if (message is null) return;
            MessageReceived?.Invoke(this, message);
        }
    }

    private async Task SendSnapshotsAsync(Stream pipe, long revision, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var next = await _snapshots.WaitForChangeAsync(revision, cancellationToken);
            foreach (var message in next.Messages)
            {
                await NativeMessageFraming.WriteAsync(pipe, message, cancellationToken);
            }
            revision = next.Revision;
        }
    }
}
