using System.Globalization;
using System.Text.RegularExpressions;
using Anchor.Core.Models;

namespace Anchor.Core.Services;

/// <summary>
/// Deterministic, offline task decomposition used when no language model is available.
/// Quantified goals ("do 3 usaco problems", "read 5 chapters") become one step per unit so
/// that progress can be tracked unit by unit.
/// </summary>
public static partial class LocalTaskPlanner
{
    public const int MaxUnitSteps = 6;

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["a"] = 1, ["an"] = 1
    };

    private static readonly string[] Verbs =
    [
        "do", "solve", "complete", "finish", "read", "write", "review", "study", "practice",
        "watch", "answer", "work through", "work on", "go through", "learn", "summarize",
        "summarise", "revise", "memorize", "memorise", "draft", "outline", "grade", "debug",
        "implement", "prepare", "translate"
    ];

    public static TaskPlan Plan(string goal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        var trimmed = goal.Trim();
        var quantified = ParseQuantifiedGoal(trimmed);
        var steps = quantified is null ? GenericSteps(trimmed) : UnitSteps(trimmed, quantified);
        return new TaskPlan(trimmed, steps);
    }

    public static QuantifiedGoal? ParseQuantifiedGoal(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            return null;
        }

        var match = QuantityPattern().Match(goal);
        if (!match.Success)
        {
            return null;
        }

        var quantityText = match.Groups["count"].Value;
        if (!int.TryParse(quantityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            && !NumberWords.TryGetValue(quantityText, out count))
        {
            return null;
        }

        if (count < 1 || count > 200)
        {
            return null;
        }

        var unit = match.Groups["unit"].Value.Trim();
        if (unit.Length == 0)
        {
            return null;
        }

        var verb = match.Groups["verb"].Success ? match.Groups["verb"].Value.Trim() : null;
        var subject = match.Groups["subject"].Success ? match.Groups["subject"].Value.Trim() : string.Empty;
        return new QuantifiedGoal(count, verb, subject, unit);
    }

    private static TaskStep[] UnitSteps(string goal, QuantifiedGoal quantified)
    {
        var singular = Singularize(quantified.Unit);
        var subject = string.IsNullOrWhiteSpace(quantified.Subject) ? string.Empty : $"{quantified.Subject} ";
        var verb = string.IsNullOrWhiteSpace(quantified.Verb)
            ? "Complete"
            : Capitalize(quantified.Verb);

        var steps = new List<TaskStep>
        {
            new(
                "step-1",
                $"Pick the {quantified.Count} {subject}{quantified.Unit} you will work on and open the first one",
                $"The first {subject}{singular} is open and its name is noted")
        };

        var visibleUnits = Math.Min(quantified.Count, MaxUnitSteps);
        for (var index = 1; index <= visibleUnits; index++)
        {
            var isLastVisible = index == visibleUnits && quantified.Count > visibleUnits;
            var title = isLastVisible
                ? $"{verb} {subject}{quantified.Unit} {index} to {quantified.Count}"
                : $"{verb} {subject}{singular} {index} of {quantified.Count}";
            var criterion = isLastVisible
                ? $"All remaining {subject}{quantified.Unit} are finished"
                : $"{Capitalize(subject)}{singular} {index} is finished and checked";
            steps.Add(new TaskStep($"step-{index + 1}", title, criterion));
        }

        steps.Add(new TaskStep(
            $"step-{steps.Count + 1}",
            $"Review what you learned from the {quantified.Count} {subject}{quantified.Unit}",
            "One-sentence takeaway is written down"));

        return steps.ToArray();
    }

    private static TaskStep[] GenericSteps(string goal)
    {
        var focus = StripLeadingVerb(goal);
        return
        [
            new("step-1", $"Open everything needed for: {focus}", "Required files, pages, or tools are open"),
            new("step-2", $"Do the first concrete part of: {focus}", "One visible piece of work exists"),
            new("step-3", $"Continue until the main work of \"{focus}\" is done", "The main work is complete"),
            new("step-4", "Review the result and note what is left", "A short note of remaining work exists")
        ];
    }

    private static string StripLeadingVerb(string goal)
    {
        foreach (var verb in Verbs.OrderByDescending(static item => item.Length))
        {
            if (goal.StartsWith(verb + " ", StringComparison.OrdinalIgnoreCase))
            {
                return goal[(verb.Length + 1)..].Trim();
            }
        }

        return goal;
    }

    internal static string Singularize(string unit)
    {
        if (unit.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && unit.Length > 3)
        {
            return unit[..^3] + "y";
        }

        if (unit.EndsWith("sses", StringComparison.OrdinalIgnoreCase)
            || unit.EndsWith("ches", StringComparison.OrdinalIgnoreCase)
            || unit.EndsWith("shes", StringComparison.OrdinalIgnoreCase)
            || unit.EndsWith("xes", StringComparison.OrdinalIgnoreCase))
        {
            return unit[..^2];
        }

        if (unit.EndsWith('s') && !unit.EndsWith("ss", StringComparison.OrdinalIgnoreCase) && unit.Length > 2)
        {
            return unit[..^1];
        }

        return unit;
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    [GeneratedRegex(
        @"^(?:(?<verb>do|solve|complete|finish|read|write|review|study|practice|watch|answer|work through|work on|go through|learn|summarize|summarise|revise|memorize|memorise|draft|outline|grade|debug|implement|prepare|translate)\s+)?(?:(?:the|my|these|those|some|about|at least|another)\s+)*(?<count>\d{1,3}|one|two|three|four|five|six|seven|eight|nine|ten)\s+(?:more\s+)?(?:(?<subject>(?:[A-Za-z][\w+#.-]*\s+){1,3}?))?(?<unit>problems?|questions?|exercises?|chapters?|pages?|sections?|lessons?|videos?|lectures?|essays?|paragraphs?|papers?|articles?|units?|modules?|tasks?|sets?|flashcards?|cards?|words?|slides?|labs?|assignments?|katas?|puzzles?|quizzes|quiz|drills?|passages?|readings?|episodes?|worksheets?|tickets?|bugs?|issues?|tests?|items?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuantityPattern();
}

public sealed record QuantifiedGoal(int Count, string? Verb, string Subject, string Unit);
