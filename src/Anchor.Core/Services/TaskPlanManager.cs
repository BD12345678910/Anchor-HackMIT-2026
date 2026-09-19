using System.Text;
using Anchor.Core.Models;

namespace Anchor.Core.Services;

public sealed class TaskPlanManager
{
    private readonly List<TaskCompletion> _completions = [];
    private TaskPlan? _plan;
    private int _currentIndex;
    private string? _pendingSuggestion;

    public TaskProgressSnapshot Start(TaskPlan plan) => ReplacePlan(plan);

    public TaskProgressSnapshot ReplacePlan(TaskPlan plan)
    {
        _plan = ValidateAndNormalize(plan);
        _currentIndex = 0;
        _pendingSuggestion = null;
        _completions.Clear();
        return Snapshot();
    }

    public TaskProgressSnapshot MarkCurrentComplete(
        CompletionSource source,
        DateTimeOffset completedAt)
    {
        var current = CurrentStep()
            ?? throw new InvalidOperationException("All task steps are already complete.");

        _completions.Add(new TaskCompletion(current.Id, source, completedAt));
        _currentIndex++;
        _pendingSuggestion = null;
        return Snapshot();
    }

    public TaskProgressSnapshot SuggestCompletion(string evidence)
    {
        if (CurrentStep() is null)
        {
            throw new InvalidOperationException("All task steps are already complete.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        _pendingSuggestion = Bound(evidence, 500);
        return Snapshot();
    }

    public TaskProgressSnapshot ConfirmSuggestedCompletion(DateTimeOffset completedAt)
    {
        if (_pendingSuggestion is null)
        {
            throw new InvalidOperationException("No completion suggestion is pending.");
        }

        return MarkCurrentComplete(CompletionSource.LlmSuggestionConfirmed, completedAt);
    }

    public TaskProgressSnapshot Snapshot()
    {
        var plan = _plan ?? throw new InvalidOperationException("No task plan has started.");
        return new TaskProgressSnapshot(
            plan,
            plan.Goal,
            CurrentStep(),
            _completions.Count,
            plan.Steps.Count,
            _pendingSuggestion,
            _completions.ToArray());
    }

    internal static TaskPlan ValidateAndNormalize(TaskPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Goal);
        ArgumentNullException.ThrowIfNull(plan.Steps);
        if (plan.Steps.Count is < 1 or > 8)
        {
            throw new ArgumentException("A task plan must contain between one and eight steps.", nameof(plan));
        }

        var normalizedGoal = Bound(plan.Goal, 240);
        var normalizedSteps = new TaskStep[plan.Steps.Count];
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = normalizedGoal != plan.Goal;

        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index]
                ?? throw new ArgumentException("Task steps cannot be null.", nameof(plan));
            ArgumentException.ThrowIfNullOrWhiteSpace(step.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(step.Title);
            ArgumentException.ThrowIfNullOrWhiteSpace(step.CompletionCriterion);

            var normalized = new TaskStep(
                Bound(step.Id, 80),
                Bound(step.Title, 240),
                Bound(step.CompletionCriterion, 500));
            if (!identifiers.Add(normalized.Id))
            {
                throw new ArgumentException($"Duplicate task step identifier: {normalized.Id}", nameof(plan));
            }

            normalizedSteps[index] = normalized;
            changed |= normalized != step;
        }

        return changed ? new TaskPlan(normalizedGoal, normalizedSteps) : plan;
    }

    private TaskStep? CurrentStep()
    {
        var plan = _plan ?? throw new InvalidOperationException("No task plan has started.");
        return _currentIndex < plan.Steps.Count ? plan.Steps[_currentIndex] : null;
    }

    private static string Bound(string value, int maximumScalars)
    {
        var trimmed = value.Trim();
        return string.Concat(
            trimmed.EnumerateRunes()
                .Take(maximumScalars)
                .Select(static rune => rune.ToString()));
    }
}
