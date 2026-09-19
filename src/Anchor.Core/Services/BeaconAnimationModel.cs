namespace Anchor.Core.Services;

public readonly record struct BeaconAnimationFrame(TimeSpan At, double X);

public static class BeaconAnimationModel
{
    private static readonly BeaconAnimationFrame[] ShakeFrames =
    [
        new(TimeSpan.Zero, 0),
        new(TimeSpan.FromMilliseconds(70), -8),
        new(TimeSpan.FromMilliseconds(150), 8),
        new(TimeSpan.FromMilliseconds(230), -7),
        new(TimeSpan.FromMilliseconds(310), 7),
        new(TimeSpan.FromMilliseconds(390), -5),
        new(TimeSpan.FromMilliseconds(470), 5),
        new(TimeSpan.FromMilliseconds(550), -2),
        new(TimeSpan.FromMilliseconds(630), 0)
    ];

    public static IReadOnlyList<BeaconAnimationFrame> CreateShake(bool reducedMotion) =>
        reducedMotion ? [] : ShakeFrames;
}
