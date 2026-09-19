using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class TaskSessionPlannerTests
{
    [Fact]
    public async Task Planning_sets_first_subtask_and_source()
    {
        var planner = new TaskSessionPlanner(new FixedIntelligence(ThreeQuestionResult()));

        var result = await planner.PlanAsync("Do 3 USACO questions");

        Assert.Equal("q1", result.Progress.CurrentStep!.Id);
        Assert.Equal(0, result.Progress.CompletedCount);
        Assert.Equal(3, result.Progress.TotalCount);
        Assert.Equal("DeepSeek", result.Source);
        Assert.False(result.IsFallback);
    }

    [Fact]
    public async Task Explicit_completion_updates_current_step_and_progress_label()
    {
        var planner = new TaskSessionPlanner(new FixedIntelligence(ThreeQuestionResult()));
        await planner.PlanAsync("Do 3 USACO questions");

        var result = planner.MarkCurrentComplete(
            CompletionSource.User,
            DateTimeOffset.Parse("2026-09-20T02:00:00Z"));

        Assert.Equal("q2", result.Progress.CurrentStep!.Id);
        Assert.Equal("1 of 3", result.ProgressLabel);
    }

    [Fact]
    public async Task Fallback_source_remains_visible_to_user()
    {
        var fallback = new TaskPlanningResult(
            new TaskPlan("Read paper", [new TaskStep("step-1", "Read paper", "User confirms completion")]),
            true,
            "Local fallback",
            "deepseek_not_configured");
        var planner = new TaskSessionPlanner(new FixedIntelligence(fallback));

        var result = await planner.PlanAsync("Read paper");

        Assert.True(result.IsFallback);
        Assert.Equal("Local fallback", result.Source);
        Assert.Equal("deepseek_not_configured", result.ErrorCode);
    }

    private static TaskPlanningResult ThreeQuestionResult() =>
        new(
            new TaskPlan("Do 3 USACO questions", [
                new TaskStep("q1", "Solve problem 1", "Accepted submission"),
                new TaskStep("q2", "Solve problem 2", "Accepted submission"),
                new TaskStep("q3", "Solve problem 3", "Accepted submission")
            ]),
            false,
            "DeepSeek",
            null);

    private sealed class FixedIntelligence(TaskPlanningResult result) : ITaskIntelligence
    {
        public TaskIntelligenceAvailability Availability { get; } = new(true, true, "test");

        public Task<TaskPlanningResult> PlanTaskAsync(string goal, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);

        public Task<RelevanceJudgment> JudgeRelevanceAsync(
            TaskContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TaskStep> BreakDownStepAsync(
            TaskContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
