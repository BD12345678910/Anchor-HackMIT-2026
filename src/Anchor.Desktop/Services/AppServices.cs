using Anchor.Core.Services;
using Anchor.Infrastructure.Persistence;
using Anchor.Infrastructure.Windows;
using Anchor.Infrastructure.Worker;

namespace Anchor_Desktop.Services;

public sealed class AppServices : IAsyncDisposable
{
    private AppServices(
        SqliteEventStore store,
        InferenceEngineAdapter inference,
        WindowsSensorCoordinator sensors,
        OverlayPresenter overlays,
        SessionOrchestrator orchestrator,
        SafetyWatchdog watchdog)
    {
        Store = store;
        Inference = inference;
        Sensors = sensors;
        Overlays = overlays;
        Orchestrator = orchestrator;
        Watchdog = watchdog;
    }

    public SqliteEventStore Store { get; }
    public InferenceEngineAdapter Inference { get; }
    public WindowsSensorCoordinator Sensors { get; }
    public OverlayPresenter Overlays { get; }
    public SessionOrchestrator Orchestrator { get; }
    public SafetyWatchdog Watchdog { get; }

    public static AppServices Create()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Anchor");
        var store = new SqliteEventStore(Path.Combine(appData, "anchor.db"));
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var worker = new InferenceWorkerClient(InferenceWorkerOptions.CreateDefault(repositoryRoot));
        var inference = new InferenceEngineAdapter(worker);
        var sensors = new WindowsSensorCoordinator();
        var overlays = new OverlayPresenter();
        var orchestrator = new SessionOrchestrator(store, inference, sensors, overlays);
        var watchdog = new SafetyWatchdog(overlays, TimeSpan.FromSeconds(5));
        return new AppServices(store, inference, sensors, overlays, orchestrator, watchdog);
    }

    public async ValueTask DisposeAsync()
    {
        Watchdog.Signal(SafetyReleaseReason.Shutdown);
        await Orchestrator.StopAsync();
        Sensors.Dispose();
        await Inference.DisposeAsync();
        await Store.DisposeAsync();
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
