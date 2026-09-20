using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class ScrollBehaviorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Steady_reading_scroll_is_not_thrashing()
    {
        var analyzer = new ScrollBehaviorAnalyzer();

        var behaviors = Enumerable.Range(0, 10)
            .Select(tick => analyzer.Observe(Start.AddSeconds(tick), notches: 2, reversals: 0, keyCount: 0))
            .ToArray();

        Assert.All(behaviors, behavior => Assert.False(behavior.IsThrashing));
    }

    [Fact]
    public void Single_fast_flick_alone_is_not_a_sustained_burst()
    {
        var analyzer = new ScrollBehaviorAnalyzer();

        var behavior = analyzer.Observe(Start, notches: 14, reversals: 1, keyCount: 0);

        Assert.True(behavior.IsThrashing);
        Assert.Equal(Start, behavior.StartedAt);

        var fusion = new AttentionFusion();
        var result = fusion.Apply(AttentionEvidence.At(
            Start,
            adapterRelevance: 0.9,
            progressObserved: true,
            scrollReversalCount: 1,
            scrollNotchCount: 14));

        Assert.False(result.Window.ScrollThrashSustained);
        Assert.DoesNotContain("scroll_thrash", result.Prediction.ReasonCodes);
    }

    [Fact]
    public void High_scroll_rate_is_thrashing()
    {
        var analyzer = new ScrollBehaviorAnalyzer();

        var behavior = Enumerable.Range(0, 4)
            .Select(tick => analyzer.Observe(Start.AddSeconds(tick), notches: 12, reversals: 0, keyCount: 0))
            .ToArray()[^1];

        Assert.True(behavior.IsThrashing);
        Assert.True(behavior.NotchesPerSecond >= ScrollBehaviorAnalyzer.FastNotchesPerSecond);
    }

    [Fact]
    public void Moderate_rate_with_direction_reversals_is_thrashing()
    {
        var analyzer = new ScrollBehaviorAnalyzer();

        var behavior = Enumerable.Range(0, 4)
            .Select(tick => analyzer.Observe(Start.AddSeconds(tick), notches: 5, reversals: 2, keyCount: 0))
            .ToArray()[^1];

        Assert.True(behavior.IsThrashing);
        Assert.True(behavior.Reversals >= ScrollBehaviorAnalyzer.ErraticReversals);
    }

    [Fact]
    public void Scrolling_while_typing_is_not_thrashing()
    {
        var analyzer = new ScrollBehaviorAnalyzer();

        var behavior = Enumerable.Range(0, 4)
            .Select(tick => analyzer.Observe(Start.AddSeconds(tick), notches: 12, reversals: 2, keyCount: 9))
            .ToArray()[^1];

        Assert.False(behavior.IsThrashing);
    }

    [Fact]
    public void Burst_ends_when_scrolling_settles_down()
    {
        var analyzer = new ScrollBehaviorAnalyzer();
        foreach (var tick in Enumerable.Range(0, 4))
        {
            analyzer.Observe(Start.AddSeconds(tick), notches: 12, reversals: 2, keyCount: 0);
        }

        var recovered = Enumerable.Range(4, 6)
            .Select(tick => analyzer.Observe(Start.AddSeconds(tick), notches: 0, reversals: 0, keyCount: 0))
            .ToArray()[^1];

        Assert.False(recovered.IsThrashing);
        Assert.Null(recovered.StartedAt);
    }

    [Fact]
    public void Sustained_pdf_scroll_burst_reports_scroll_thrash_and_asks_for_a_recovery_card()
    {
        var fusion = new AttentionFusion();

        var results = Enumerable.Range(0, 6)
            .Select(tick => fusion.Apply(AttentionEvidence.At(
                Start.AddSeconds(tick),
                keyCount: 0,
                mouseDistance: 5,
                idleSeconds: 0,
                adapterRelevance: 0.9,
                progressObserved: true,
                scrollReversalCount: 3,
                scrollNotchCount: 14)))
            .ToArray();

        Assert.DoesNotContain("scroll_thrash", results[0].Prediction.ReasonCodes);
        Assert.Contains("scroll_thrash", results[^1].Prediction.ReasonCodes);
        Assert.Equal(AttentionState.Stuck, results[^1].Prediction.State);
        Assert.True(results[^1].Window.ScrollThrashSustained);
    }

    [Fact]
    public void Paging_keys_do_not_count_as_typing_that_hides_a_burst()
    {
        var fusion = new AttentionFusion();

        var result = Enumerable.Range(0, 6)
            .Select(tick => fusion.Apply(AttentionEvidence.At(
                Start.AddSeconds(tick),
                keyCount: 12,
                adapterRelevance: 0.9,
                progressObserved: true,
                scrollReversalCount: 3,
                scrollNotchCount: 12,
                navigationKeyCount: 12)))
            .ToArray()[^1];

        Assert.Contains("scroll_thrash", result.Prediction.ReasonCodes);
    }

    [Fact]
    public void Ordinary_reading_scroll_never_reports_scroll_thrash()
    {
        var fusion = new AttentionFusion();

        var results = Enumerable.Range(0, 12)
            .Select(tick => fusion.Apply(AttentionEvidence.At(
                Start.AddSeconds(tick),
                keyCount: 1,
                mouseDistance: 12,
                adapterRelevance: 0.9,
                progressObserved: true,
                scrollReversalCount: 1,
                scrollNotchCount: 3)))
            .ToArray();

        Assert.All(results, result =>
            Assert.DoesNotContain("scroll_thrash", result.Prediction.ReasonCodes));
    }

    [Fact]
    public void Recovery_card_keeps_the_page_from_before_the_scroll_burst()
    {
        var manager = new ContextCapsuleManager(Guid.NewGuid(), "Read the methods section");
        manager.Observe(new ContextObservation(
            "SumatraPDF", "paper.pdf", "Methods",
            "Read the sampling paragraph", "Compare with the results table",
            null, "paper.pdf", 0.9, false,
            Activity: ActivityKind.Reading,
            FocusText: "Participants were sampled from two schools.",
            FocusSource: FocusSource.Gaze,
            DocumentPosition: "page 7 of 30"));

        var duringBurst = manager.Observe(new ContextObservation(
            "SumatraPDF", "paper.pdf", "References",
            "Scrolled", "Keep scrolling", null, "paper.pdf", 0.9, false,
            Activity: ActivityKind.Reading,
            DocumentPosition: "page 26 of 30",
            IsScrollBurst: true));

        Assert.NotNull(duringBurst);
        Assert.Equal("page 7 of 30", duringBurst!.DocumentPosition);
        Assert.Equal("Methods", duringBurst.Location);
        Assert.Equal(DistractionReason.ScrollBurst, duringBurst.Reason);

        var reminder = LocalContextReminder.Compose(duringBurst);
        Assert.Contains("page 7 of 30", reminder.WhereYouWere, StringComparison.Ordinal);
        Assert.Contains("page 7 of 30", reminder.ResumeWith, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Page 7 of 30", "page 7 of 30")]
    [InlineData("7 / 30", "page 7 of 30")]
    [InlineData("page 12", "page 12")]
    public void Page_indicators_are_read_from_screen_text(string line, string expected)
    {
        var lines = new[]
        {
            new ScreenLine(line, 0, 0, 100, 20),
            new ScreenLine("Participants were sampled from two schools.", 0, 30, 600, 20)
        };

        Assert.Equal(expected, DocumentPositionReader.DescribePage(lines));
    }

    [Fact]
    public void Window_title_page_number_is_used_when_screen_text_has_none()
    {
        Assert.Equal(
            "page 4 of 18",
            DocumentPositionReader.DescribePage(null, "paper.pdf - page 4 of 18 - SumatraPDF"));
    }

    [Fact]
    public void Text_without_a_page_indicator_yields_no_position()
    {
        var lines = new[] { new ScreenLine("Methods", 0, 0, 100, 20) };

        Assert.Null(DocumentPositionReader.DescribePage(lines, "paper.pdf - SumatraPDF"));
    }
}
