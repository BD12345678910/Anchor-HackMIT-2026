using Anchor.Core.Services;
using Anchor.Infrastructure.Persistence;
using Anchor.Infrastructure.DeepSeek;
using Anchor.Infrastructure.Browser;
using Anchor.Infrastructure.Windows;
using Anchor.Infrastructure.Worker;

namespace Anchor_Desktop.Services;

public sealed class AppServices : IAsyncDisposable
{
    private readonly HttpClient _deepSeekHttpClient;

    private AppServices(
        SqliteEventStore store,
        InferenceEngineAdapter inference,
        WindowsSensorCoordinator sensors,
        OverlayPresenter overlays,
        SessionOrchestrator orchestrator,
        SafetyWatchdog watchdog,
        DeepSeekSettingsStore deepSeekSettings,
        NativeBridgeServer browserBridge,
        ChromeDevToolsBridge pageBridge,
        BrowserContextTracker browserContext,
        StudyRecordingService recording,
        HttpClient deepSeekHttpClient,
        string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Preferences = new ToolPreferencesStore(Path.Combine(dataDirectory, "preferences.json"));
        Store = store;
        Inference = inference;
        Sensors = sensors;
        Overlays = overlays;
        Orchestrator = orchestrator;
        Watchdog = watchdog;
        DeepSeekSettings = deepSeekSettings;
        BrowserBridge = browserBridge;
        PageBridge = pageBridge;
        BrowserContext = browserContext;
        Recording = recording;
        _deepSeekHttpClient = deepSeekHttpClient;
    }

    public SqliteEventStore Store { get; }
    public InferenceEngineAdapter Inference { get; }
    public WindowsSensorCoordinator Sensors { get; }
    public OverlayPresenter Overlays { get; }
    public SessionOrchestrator Orchestrator { get; }
    public SafetyWatchdog Watchdog { get; }
    public DeepSeekSettingsStore DeepSeekSettings { get; }
    public NativeBridgeServer BrowserBridge { get; }

    /// <summary>Edits live pages in Chrome or Edge directly, with nothing for the user to install.</summary>
    public ChromeDevToolsBridge PageBridge { get; }

    /// <summary>Where the user is in the page in front of them, for context recovery.</summary>
    public BrowserContextTracker BrowserContext { get; }
    public StudyRecordingService Recording { get; }
    public string DataDirectory { get; }
    public ToolPreferencesStore Preferences { get; }
    public ScreenTextReader ScreenReader { get; } = new();

    /// <summary>Appends a line to attention.log; only active when ANCHOR_TRACE is set.</summary>
    public void Trace(string message)
    {
        if (!TraceEnabled)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(
                Path.Combine(DataDirectory, "attention.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static readonly bool TraceEnabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANCHOR_TRACE"));

    public void LogError(string context, Exception error)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(
                Path.Combine(DataDirectory, "errors.log"),
                $"{DateTimeOffset.Now:O} [{context}] {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static AppServices Create()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Anchor");
        var store = new SqliteEventStore(Path.Combine(appData, "anchor.db"));
        var repositoryRoot = FindRepositoryRoot(
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory);
        var worker = new InferenceWorkerClient(InferenceWorkerOptions.CreateDefault(repositoryRoot));
        var inference = new InferenceEngineAdapter(worker);
        var browserContext = new BrowserContextTracker();
        var sensors = new WindowsSensorCoordinator(browserContext);
        var browserBridge = new NativeBridgeServer(Path.Combine(appData, "bridge.json"));
        browserBridge.MessageReceived += (_, message) => browserContext.Apply(message);
        browserBridge.Start();
        var pageBridge = new ChromeDevToolsBridge(
            logError: (context, error) => App.Services?.LogError(context, error));
        var overlays = new OverlayPresenter(browserBridge, (context, error) => App.Services?.LogError(context, error));
        var orchestrator = new SessionOrchestrator(store, inference, sensors, overlays);
        var recording = new StudyRecordingService(inference, orchestrator);
        orchestrator.InterventionPresented += recording.RecordIntervention;
        var watchdog = new SafetyWatchdog(overlays, TimeSpan.FromSeconds(30));
        var deepSeekSettings = new DeepSeekSettingsStore(Path.Combine(appData, "settings.json"));
        var deepSeekHttpClient = new HttpClient();
        return new AppServices(
            store,
            inference,
            sensors,
            overlays,
            orchestrator,
            watchdog,
            deepSeekSettings,
            browserBridge,
            pageBridge,
            browserContext,
            recording,
            deepSeekHttpClient,
            appData);
    }

    public async Task<TaskSessionPlanner> CreateTaskPlannerAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await DeepSeekSettings.LoadAsync(cancellationToken);
        var key = ResolveDeepSeekKey(settings);
        var model = string.IsNullOrWhiteSpace(settings?.Model) ? "deepseek-flash" : settings!.Model;
        var endpoint = settings is null
            ? DeepSeekClient.DefaultEndpoint
            : new Uri(settings.Endpoint, UriKind.Absolute);
        return new TaskSessionPlanner(new DeepSeekClient(
            _deepSeekHttpClient,
            key,
            model,
            endpoint));
    }

    /// <summary>
    /// DeepSeek is always the intelligence path whenever a key is present: the saved key wins,
    /// otherwise the <c>DEEPSEEK_API_KEY</c> environment variable. Local rules are only a fallback
    /// for a missing key or an unreachable API, never a mode the user picks.
    /// </summary>
    public static string ResolveDeepSeekKey(DeepSeekSettings? settings)
    {
        var environmentKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? string.Empty;
        return string.IsNullOrWhiteSpace(settings?.ApiKey) ? environmentKey.Trim() : settings.ApiKey.Trim();
    }

    public async ValueTask DisposeAsync()
    {
        Watchdog.Signal(SafetyReleaseReason.Shutdown);
        await Recording.DisposeAsync();
        await Orchestrator.StopAsync();
        Sensors.Dispose();
        Overlays.ImageBlur.Dispose();
        await Inference.DisposeAsync();
        await Store.DisposeAsync();
        await BrowserBridge.DisposeAsync();
        PageBridge.Dispose();
        _deepSeekHttpClient.Dispose();
    }

    private static string FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Anchor.slnx")))
            {
                return directory.FullName;
            }
        }

        return AppContext.BaseDirectory;
    }
}
