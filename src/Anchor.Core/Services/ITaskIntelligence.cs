using Anchor.Core.Models;

namespace Anchor.Core.Services;

public interface ITaskIntelligence
{
    TaskIntelligenceAvailability Availability { get; }

    Task<TaskPlanningResult> PlanTaskAsync(
        string goal,
        CancellationToken cancellationToken = default);

    Task<RelevanceJudgment> JudgeRelevanceAsync(
        TaskContext context,
        CancellationToken cancellationToken = default);

    Task<TaskStep> BreakDownStepAsync(
        TaskContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Decides from what is visible on screen whether the current step is finished.</summary>
    Task<ProgressJudgment> JudgeProgressAsync(
        ProgressEvidence evidence,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(LocalProgressJudge.Judge(evidence));

    /// <summary>Phrases the "where you were" reminder for a frozen context capsule.</summary>
    Task<ContextReminder> ComposeReminderAsync(
        ContextCapsule capsule,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(LocalContextReminder.Compose(capsule));
}
