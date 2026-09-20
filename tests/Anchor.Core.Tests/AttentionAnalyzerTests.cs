using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class AttentionAnalyzerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fewer_than_two_samples_yields_empty_analysis()
    {
        var analyzer = new AttentionAnalyzer();
        analyzer.Record(T0, AttentionPrediction.Create(AttentionState.Focused, 0.9, 0.1));

        Assert.Same(AttentionAnalysis.Empty, analyzer.Analyze(T0.AddMinutes(1)));
    }

    [Fact]
    public void Focus_bouts_recoveries_and_triggers_are_derived_from_state_transitions()
    {
        var analyzer = new AttentionAnalyzer();
        analyzer.Record(T0, AttentionPrediction.Create(AttentionState.Focused, 0.9, 0.1));
        analyzer.Record(T0.AddMinutes(10), AttentionPrediction.Create(AttentionState.Distracted, 0.9, 0.9, ["low_app_relevance", "foreground_switches"]));
        analyzer.Record(T0.AddMinutes(11), AttentionPrediction.Create(AttentionState.Recovering, 0.6, 0.4));
        analyzer.Record(T0.AddMinutes(12), AttentionPrediction.Create(AttentionState.Focused, 0.9, 0.1));
        analyzer.Record(T0.AddMinutes(18), AttentionPrediction.Create(AttentionState.Distracted, 0.9, 0.9, ["low_app_relevance"]));
        analyzer.Record(T0.AddMinutes(19), AttentionPrediction.Create(AttentionState.Focused, 0.9, 0.1));
        analyzer.RecordIntervention(new InterventionDecision(InterventionKind.RecoveryCard, "distracted", 0.9, TimeSpan.Zero));
        analyzer.RecordIntervention(new InterventionDecision(InterventionKind.None, "none", 0, TimeSpan.Zero));

        var analysis = analyzer.Analyze(T0.AddMinutes(23));

        Assert.Equal(2, analysis.Distractions);
        Assert.Equal(3, analysis.FocusBouts);
        Assert.Equal(TimeSpan.FromMinutes(10), analysis.LongestAttentionSpan);
        Assert.Equal(TimeSpan.FromMinutes(6), analysis.MedianAttentionSpan);
        Assert.Equal(TimeSpan.FromMinutes(4), analysis.CurrentAttentionSpan);
        Assert.Equal(TimeSpan.FromMinutes(1.5), analysis.MedianRecovery);
        Assert.Equal("low_app_relevance", analysis.Triggers[0].Key);
        Assert.Equal(2, analysis.Triggers[0].Value);
        Assert.Single(analysis.Interventions);
        Assert.Equal(InterventionKind.RecoveryCard, analysis.Interventions[0].Key);
        Assert.InRange(analysis.OnTaskShare, 0.89, 0.90);
        Assert.InRange(analysis.DistractionsPerHour, 6.3, 6.4);
    }

    [Fact]
    public void Trend_compares_early_and_late_bouts()
    {
        var analyzer = new AttentionAnalyzer();
        var at = T0;
        foreach (var minutes in new[] { 2, 2, 6, 6 })
        {
            analyzer.Record(at, AttentionPrediction.Create(AttentionState.Focused, 0.9, 0.1));
            at = at.AddMinutes(minutes);
            analyzer.Record(at, AttentionPrediction.Create(AttentionState.Distracted, 0.9, 0.9));
            at = at.AddSeconds(30);
        }

        Assert.StartsWith("Focus bouts are lengthening", analyzer.Analyze(at).Trend);
    }

    [Fact]
    public void Distractors_rank_places_by_time_lost_and_count_one_episode_per_visit()
    {
        var analyzer = new AttentionAnalyzer();
        analyzer.Record(T0, AttentionPrediction.Create(AttentionState.Focused, 0.9, 0.1), "code");
        analyzer.Record(T0.AddMinutes(5), AttentionPrediction.Create(AttentionState.Distracted, 0.9, 0.9), "youtube.com");
        analyzer.Record(T0.AddMinutes(11), AttentionPrediction.Create(AttentionState.Distracted, 0.9, 0.9), "reddit.com");
        analyzer.Record(T0.AddMinutes(13), AttentionPrediction.Create(AttentionState.Focused, 0.9, 0.1), "code");
        analyzer.Record(T0.AddMinutes(20), AttentionPrediction.Create(AttentionState.Distracted, 0.9, 0.9), "youtube.com");
        analyzer.Record(T0.AddMinutes(23), AttentionPrediction.Create(AttentionState.Focused, 0.9, 0.1), "code");

        var analysis = analyzer.Analyze(T0.AddMinutes(25));

        Assert.Equal(TimeSpan.FromMinutes(11), analysis.TimeLost);
        Assert.Equal("youtube.com", analysis.Distractors[0].Name);
        Assert.Equal(TimeSpan.FromMinutes(9), analysis.Distractors[0].TimeLost);
        Assert.Equal(2, analysis.Distractors[0].Episodes);
        Assert.Equal("reddit.com", analysis.Distractors[1].Name);
        Assert.Equal(TimeSpan.FromMinutes(2), analysis.Distractors[1].TimeLost);
        Assert.Equal(1, analysis.Distractors[1].Episodes);
        Assert.DoesNotContain(analysis.Distractors, item => item.Name == "code");
    }

    [Fact]
    public void Samples_without_a_place_still_analyze_and_rank_nothing()
    {
        var analyzer = new AttentionAnalyzer();
        analyzer.Record(T0, AttentionPrediction.Create(AttentionState.Focused, 0.9, 0.1));
        analyzer.Record(T0.AddMinutes(3), AttentionPrediction.Create(AttentionState.Distracted, 0.9, 0.9));
        analyzer.Record(T0.AddMinutes(5), AttentionPrediction.Create(AttentionState.Focused, 0.9, 0.1));

        var analysis = analyzer.Analyze(T0.AddMinutes(6));

        Assert.Empty(analysis.Distractors);
        Assert.Equal(TimeSpan.FromMinutes(2), analysis.TimeLost);
    }

    [Theory]
    [InlineData("chrome.exe", "En.Wikipedia.org", "en.wikipedia.org")]
    [InlineData("Code.exe", null, "Code")]
    [InlineData("Code", "", "Code")]
    [InlineData(null, null, null)]
    public void Place_labels_prefer_the_site_and_drop_the_exe_suffix(string? process, string? domain, string? expected) =>
        Assert.Equal(expected, AttentionAnalyzer.DescribePlace(process, domain));
}
