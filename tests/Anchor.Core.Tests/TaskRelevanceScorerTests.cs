using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class TaskRelevanceScorerTests
{
    [Fact]
    public void Problem_page_matching_the_goal_is_relevant()
    {
        var score = TaskRelevanceScorer.Score(
            "do 3 usaco problems Do problem 1 of 3",
            "chrome",
            "USACO 2021 December Contest, Bronze Problem 1. Closest Cow Wins - Google Chrome");

        Assert.True(score >= 0.62, score.ToString());
    }

    [Fact]
    public void Known_entertainment_sites_are_strong_detours()
    {
        var score = TaskRelevanceScorer.Score("do 3 usaco problems", "chrome", "Funny cats compilation - YouTube");

        Assert.True(score < 0.2, score.ToString());
    }

    [Fact]
    public void Editors_and_readers_without_title_overlap_stay_neutral()
    {
        var score = TaskRelevanceScorer.Score("read chapter 4 of the biology textbook", "Acrobat", "lecture-notes.pdf");

        Assert.Equal(TaskRelevanceScorer.NeutralToolScore, score);
    }

    [Fact]
    public void Anchor_window_never_counts_as_a_detour()
    {
        Assert.Equal(TaskRelevanceScorer.SelfWindowScore, TaskRelevanceScorer.Score("write essay", "Anchor", "Anchor"));
    }

    [Fact]
    public void Sustained_off_task_window_alone_reaches_an_intention_gate()
    {
        var machine = AttentionStateMachine.CreateDefault();
        var now = DateTimeOffset.UnixEpoch;
        var policy = new InterventionPolicy(() => now);
        var kinds = new List<InterventionKind>();

        for (var i = 0; i < 4; i++)
        {
            var prediction = machine.Update(SensorWindow.Create(
                keyCount: 0,
                mouseDistance: 40,
                idleSeconds: 0,
                appRelevance: TaskRelevanceScorer.KnownDetourScore,
                isWorkerAvailable: false,
                timestamp: now));
            kinds.Add(policy.Decide(prediction, UserPreferences.Default).Kind);
            now += TimeSpan.FromSeconds(2);
        }

        Assert.Contains(InterventionKind.BeaconPulse, kinds);
        Assert.Contains(InterventionKind.IntentionGate, kinds);
        Assert.True(kinds.IndexOf(InterventionKind.BeaconPulse) < kinds.IndexOf(InterventionKind.IntentionGate));
    }
}
