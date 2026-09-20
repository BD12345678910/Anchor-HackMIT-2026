using Anchor.Core.Models;

namespace Anchor.Core.Services;

public sealed record TaskSessionPlanState(
    TaskProgressSnapshot Progress,
    string Source,
    bool IsFallback,
    string? ErrorCode)
{
    public string ProgressLabel => $"{Progress.CompletedCount} of {Progress.TotalCount}";
}

public sealed class TaskSessionPlanner
{
    private readonly ITaskIntelligence _intelligence;
    private readonly TaskPlanManager _manager = new();
    private TaskPlanningResult? _planningResult;

    public TaskSessionPlanner(ITaskIntelligence intelligence)
    {
        _intelligence = intelligence ?? throw new ArgumentNullException(nameof(intelligence));
    }

    public TaskSessionPlanState? Current { get; private set; }

    public async Task<TaskSessionPlanState> PlanAsync(
        string goal,
        CancellationToken cancellationToken = default)
    {
        _planningResult = await _intelligence.PlanTaskAsync(goal, cancellationToken);
        Current = CreateState(_manager.Start(_planningResult.Plan));
        return Current;
    }

    public TaskSessionPlanState MarkCurrentComplete(
        CompletionSource source,
        DateTimeOffset completedAt)
    {
        EnsurePlanned();
        Current = CreateState(_manager.MarkCurrentComplete(source, completedAt));
        return Current;
    }

    public TaskSessionPlanState SuggestCompletion(string evidence)
    {
        EnsurePlanned();
        Current = CreateState(_manager.SuggestCompletion(evidence));
        return Current;
    }

    public Task<RelevanceJudgment> JudgeRelevanceAsync(
        TaskContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (TaskRelevanceScorer.IsSelfWindow(context.ProcessName))
        {
            return Task.FromResult(new RelevanceJudgment(
                TaskRelevanceScorer.SelfWindowScore,
                RelevanceClass.Relevant,
                "Anchor's own window is never a detour",
                false,
                DateTimeOffset.MaxValue));
        }
        return _intelligence.JudgeRelevanceAsync(context, cancellationToken);
    }

    public Task<TaskStep> BreakDownCurrentStepAsync(
        TaskContext context,
        CancellationToken cancellationToken = default) =>
        _intelligence.BreakDownStepAsync(context, cancellationToken);

    public Task<ProgressJudgment> JudgeProgressAsync(
        ProgressEvidence evidence,
        CancellationToken cancellationToken = default) =>
        _intelligence.JudgeProgressAsync(evidence, cancellationToken);

    public Task<PictureGrading> GradePicturesAsync(
        PictureGradingRequest request,
        CancellationToken cancellationToken = default) =>
        _intelligence.GradePicturesAsync(request, cancellationToken);

    public Task<TextGrading> GradeTextBlocksAsync(
        TextGradingRequest request,
        CancellationToken cancellationToken = default) =>
        _intelligence.GradeTextBlocksAsync(request, cancellationToken);

    public Task<ContextReminder> ComposeReminderAsync(
        ContextCapsule capsule,
        CancellationToken cancellationToken = default) =>
        _intelligence.ComposeReminderAsync(capsule, cancellationToken);

    public TaskIntelligenceAvailability Availability => _intelligence.Availability;

    public TaskSessionPlanState DismissSuggestion()
    {
        EnsurePlanned();
        Current = CreateState(_manager.DismissSuggestion());
        return Current;
    }

    public TaskSessionPlanState ConfirmSuggestedCompletion(DateTimeOffset completedAt)
    {
        EnsurePlanned();
        Current = CreateState(_manager.ConfirmSuggestedCompletion(completedAt));
        return Current;
    }

    private TaskSessionPlanState CreateState(TaskProgressSnapshot progress)
    {
        var result = _planningResult
            ?? throw new InvalidOperationException("The task has not been planned.");
        return new TaskSessionPlanState(
            progress,
            result.Source,
            result.IsFallback,
            result.ErrorCode);
    }

    private void EnsurePlanned()
    {
        if (_planningResult is null)
        {
            throw new InvalidOperationException("Plan the task before updating progress.");
        }
    }
}
