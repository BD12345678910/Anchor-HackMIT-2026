using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class TaskPlanManagerTests
{
    private static readonly DateTimeOffset CompletedAt =
        DateTimeOffset.Parse("2026-09-20T02:00:00Z");

    [Fact]
    public void Start_rejects_empty_plan()
    {
        var manager = new TaskPlanManager();

        Assert.Throws<ArgumentException>(() =>
            manager.Start(new TaskPlan("Solve USACO", [])));
    }

    [Fact]
    public void Start_rejects_more_than_eight_steps()
    {
        var manager = new TaskPlanManager();
        var steps = Enumerable.Range(1, 9)
            .Select(index => new TaskStep($"q{index}", $"Question {index}", "Accepted submission"))
            .ToArray();

        Assert.Throws<ArgumentException>(() =>
            manager.Start(new TaskPlan("Solve USACO", steps)));
    }

    [Fact]
    public void Start_rejects_duplicate_step_identifiers()
    {
        var manager = new TaskPlanManager();
        var plan = new TaskPlan("Solve USACO", [
            new TaskStep("q1", "Question 1", "Accepted submission"),
            new TaskStep("q1", "Question 2", "Accepted submission")
        ]);

        Assert.Throws<ArgumentException>(() => manager.Start(plan));
    }

    [Theory]
    [InlineData("", "Question", "Accepted submission")]
    [InlineData("q1", " ", "Accepted submission")]
    [InlineData("q1", "Question", "\t")]
    public void Start_rejects_blank_step_fields(string id, string title, string criterion)
    {
        var manager = new TaskPlanManager();

        Assert.Throws<ArgumentException>(() =>
            manager.Start(new TaskPlan("Solve USACO", [new TaskStep(id, title, criterion)])));
    }

    [Fact]
    public void Start_accepts_one_step_local_fallback()
    {
        var manager = new TaskPlanManager();

        var result = manager.Start(new TaskPlan("Read chapter", [
            new TaskStep("step-1", "Read chapter", "Reach the end")
        ]));

        Assert.Equal("step-1", result.CurrentStep!.Id);
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(0, result.CompletedCount);
    }

    [Fact]
    public void MarkCurrentComplete_advances_exactly_one_step()
    {
        var manager = StartedThreeQuestionManager();

        var result = manager.MarkCurrentComplete(CompletionSource.User, CompletedAt);

        Assert.Equal("q2", result.CurrentStep!.Id);
        Assert.Equal(1, result.CompletedCount);
        var completion = Assert.Single(result.Completions);
        Assert.Equal("q1", completion.StepId);
        Assert.Equal(CompletionSource.User, completion.Source);
        Assert.Equal(CompletedAt, completion.CompletedAt);
    }

    [Fact]
    public void SuggestCompletion_does_not_advance_until_confirmed()
    {
        var manager = StartedThreeQuestionManager();

        var suggested = manager.SuggestCompletion("Accepted result detected");

        Assert.Equal("q1", suggested.CurrentStep!.Id);
        Assert.Equal(0, suggested.CompletedCount);
        Assert.Equal("Accepted result detected", suggested.PendingSuggestion);

        var confirmed = manager.ConfirmSuggestedCompletion(CompletedAt);

        Assert.Equal("q2", confirmed.CurrentStep!.Id);
        Assert.Equal(1, confirmed.CompletedCount);
        Assert.Null(confirmed.PendingSuggestion);
        Assert.Equal(CompletionSource.LlmSuggestionConfirmed, Assert.Single(confirmed.Completions).Source);
    }

    [Fact]
    public void ReplacePlan_resets_progress_and_normalizes_text()
    {
        var manager = StartedThreeQuestionManager();
        manager.MarkCurrentComplete(CompletionSource.Adapter, CompletedAt);

        var result = manager.ReplacePlan(new TaskPlan("  Read paper  ", [
            new TaskStep("  first  ", "  Read abstract  ", "  Abstract reviewed  ")
        ]));

        Assert.Equal("Read paper", result.Goal);
        Assert.Equal("first", result.CurrentStep!.Id);
        Assert.Equal("Read abstract", result.CurrentStep.Title);
        Assert.Equal("Abstract reviewed", result.CurrentStep.CompletionCriterion);
        Assert.Equal(0, result.CompletedCount);
        Assert.Empty(result.Completions);
    }

    [Fact]
    public void GoalSession_can_attach_a_validated_plan()
    {
        var session = GoalSession.Create("Solve USACO", DateTimeOffset.UnixEpoch);
        var plan = ThreeQuestionPlan();

        var withPlan = session.WithPlan(plan);

        Assert.Same(plan, withPlan.Plan);
        Assert.Equal(session.Id, withPlan.Id);
    }

    private static TaskPlanManager StartedThreeQuestionManager()
    {
        var manager = new TaskPlanManager();
        manager.Start(ThreeQuestionPlan());
        return manager;
    }

    private static TaskPlan ThreeQuestionPlan() =>
        new("Do 3 USACO questions", [
            new TaskStep("q1", "Finish question 1", "Accepted submission"),
            new TaskStep("q2", "Finish question 2", "Accepted submission"),
            new TaskStep("q3", "Finish question 3", "Accepted submission")
        ]);
}
