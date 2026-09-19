using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class InterventionPolicyTests
{
    [Fact]
    public void Manual_report_requests_recovery_even_when_confidence_is_low()
    {
        var policy = new InterventionPolicy();
        var prediction = AttentionPrediction.Create(
            AttentionState.Distracted,
            0.1,
            0.1,
            ["manual_report"]);

        var decision = policy.Decide(prediction, UserPreferences.Default);

        Assert.Equal(InterventionKind.RecoveryCard, decision.Kind);
    }

    [Fact]
    public void Low_confidence_prediction_does_not_interrupt()
    {
        var policy = new InterventionPolicy();
        var prediction = AttentionPrediction.Create(
            AttentionState.Drifting,
            0.3,
            0.7,
            ["rapid_switching"]);

        var decision = policy.Decide(prediction, UserPreferences.Default);

        Assert.Equal(InterventionKind.None, decision.Kind);
    }

    [Fact]
    public void Secure_window_never_receives_an_intervention()
    {
        var policy = new InterventionPolicy();
        var prediction = AttentionPrediction.Create(
            AttentionState.Distracted,
            1,
            1,
            ["secure_window"]);

        var decision = policy.Decide(prediction, UserPreferences.Default);

        Assert.Equal(InterventionKind.None, decision.Kind);
    }

    [Fact]
    public void Cooldown_suppresses_repeated_interventions()
    {
        var now = DateTimeOffset.UnixEpoch;
        var policy = new InterventionPolicy(() => now);
        var prediction = AttentionPrediction.Create(
            AttentionState.Drifting,
            0.9,
            0.8,
            ["rapid_switching"]);

        var first = policy.Decide(prediction, UserPreferences.Default);
        var second = policy.Decide(prediction, UserPreferences.Default);
        now += TimeSpan.FromMinutes(3);
        var third = policy.Decide(prediction, UserPreferences.Default);

        Assert.Equal(InterventionKind.BeaconPulse, first.Kind);
        Assert.Equal(InterventionKind.None, second.Kind);
        Assert.Equal(InterventionKind.BeaconPulse, third.Kind);
    }

    [Fact]
    public void Repeated_dismissals_raise_the_intervention_threshold()
    {
        var policy = new InterventionPolicy();
        var original = policy.CurrentThreshold;

        policy.RecordResponse(InterventionResponse.Dismissed);
        policy.RecordResponse(InterventionResponse.Dismissed);

        Assert.True(policy.CurrentThreshold > original);
    }
}
