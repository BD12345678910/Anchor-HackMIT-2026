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

    /// <summary>Grades each on-screen picture as illustrating the task, unrelated, or attention bait.</summary>
    Task<PictureGrading> GradePicturesAsync(
        PictureGradingRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(PictureTreatmentPlanner.LocalGrade(request));

    /// <summary>Grades each on-screen passage as part of the task or off-task (to be dimmed).</summary>
    Task<TextGrading> GradeTextBlocksAsync(
        TextGradingRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(TextBlockPlanner.LocalGrade(request));

    /// <summary>Phrases the "where you were" reminder for a frozen context capsule.</summary>
    Task<ContextReminder> ComposeReminderAsync(
        ContextCapsule capsule,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(LocalContextReminder.Compose(capsule));
}
