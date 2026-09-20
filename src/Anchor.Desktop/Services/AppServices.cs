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
    public StudyRecordingService Recording { get; }
    public string DataDirectory { get; }
    public ToolPreferencesStore Preferences { get; }

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
        var overlays = new OverlayPresenter(browserBridge);
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
            recording,
            deepSeekHttpClient,
            appData);
    }

    public async Task<TaskSessionPlanner> CreateTaskPlannerAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await DeepSeekSettings.LoadAsync(cancellationToken);
        var environmentKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? string.Empty;
        var key = settings?.Enabled == true ? settings.ApiKey : environmentKey;
        var model = settings?.Model ?? "deepseek-flash";
        var endpoint = settings is null
            ? DeepSeekClient.DefaultEndpoint
            : new Uri(settings.Endpoint, UriKind.Absolute);
        return new TaskSessionPlanner(new DeepSeekClient(
            _deepSeekHttpClient,
            key,
            model,
            endpoint));
    }

    public async ValueTask DisposeAsync()
    {
        Watchdog.Signal(SafetyReleaseReason.Shutdown);
        await Recording.DisposeAsync();
        await Orchestrator.StopAsync();
        Sensors.Dispose();
        await Inference.DisposeAsync();
        await Store.DisposeAsync();
        await BrowserBridge.DisposeAsync();
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
