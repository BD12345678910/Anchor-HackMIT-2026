using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anchor.Infrastructure.Browser;

/// <summary>A page Anchor can edit, as reported by the browser's DevTools endpoint.</summary>
public sealed record BrowserPage(string Id, string Title, string Url, string WebSocketUrl);

/// <summary>What the desktop app wants the live page to look like.</summary>
public sealed record PageEditRequest(
    bool PixelateOffTaskPictures,
    bool DeleteOffTaskBlocks,
    bool SimplifySentences,
    double Threshold,
    int MaxWords,
    IReadOnlyList<string> Keywords)
{
    public static PageEditRequest Off { get; } = new(false, false, false, 0.5, 28, []);
}

/// <summary>The outcome of editing one page.</summary>
public sealed record PageEditResult(string Url, bool Applied, string Detail);

/// <summary>
/// Edits the HTML of pages open in Chrome or Edge directly from Anchor, so the user never installs
/// or configures a browser extension. Anchor talks to the browser's own DevTools endpoint: it lists
/// the open pages, injects the editing engine plus <c>page-agent.js</c>, and calls into it. Every
/// edit is reversible through <see cref="ClearAsync"/>.
/// </summary>
public sealed class ChromeDevToolsBridge : IDisposable
{
    public const int DefaultPort = 9222;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] BrowserExecutables =
    [
        @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
        @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
        @"%LocalAppData%\Google\Chrome\Application\chrome.exe",
        @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
        @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe",
    ];

    private readonly HttpClient _http;
    private readonly int _port;
    private readonly Action<string, Exception> _logError;
    private readonly bool _ownsHttpClient;

    public ChromeDevToolsBridge(
        int port = DefaultPort,
        HttpClient? httpClient = null,
        Action<string, Exception>? logError = null)
    {
        _port = port;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _logError = logError ?? ((_, _) => { });
    }

    /// <summary>The browser profile Anchor launches, kept apart from the user's default profile.</summary>
    public static string ProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Anchor",
        "browser-profile");

    public bool IsConnected { get; private set; }

    public string Status { get; private set; } = "Not connected · press Open focused browser";

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http
                .GetAsync($"http://127.0.0.1:{_port}/json/version", cancellationToken)
                .ConfigureAwait(false);
            IsConnected = response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            IsConnected = false;
        }

        if (!IsConnected)
        {
            Status = "Not connected · press Open focused browser";
        }

        return IsConnected;
    }

    /// <summary>
    /// Starts Chrome (or Edge) with its DevTools endpoint open so Anchor can edit what it shows, and
    /// returns once the endpoint answers. Re-used when a focused browser is already running.
    /// </summary>
    public async Task<bool> LaunchAsync(string startUrl = "about:blank", CancellationToken cancellationToken = default)
    {
        if (await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            Status = "Focused browser already open";
            return true;
        }

        var executable = FindBrowser();
        if (executable is null)
        {
            Status = "Chrome or Edge was not found on this PC";
            return false;
        }

        Directory.CreateDirectory(ProfileDirectory);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add($"--remote-debugging-port={_port}");
        start.ArgumentList.Add($"--user-data-dir={ProfileDirectory}");
        start.ArgumentList.Add("--remote-allow-origins=*");
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--no-default-browser-check");
        start.ArgumentList.Add(startUrl);
        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logError("Could not start the focused browser", exception);
            Status = "Could not start the browser";
            return false;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            if (await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                Status = $"Focused browser open ({Path.GetFileNameWithoutExtension(executable)})";
                return true;
            }
        }

        Status = "The browser started but did not open its DevTools endpoint";
        return false;
    }

    public static string? FindBrowser() => BrowserExecutables
        .Select(Environment.ExpandEnvironmentVariables)
        .FirstOrDefault(File.Exists);

    public async Task<IReadOnlyList<BrowserPage>> ListPagesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = await _http
                .GetStringAsync($"http://127.0.0.1:{_port}/json/list", cancellationToken)
                .ConfigureAwait(false);
            var targets = JsonSerializer.Deserialize<List<DevToolsTarget>>(payload, JsonOptions) ?? [];
            IsConnected = true;
            return
            [
                .. targets
                    .Where(target => string.Equals(target.Type, "page", StringComparison.Ordinal))
                    .Where(target => !string.IsNullOrEmpty(target.WebSocketDebuggerUrl))
                    .Where(target => target.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        || target.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    .Select(target => new BrowserPage(target.Id, target.Title, target.Url, target.WebSocketDebuggerUrl))
            ];
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            IsConnected = false;
            return [];
        }
    }

    /// <summary>Applies the requested edits to every open page and reports what happened per page.</summary>
    public async Task<IReadOnlyList<PageEditResult>> ApplyAsync(
        PageEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expression = $"{Scripts.Bootstrap}AnchorPageAgent.apply({BuildState(request)})";
        return await RunOnAllPagesAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads where the user is in the page in front of them — address, title and how far down they
    /// have read — so context recovery keeps working with nothing installed. Empty when no page is open.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReadPageContextAsync(CancellationToken cancellationToken = default)
    {
        var page = (await ListPagesAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        if (page is null)
        {
            return [];
        }

        try
        {
            var reply = await EvaluateAsync(page, ReadContextExpression, cancellationToken).ConfigureAwait(false);
            var value = ReadEvaluateValue(reply);
            return value is null ? [] : ParseContextMessages(value);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or JsonException or TaskCanceledException)
        {
            _logError($"Could not read {page.Url}", exception);
            return [];
        }
    }

    private const string ReadContextExpression = """
        (() => {
          const doc = document.documentElement;
          const span = Math.max(1, doc.scrollHeight - window.innerHeight);
          return JSON.stringify({
            origin: location.href,
            title: document.title,
            progress: Math.min(1, Math.max(0, window.scrollY / span))
          });
        })()
        """;

    /// <summary>Shapes a page reading into the events <see cref="BrowserContextTracker"/> consumes.</summary>
    public static IReadOnlyList<string> ParseContextMessages(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var origin = root.GetProperty("origin").GetString() ?? string.Empty;
        var title = root.GetProperty("title").GetString() ?? string.Empty;
        var progress = root.GetProperty("progress").GetDouble();
        return
        [
            Message(new { type = "page-context", origin, title }),
            Message(new { type = "reading-progress", progress }),
        ];
    }

    private static string Message(object browserEvent) =>
        JsonSerializer.Serialize(new { source = "anchor-content", @event = browserEvent }, JsonOptions);

    /// <summary>Unwraps a Runtime.evaluate reply, or null when the page returned no string.</summary>
    public static string? ReadEvaluateValue(string reply)
    {
        using var document = JsonDocument.Parse(reply);
        return document.RootElement.TryGetProperty("result", out var outer)
            && outer.TryGetProperty("result", out var inner)
            && inner.TryGetProperty("value", out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>Puts every page back the way the site served it.</summary>
    public async Task<IReadOnlyList<PageEditResult>> ClearAsync(CancellationToken cancellationToken = default) =>
        await RunOnAllPagesAsync($"{Scripts.Bootstrap}AnchorPageAgent.clear()", cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The state handed to the page agent; also the fingerprint of a requested edit.</summary>
    public static string BuildState(PageEditRequest request) => JsonSerializer.Serialize(
        new
        {
            imageBlur = request.PixelateOffTaskPictures,
            clutterRemoval = request.DeleteOffTaskBlocks,
            simplifyText = request.SimplifySentences,
            threshold = request.Threshold,
            maxWords = request.MaxWords,
            keywords = request.Keywords,
        },
        JsonOptions);

    private async Task<IReadOnlyList<PageEditResult>> RunOnAllPagesAsync(
        string expression,
        CancellationToken cancellationToken)
    {
        var pages = await ListPagesAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<PageEditResult>();
        foreach (var page in pages)
        {
            try
            {
                var reply = await EvaluateAsync(page, expression, cancellationToken).ConfigureAwait(false);
                results.Add(new PageEditResult(page.Url, true, reply));
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or JsonException or TaskCanceledException)
            {
                _logError($"Could not edit {page.Url}", exception);
                results.Add(new PageEditResult(page.Url, false, exception.Message));
            }
        }

        Status = results.Count == 0
            ? IsConnected ? "Focused browser open · no editable page" : "Not connected · press Open focused browser"
            : $"Editing {results.Count(result => result.Applied)} of {results.Count} open page(s)";
        return results;
    }

    private async Task<string> EvaluateAsync(BrowserPage page, string expression, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{_port}");
        await socket.ConnectAsync(new Uri(page.WebSocketUrl), cancellationToken).ConfigureAwait(false);
        var message = JsonSerializer.Serialize(
            new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new { expression, returnByValue = true, awaitPromise = true },
            },
            JsonOptions);
        await socket
            .SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
        var reply = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        await socket
            .CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None)
            .ConfigureAwait(false);
        return reply;
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var builder = new StringBuilder();
        while (true)
        {
            var frame = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            builder.Append(Encoding.UTF8.GetString(buffer, 0, frame.Count));
            if (frame.EndOfMessage) return builder.ToString();
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    /// <summary>The injected JavaScript, embedded in the app so nothing has to be installed.</summary>
    internal static class Scripts
    {
        private static readonly Lazy<string> BootstrapSource = new(Build);

        public static string Bootstrap => BootstrapSource.Value;

        private static string Build()
        {
            var engine = Read("Anchor.Infrastructure.Browser.focus-engine.js");
            var agent = Read("Anchor.Infrastructure.Browser.page-agent.js");
            return $$"""
                (() => {
                  if (!window.AnchorFocusEngine) { {{engine}} }
                  if (!window.AnchorPageAgent) { {{agent}} }
                })();

                """;
        }

        private static string Read(string resource)
        {
            using var stream = typeof(ChromeDevToolsBridge).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded script '{resource}' is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    private sealed record DevToolsTarget(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("webSocketDebuggerUrl")] string WebSocketDebuggerUrl);
}
