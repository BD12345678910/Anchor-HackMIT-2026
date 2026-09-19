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
}
