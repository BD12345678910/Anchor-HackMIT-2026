using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Anchor.Infrastructure.Browser;

var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var endpointPath = Path.Combine(appData, "Anchor", "bridge.json");
if (!File.Exists(endpointPath)) return 2;
var endpoint = JsonSerializer.Deserialize<BridgeEndpoint>(await File.ReadAllTextAsync(endpointPath));
if (endpoint is null || string.IsNullOrWhiteSpace(endpoint.PipeName) || string.IsNullOrWhiteSpace(endpoint.Token)) return 3;

await using var pipe = new NamedPipeClientStream(
    ".",
    endpoint.PipeName,
    PipeDirection.InOut,
    PipeOptions.Asynchronous,
    TokenImpersonationLevel.Identification);
await pipe.ConnectAsync(3000);
await NativeMessageFraming.WriteAsync(pipe, JsonSerializer.Serialize(new
{
    type = "hello",
    token = endpoint.Token,
    protocolVersion = 1
}));

var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
using var stop = new CancellationTokenSource();
var incoming = PumpAsync(pipe, output, stop.Token);
var outgoing = PumpAsync(input, pipe, stop.Token);
await Task.WhenAny(incoming, outgoing);
stop.Cancel();
return 0;

static async Task PumpAsync(Stream source, Stream destination, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var message = await NativeMessageFraming.ReadAsync(source, cancellationToken);
        if (message is null) return;
        await NativeMessageFraming.WriteAsync(destination, message, cancellationToken);
    }
}

file sealed record BridgeEndpoint(string PipeName, string Token);
