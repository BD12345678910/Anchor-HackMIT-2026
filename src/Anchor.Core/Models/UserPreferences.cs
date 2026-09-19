namespace Anchor.Core.Models;

public sealed record UserPreferences(
    bool InterventionsEnabled,
    bool ReducedMotion,
    double MinimumConfidence,
    TimeSpan InterventionCooldown)
{
    public static UserPreferences Default { get; } = new(
        InterventionsEnabled: true,
        ReducedMotion: false,
        MinimumConfidence: 0.65,
        InterventionCooldown: TimeSpan.FromMinutes(2));
}

public enum InterventionResponse
{
    Dismissed,
    ReturnedToTask,
    Snoozed,
    Disabled,
    NeededForTask,
    DeliberateBreak
}
