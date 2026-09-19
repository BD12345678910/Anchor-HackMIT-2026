using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class ReplayScenarioTests
{
    [Theory]
    [InlineData("focused-to-distracted.jsonl", AttentionState.Distracted, InterventionKind.IntentionGate)]
    [InlineData("interrupted-and-returned.jsonl", AttentionState.Focused, InterventionKind.RecoveryCard)]
    [InlineData("stuck-reading.jsonl", AttentionState.Stuck, InterventionKind.RecoveryCard)]
    public async Task Replay_scenarios_match_declared_states_interventions_and_metrics(
        string file,
        AttentionState expectedFinalState,
        InterventionKind expectedIntervention)
    {
        var report = await ReplayScenarioRunner.RunFileAsync(Path.Combine(FindRoot(), "demo", "replay", file));

        Assert.Equal(expectedFinalState, report.FinalState);
        Assert.Contains(expectedIntervention, report.Interventions);
        Assert.True(report.Progress.InterruptionCount >= (file == "interrupted-and-returned.jsonl" ? 1 : 0));
        Assert.All(report.Steps, static step => Assert.True(step.ExpectationMatched, step.ExpectationMessage));
        if (expectedIntervention == InterventionKind.RecoveryCard)
        {
            Assert.NotNull(report.LastContextCapsule);
            Assert.False(string.IsNullOrWhiteSpace(report.LastContextCapsule.NextAction));
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Anchor.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate Anchor.slnx.");
    }
}
