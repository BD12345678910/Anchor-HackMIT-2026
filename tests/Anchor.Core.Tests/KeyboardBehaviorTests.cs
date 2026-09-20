using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class KeyboardBehaviorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    private static KeyboardBehavior Run(
        KeyboardBehaviorAnalyzer analyzer,
        KeyStrokeCounts counts,
        bool? screenChanged,
        bool? hasTextCaret,
        int ticks = 5) =>
        Enumerable.Range(0, ticks)
            .Select(tick => analyzer.Observe(Start.AddSeconds(tick), counts, screenChanged, hasTextCaret))
            .ToArray()[^1];

    [Fact]
    public void Writing_prose_into_an_editor_is_not_random()
    {
        var behavior = Run(
            new KeyboardBehaviorAnalyzer(),
            new KeyStrokeCounts(Letters: 14, Editing: 2, Modifiers: 1),
            screenChanged: true,
            hasTextCaret: true);

        Assert.False(behavior.IsRandom);
    }

    [Fact]
    public void Typing_code_with_symbols_and_digits_is_not_random()
    {
        var behavior = Run(
            new KeyboardBehaviorAnalyzer(),
            new KeyStrokeCounts(Letters: 9, Digits: 3, Editing: 4, Modifiers: 3, Other: 4),
            screenChanged: true,
            hasTextCaret: true);

        Assert.False(behavior.IsRandom);
    }

    [Fact]
    public void Thinking_with_the_odd_keystroke_is_not_random()
    {
        var behavior = Run(
            new KeyboardBehaviorAnalyzer(),
            new KeyStrokeCounts(Function: 1),
            screenChanged: null,
            hasTextCaret: false);

        Assert.False(behavior.IsRandom);
    }

    [Fact]
    public void Typing_into_a_page_that_takes_no_text_is_random()
    {
        var behavior = Run(
            new KeyboardBehaviorAnalyzer(),
            new KeyStrokeCounts(Letters: 7),
            screenChanged: false,
            hasTextCaret: false);

        Assert.True(behavior.IsRandom);
        Assert.Contains("takes no text", behavior.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Typing_where_text_appears_is_not_random_even_without_a_caret()
    {
        var behavior = Run(
            new KeyboardBehaviorAnalyzer(),
            new KeyStrokeCounts(Letters: 7),
            screenChanged: true,
            hasTextCaret: false);

        Assert.False(behavior.IsRandom);
    }

    [Fact]
    public void A_short_burst_on_an_unwritable_page_is_not_enough()
    {
        var analyzer = new KeyboardBehaviorAnalyzer();

        var behavior = analyzer.Observe(
            Start,
            new KeyStrokeCounts(Letters: 5),
            screenChanged: false,
            hasTextCaret: false);

        Assert.False(behavior.IsRandom);
    }

    [Fact]
    public void Hammering_keys_that_do_nothing_is_random()
    {
        var behavior = Run(
            new KeyboardBehaviorAnalyzer(),
            new KeyStrokeCounts(Letters: 2, Function: 6, Other: 4),
            screenChanged: null,
            hasTextCaret: null);

        Assert.True(behavior.IsRandom);
        Assert.True(behavior.UnproductiveShare >= KeyboardBehaviorAnalyzer.UnproductiveShare);
    }

    [Fact]
    public void Holding_the_arrow_keys_to_skim_is_random()
    {
        var behavior = Run(
            new KeyboardBehaviorAnalyzer(),
            new KeyStrokeCounts(Navigation: 10),
            screenChanged: true,
            hasTextCaret: null);

        Assert.True(behavior.IsRandom);
    }

    [Fact]
    public void Paging_through_a_document_at_a_reading_pace_is_not_random()
    {
        var behavior = Run(
            new KeyboardBehaviorAnalyzer(),
            new KeyStrokeCounts(Navigation: 2),
            screenChanged: true,
            hasTextCaret: null);

        Assert.False(behavior.IsRandom);
    }

    [Fact]
    public void Returning_to_real_typing_clears_the_run()
    {
        var analyzer = new KeyboardBehaviorAnalyzer();
        var random = Run(analyzer, new KeyStrokeCounts(Letters: 7), screenChanged: false, hasTextCaret: false);
        Assert.True(random.IsRandom);

        var recovered = analyzer.Observe(
            Start.AddSeconds(6),
            new KeyStrokeCounts(Letters: 8, Editing: 2),
            screenChanged: true,
            hasTextCaret: true);

        Assert.False(recovered.IsRandom);
        Assert.Null(recovered.StartedAt);
    }

    [Fact]
    public void Sustained_random_typing_reaches_the_state_machine()
    {
        var fusion = new AttentionFusion();
        AttentionFusionResult? result = null;

        for (var tick = 0; tick < 6; tick++)
        {
            result = fusion.Apply(AttentionEvidence.At(
                Start.AddSeconds(tick),
                keyCount: 7,
                adapterRelevance: 0.8,
                keyStrokes: new KeyStrokeCounts(Letters: 7),
                screenTextChanged: false,
                hasTextCaret: false));
        }

        Assert.NotNull(result);
        Assert.True(result.Window.RandomTypingSustained);
        Assert.Contains("random_typing", result.Prediction.ReasonCodes);
        // Keys landing nowhere are enough on their own: the window is still "relevant", so the
        // relevance-weighted score never crosses the threshold and nothing would be shown.
        Assert.Equal(AttentionState.Distracted, result.Prediction.State);
    }

    [Fact]
    public void Real_typing_leaves_the_window_clean()
    {
        var fusion = new AttentionFusion();
        AttentionFusionResult? result = null;

        for (var tick = 0; tick < 6; tick++)
        {
            result = fusion.Apply(AttentionEvidence.At(
                Start.AddSeconds(tick),
                keyCount: 12,
                adapterRelevance: 0.8,
                activity: ActivityKind.Writing,
                keyStrokes: new KeyStrokeCounts(Letters: 11, Editing: 1),
                screenTextChanged: true,
                hasTextCaret: true));
        }

        Assert.NotNull(result);
        Assert.False(result.Window.RandomTypingSustained);
        Assert.DoesNotContain("random_typing", result.Prediction.ReasonCodes);
    }
}
