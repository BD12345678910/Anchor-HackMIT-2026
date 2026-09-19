namespace Anchor.Core.Services;

public enum AppLifecycleAction
{
    None,
    ShowSettings,
    HideSettings,
    ReportDistracted,
    EmergencyRelease,
    Exit
}

public sealed record AppLifecycleTransition(
    AppLifecycleAction Action,
    bool KeepSessionRunning = false);

public sealed class AppLifecycleModel
{
    public bool SettingsVisible { get; private set; } = true;
    public bool ExitRequested { get; private set; }

    public AppLifecycleTransition CloseSettings(bool sessionRunning)
    {
        if (ExitRequested) return new(AppLifecycleAction.Exit);
        SettingsVisible = false;
        return new(AppLifecycleAction.HideSettings, sessionRunning);
    }

    public AppLifecycleTransition ShowSettings()
    {
        if (ExitRequested) return new(AppLifecycleAction.None);
        SettingsVisible = true;
        return new(AppLifecycleAction.ShowSettings);
    }

    public AppLifecycleTransition RequestExit()
    {
        ExitRequested = true;
        return new(AppLifecycleAction.Exit);
    }
}
