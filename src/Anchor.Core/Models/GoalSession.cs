using System.Text;

namespace Anchor.Core.Models;

public sealed record GoalSession(Guid Id, string Title, DateTimeOffset StartedAt)
{
    public TaskPlan? Plan { get; init; }

    public static GoalSession Create(string title, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(title);

        var normalized = title.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("A task title is required.", nameof(title));
        }

        normalized = string.Concat(
            normalized.EnumerateRunes().Take(240).Select(static rune => rune.ToString()));

        return new GoalSession(Guid.NewGuid(), normalized, startedAt);
    }

    public GoalSession WithPlan(TaskPlan plan) =>
        this with { Plan = Services.TaskPlanManager.ValidateAndNormalize(plan) };
}
