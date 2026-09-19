using Anchor.Core.Models;

namespace Anchor.Core.Tests;

public sealed class ModelValidationTests
{
    [Fact]
    public void Goal_session_trims_title()
    {
        var session = GoalSession.Create("  Read paper  ", DateTimeOffset.UnixEpoch);

        Assert.Equal("Read paper", session.Title);
        Assert.Equal(DateTimeOffset.UnixEpoch, session.StartedAt);
        Assert.NotEqual(Guid.Empty, session.Id);
    }

    [Fact]
    public void Goal_session_rejects_blank_title()
    {
        Assert.Throws<ArgumentException>(() =>
            GoalSession.Create(" \t ", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Goal_session_limits_title_to_240_unicode_scalars()
    {
        var title = string.Concat(Enumerable.Repeat("🧠", 250));

        var session = GoalSession.Create(title, DateTimeOffset.UnixEpoch);

        Assert.Equal(240, session.Title.EnumerateRunes().Count());
    }

    [Fact]
    public void Sensor_window_rejects_negative_counts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SensorWindow.Create(keyCount: -1, mouseDistance: 0, idleSeconds: 0));
    }

    [Fact]
    public void Sensor_window_clamps_optional_scores()
    {
        var window = SensorWindow.Create(
            keyCount: 2,
            mouseDistance: 8,
            idleSeconds: 1,
            appRelevance: 1.7,
            gazePresence: -0.5);

        Assert.Equal(1, window.AppRelevance);
        Assert.Equal(0, window.GazePresence);
    }

    [Fact]
    public void Attention_prediction_clamps_probabilities()
    {
        var prediction = AttentionPrediction.Create(
            AttentionState.Drifting,
            confidence: 2,
            distractionProbability: -1,
            ["rapid_switching"]);

        Assert.Equal(1, prediction.Confidence);
        Assert.Equal(0, prediction.DistractionProbability);
        Assert.Equal(["rapid_switching"], prediction.ReasonCodes);
    }
}
