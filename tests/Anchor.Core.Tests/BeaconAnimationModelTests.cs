using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class BeaconAnimationModelTests
{
    [Fact]
    public void Shake_is_bounded_and_returns_to_origin_within_700ms()
    {
        var frames = BeaconAnimationModel.CreateShake(reducedMotion: false);

        Assert.NotEmpty(frames);
        Assert.All(frames, frame => Assert.InRange(frame.X, -8, 8));
        Assert.Equal(0, frames[^1].X);
        Assert.InRange(frames[^1].At.TotalMilliseconds, 1, 700);
        Assert.Contains(frames, frame => frame.X < 0);
        Assert.Contains(frames, frame => frame.X > 0);
    }

    [Fact]
    public void Reduced_motion_has_no_translation_frames()
    {
        Assert.Empty(BeaconAnimationModel.CreateShake(reducedMotion: true));
    }
}
