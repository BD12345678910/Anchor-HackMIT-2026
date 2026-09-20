using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class MouseBehaviorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Pointing_at_a_target_travels_far_but_is_not_aimless()
    {
        var analyzer = new MouseBehaviorAnalyzer();

        var behaviors = Enumerable.Range(0, 6)
            .Select(tick => analyzer.Observe(
                Start.AddSeconds(tick),
                pathDistance: 700,
                netDistance: 680,
                directionChanges: 1,
                clickCount: 1,
                keyCount: 0))
            .ToArray();

        Assert.All(behaviors, behavior => Assert.False(behavior.IsAimless));
    }

    [Fact]
    public void Circling_the_cursor_without_going_anywhere_is_aimless()
    {
        var analyzer = new MouseBehaviorAnalyzer();

        var behavior = Enumerable.Range(0, 5)
            .Select(tick => analyzer.Observe(
                Start.AddSeconds(tick),
                pathDistance: 600,
                netDistance: 40,
                directionChanges: 6,
                clickCount: 0,
                keyCount: 0))
            .ToArray()[^1];

        Assert.True(behavior.IsAimless);
        Assert.True(behavior.Wander >= MouseBehaviorAnalyzer.WanderingRatio);
    }

    [Fact]
    public void Repeated_clicking_is_aimless_even_without_much_movement()
    {
        var analyzer = new MouseBehaviorAnalyzer();

        var behavior = Enumerable.Range(0, 4)
            .Select(tick => analyzer.Observe(
                Start.AddSeconds(tick),
                pathDistance: 30,
                netDistance: 10,
                directionChanges: 1,
                clickCount: 4,
                keyCount: 0))
            .ToArray()[^1];

        Assert.True(behavior.IsAimless);
        Assert.True(behavior.ClicksPerSecond >= MouseBehaviorAnalyzer.RestlessClicksPerSecond);
    }

    [Fact]
    public void Typing_alongside_the_pointer_means_the_mouse_work_belongs_to_the_task()
    {
        var analyzer = new MouseBehaviorAnalyzer();

        var behavior = Enumerable.Range(0, 5)
            .Select(tick => analyzer.Observe(
                Start.AddSeconds(tick),
                pathDistance: 600,
                netDistance: 40,
                directionChanges: 6,
                clickCount: 3,
                keyCount: 8))
            .ToArray()[^1];

        Assert.False(behavior.IsAimless);
    }

    [Fact]
    public void One_wandering_second_is_not_enough_to_report_pointer_drift()
    {
        var fusion = new AttentionFusion();

        var result = fusion.Apply(AttentionEvidence.At(
            Start,
            adapterRelevance: 0.9,
            mouseDistance: 700,
            mouseNetDistance: 30,
            mouseDirectionChanges: 8,
            mouseClickCount: 1));

        Assert.False(result.Window.AimlessMouseSustained);
        Assert.DoesNotContain("pointer_wandering", result.Prediction.ReasonCodes);
    }

    [Fact]
    public void Sustained_wandering_reports_pointer_drift_and_stops_counting_as_progress()
    {
        var fusion = new AttentionFusion();

        var results = Enumerable.Range(0, 5)
            .Select(tick => fusion.Apply(AttentionEvidence.At(
                Start.AddSeconds(tick),
                adapterRelevance: 0.9,
                mouseDistance: 700,
                mouseNetDistance: 30,
                mouseDirectionChanges: 8,
                mouseClickCount: 1)))
            .ToArray();

        Assert.False(results[0].Window.AimlessMouseSustained);
        Assert.True(results[^1].Window.AimlessMouseSustained);
        Assert.Contains("pointer_wandering", results[^1].Prediction.ReasonCodes);
    }

    [Fact]
    public void A_motionless_window_carries_no_more_weight_than_a_moving_one()
    {
        var still = AttentionStateMachine.CreateDefault().Update(SensorWindow.Create(
            keyCount: 0,
            mouseDistance: 0,
            idleSeconds: 40,
            appRelevance: 0.7,
            activity: ActivityKind.Reading));
        var moving = AttentionStateMachine.CreateDefault().Update(SensorWindow.Create(
            keyCount: 0,
            mouseDistance: 400,
            idleSeconds: 0,
            appRelevance: 0.7,
            activity: ActivityKind.Reading));

        Assert.Equal(AttentionState.Focused, still.State);
        Assert.Equal(moving.DistractionProbability, still.DistractionProbability, 3);
    }

    [Fact]
    public void Far_more_travel_than_the_work_needs_weighs_more_than_a_little()
    {
        static double Evidence(double distance) => AttentionStateMachine.CreateDefault()
            .Update(SensorWindow.Create(
                keyCount: 0,
                mouseDistance: distance,
                idleSeconds: 0,
                appRelevance: 0.7,
                activity: ActivityKind.Reading))
            .DistractionProbability;

        Assert.Equal(Evidence(300), Evidence(900), 3);
        Assert.True(Evidence(1_400) > Evidence(900));
        Assert.True(Evidence(1_800) - Evidence(1_400) > Evidence(1_400) - Evidence(900));
    }

    [Fact]
    public void Click_mashing_with_no_typing_is_reported()
    {
        var fusion = new AttentionFusion();

        var result = fusion.Apply(AttentionEvidence.At(
            Start,
            adapterRelevance: 0.9,
            mouseDistance: 60,
            mouseNetDistance: 20,
            mouseClickCount: 14));

        Assert.Contains("click_mashing", result.Prediction.ReasonCodes);
    }

    [Fact]
    public void Deliberate_mouse_work_while_coding_is_never_pointer_drift()
    {
        var fusion = new AttentionFusion();

        var results = Enumerable.Range(0, 6)
            .Select(tick => fusion.Apply(AttentionEvidence.At(
                Start.AddSeconds(tick),
                keyCount: 12,
                adapterRelevance: 0.9,
                mouseDistance: 650,
                mouseNetDistance: 40,
                mouseDirectionChanges: 7,
                mouseClickCount: 5,
                activity: ActivityKind.Coding)))
            .ToArray();

        Assert.All(results, result => Assert.False(result.Window.AimlessMouseSustained));
        Assert.All(
            results,
            result => Assert.DoesNotContain("pointer_wandering", result.Prediction.ReasonCodes));
    }

    [Fact]
    public void Pointer_drift_anchors_the_recovery_card_to_the_last_calm_screen()
    {
        var manager = new ContextCapsuleManager(Guid.NewGuid(), "Finish the DP chapter");
        var observation = new ContextObservation(
            Application: "msedge",
            DocumentIdentity: "Dynamic programming — notes",
            Location: "Section 3",
            LastAction: "Reading \u201cstates and transitions\u201d",
            NextAction: "Read on",
            SelectedText: null,
            RestoreTarget: null,
            Confidence: 0.9,
            IsSensitiveField: false,
            CurrentSubtask: "Read section 3",
            Activity: ActivityKind.Reading,
            FocusText: "states and transitions",
            FocusSource: FocusSource.Pointer);

        manager.Observe(observation);
        var capsule = manager.Observe(observation with
        {
            Location = "Section 9",
            FocusText = "unrelated footer",
            IsPointerFidget = true
        });

        Assert.NotNull(capsule);
        Assert.Equal(DistractionReason.PointerFidget, capsule!.Reason);
        Assert.Equal("Section 3", capsule.Location);

        var reminder = LocalContextReminder.Compose(capsule);
        Assert.Contains("pointer", reminder.Headline, StringComparison.OrdinalIgnoreCase);
    }
}
