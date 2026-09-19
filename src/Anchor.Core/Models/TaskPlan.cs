namespace Anchor.Core.Models;

public sealed record TaskStep(string Id, string Title, string CompletionCriterion);

public sealed record TaskPlan(string Goal, IReadOnlyList<TaskStep> Steps);

public enum CompletionSource
{
    User,
    Adapter,
    LlmSuggestionConfirmed
}

public sealed record TaskCompletion(
    string StepId,
    CompletionSource Source,
    DateTimeOffset CompletedAt);

public sealed record TaskProgressSnapshot(
    TaskPlan Plan,
    string Goal,
    TaskStep? CurrentStep,
    int CompletedCount,
    int TotalCount,
    string? PendingSuggestion,
    IReadOnlyList<TaskCompletion> Completions);
