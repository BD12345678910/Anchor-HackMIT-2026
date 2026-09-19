namespace Anchor.Core.Models;

public enum ToolkitFeature
{
    GazeSpotlight,
    PeripheralDim,
    WindowFirewall,
    IntentionGate,
    ContextReminder,
    PointerGuard
}

public sealed record ToolkitState(
    bool GazeSpotlight,
    bool PeripheralDim,
    bool WindowFirewall,
    bool PointerGuard,
    bool SecureWindow = false,
    bool BrowserImageBlur = true,
    bool BrowserFutureTextMask = true,
    bool BrowserAnimationSuppression = false)
{
    public static ToolkitState Off { get; } = new(false, false, false, false, BrowserImageBlur: false, BrowserFutureTextMask: false);
}

public sealed record ToolkitApplyResult(
    ToolkitFeature Feature,
    string Status,
    bool IsVisible);
