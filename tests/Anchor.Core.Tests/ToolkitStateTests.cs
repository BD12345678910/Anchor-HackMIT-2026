using Anchor.Core.Models;

namespace Anchor.Core.Tests;

public sealed class ToolkitStateTests
{
    [Fact]
    public void Settings_mode_projects_desktop_preferences_to_inactive_state()
    {
        var state = ToolkitState.FromPreferences(
            sessionActive: false,
            gazeSpotlight: true,
            peripheralDim: true,
            windowFirewall: true,
            pointerGuard: true,
            secureWindow: false,
            browserImageBlur: true,
            browserFutureTextMask: true,
            browserAnimationSuppression: true);

        Assert.False(state.GazeSpotlight);
        Assert.False(state.PeripheralDim);
        Assert.False(state.WindowFirewall);
        Assert.False(state.PointerGuard);
        Assert.True(state.BrowserImageBlur);
        Assert.True(state.BrowserFutureTextMask);
        Assert.True(state.BrowserAnimationSuppression);
    }

    [Fact]
    public void Active_session_preserves_desktop_tool_preferences()
    {
        var state = ToolkitState.FromPreferences(
            sessionActive: true,
            gazeSpotlight: true,
            peripheralDim: true,
            windowFirewall: false,
            pointerGuard: true,
            secureWindow: false,
            browserImageBlur: true,
            browserFutureTextMask: false,
            browserAnimationSuppression: true);

        Assert.True(state.GazeSpotlight);
        Assert.True(state.PeripheralDim);
        Assert.False(state.WindowFirewall);
        Assert.True(state.PointerGuard);
        Assert.True(state.BrowserImageBlur);
        Assert.False(state.BrowserFutureTextMask);
        Assert.True(state.BrowserAnimationSuppression);
    }
}
