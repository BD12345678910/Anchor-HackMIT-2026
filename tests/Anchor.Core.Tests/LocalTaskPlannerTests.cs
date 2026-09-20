using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class LocalTaskPlannerTests
{
    [Fact]
    public void Quantified_goal_becomes_one_step_per_unit()
    {
        var plan = LocalTaskPlanner.Plan("do 3 usaco problems");

        Assert.Equal(5, plan.Steps.Count);
        Assert.Contains("open the first one", plan.Steps[0].Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Do usaco problem 1 of 3", plan.Steps[1].Title);
        Assert.Equal("Do usaco problem 2 of 3", plan.Steps[2].Title);
        Assert.Equal("Do usaco problem 3 of 3", plan.Steps[3].Title);
        Assert.Contains("Review", plan.Steps[4].Title, StringComparison.Ordinal);
        new TaskPlanManager().Start(plan);
    }

    [Theory]
    [InlineData("read 5 chapters", 5, "chapters")]
    [InlineData("Solve two leetcode medium problems", 2, "problems")]
    [InlineData("finish the 4 physics exercises", 4, "exercises")]
    [InlineData("write 1 essay", 1, "essay")]
    public void Quantities_and_units_are_parsed(string goal, int count, string unit)
    {
        var parsed = LocalTaskPlanner.ParseQuantifiedGoal(goal);

        Assert.NotNull(parsed);
        Assert.Equal(count, parsed.Count);
        Assert.Equal(unit, parsed.Unit, ignoreCase: true);
    }

    [Fact]
    public void Large_quantities_are_capped_to_eight_steps()
    {
        var plan = LocalTaskPlanner.Plan("do 40 flashcards");

        Assert.InRange(plan.Steps.Count, 2, 8);
        Assert.Contains(plan.Steps, static step => step.Title.Contains("to 40", StringComparison.Ordinal));
        new TaskPlanManager().Start(plan);
    }

    [Fact]
    public void Unquantified_goal_gets_generic_multi_step_plan()
    {
        var plan = LocalTaskPlanner.Plan("Finish the HackMIT project plan");

        Assert.InRange(plan.Steps.Count, 2, 8);
        Assert.Contains("HackMIT project plan", plan.Steps[0].Title, StringComparison.Ordinal);
        Assert.Null(LocalTaskPlanner.ParseQuantifiedGoal("Finish the HackMIT project plan"));
    }

    [Fact]
    public void Evidence_naming_a_specific_problem_suggests_completing_the_pick_step()
    {
        var plan = LocalTaskPlanner.Plan("do 3 usaco problems");

        var match = TaskEvidenceMatcher.Evaluate(plan.Steps[0], plan.Goal, "USACO 2021 December Bronze Problem 1");

        Assert.True(match.SuggestsCompletion, $"score {match.Score}");
        Assert.Contains("usaco", match.MatchedTokens);
    }

    [Fact]
    public void Unrelated_evidence_does_not_suggest_completion()
    {
        var plan = LocalTaskPlanner.Plan("do 3 usaco problems");

        var match = TaskEvidenceMatcher.Evaluate(plan.Steps[0], plan.Goal, "YouTube - Home");

        Assert.False(match.SuggestsCompletion);
        Assert.Empty(match.MatchedTokens);
    }

    [Fact]
    public void Empty_evidence_scores_zero()
    {
        var step = new TaskStep("step-1", "Read chapter 2", "Chapter 2 finished");

        Assert.Equal(0, TaskEvidenceMatcher.Evaluate(step, "read 3 chapters", "   ").Score);
    }

    [Fact]
    public void Page_editing_keywords_drop_filler_numbers_and_duplicates()
    {
        var keywords = TaskEvidenceMatcher.Keywords("do 3 usaco problems", "Open the first USACO problem");

        Assert.Contains("usaco", keywords);
        Assert.Contains("problems", keywords);
        Assert.DoesNotContain("the", keywords);
        Assert.DoesNotContain("3", keywords);
        Assert.Equal(keywords.Distinct(StringComparer.OrdinalIgnoreCase).Count(), keywords.Count);
    }
}
