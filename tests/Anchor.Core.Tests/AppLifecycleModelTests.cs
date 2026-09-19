using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class AppLifecycleModelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Closing_settings_hides_window_and_keeps_background_services(bool sessionRunning)
    {
        var model = new AppLifecycleModel();

        var transition = model.CloseSettings(sessionRunning);

        Assert.Equal(AppLifecycleAction.HideSettings, transition.Action);
        Assert.False(model.SettingsVisible);
        Assert.False(model.ExitRequested);
        Assert.Equal(sessionRunning, transition.KeepSessionRunning);
    }

    [Fact]
    public void Tray_restore_and_explicit_exit_are_distinct_transitions()
    {
        var model = new AppLifecycleModel();
        model.CloseSettings(sessionRunning: true);

        Assert.Equal(AppLifecycleAction.ShowSettings, model.ShowSettings().Action);
        Assert.True(model.SettingsVisible);
        Assert.Equal(AppLifecycleAction.Exit, model.RequestExit().Action);
        Assert.True(model.ExitRequested);
    }
}
