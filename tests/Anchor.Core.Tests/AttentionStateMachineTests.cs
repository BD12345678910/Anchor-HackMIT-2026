using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class AttentionStateMachineTests
{
    [Fact]
    public void Sustained_irrelevant_activity_enters_distracted_but_one_noisy_window_does_not()
    {
        var machine = AttentionStateMachine.CreateDefault();

        Assert.Equal(AttentionState.Focused, machine.Update(FocusedWindow()).State);
        Assert.NotEqual(AttentionState.Distracted, machine.Update(IrrelevantWindow()).State);
        Assert.NotEqual(AttentionState.Distracted, machine.Update(IrrelevantWindow()).State);
        Assert.Equal(AttentionState.Distracted, machine.Update(IrrelevantWindow()).State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("阅读量子物理章节")]
    [InlineData("write parser tests")]
    public void Relevance_score_is_bounded_for_any_title(string title)
    {
        var score = TaskRelevanceScorer.Score(title, "browser", "example");

        Assert.InRange(score, 0, 1);
    }

    [Fact]
    public void Manual_report_immediately_enters_distracted_state()
    {
        var machine = AttentionStateMachine.CreateDefault();

        var prediction = machine.Update(SensorWindow.Create(
            keyCount: 0,
            mouseDistance: 0,
            idleSeconds: 0,
            appRelevance: 1,
            isManualReport: true));

        Assert.Equal(AttentionState.Distracted, prediction.State);
        Assert.Contains("manual_report", prediction.ReasonCodes);
    }

    [Fact]
    public void Secure_window_suppresses_attention_classification()
    {
        var machine = AttentionStateMachine.CreateDefault();

        var prediction = machine.Update(SensorWindow.Create(
            keyCount: 0,
            mouseDistance: 0,
            idleSeconds: 15,
            appRelevance: 0,
            isSecureWindow: true));

        Assert.Equal(AttentionState.Unknown, prediction.State);
        Assert.Contains("secure_window", prediction.ReasonCodes);
    }

    [Fact]
    public void Missing_worker_keeps_deterministic_classification_available()
    {
        var machine = AttentionStateMachine.CreateDefault();

        var prediction = machine.Update(FocusedWindow() with { IsWorkerAvailable = false });

        Assert.Equal(AttentionState.Focused, prediction.State);
        Assert.Contains("worker_unavailable", prediction.ReasonCodes);
    }

    [Fact]
    public void Repeated_scroll_loops_on_relevant_text_enter_stuck_state()
    {
        var machine = AttentionStateMachine.CreateDefault();
        var loop = SensorWindow.Create(
            keyCount: 0,
            mouseDistance: 30,
            idleSeconds: 2,
            appRelevance: 0.9,
            scrollReversalCount: 9,
            isWorkerAvailable: false);

        Assert.Equal(AttentionState.Drifting, machine.Update(loop).State);
        var prediction = machine.Update(loop);

        Assert.Equal(AttentionState.Stuck, prediction.State);
        Assert.Contains("stuck_phrase", prediction.ReasonCodes);
    }

    private static SensorWindow FocusedWindow() => SensorWindow.Create(
        keyCount: 8,
        mouseDistance: 30,
        idleSeconds: 0,
        appRelevance: 0.95,
        gazePresence: 0.9,
        isWorkerAvailable: true);

    private static SensorWindow IrrelevantWindow() => SensorWindow.Create(
        keyCount: 0,
        mouseDistance: 250,
        idleSeconds: 8,
        appRelevance: 0.05,
        gazePresence: 0.2,
        appSwitchCount: 4,
        scrollReversalCount: 5,
        isWorkerAvailable: true);
}
