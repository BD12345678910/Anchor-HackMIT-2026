using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class TextBlockPlannerTests
{
    private static ScreenLine Line(string text, double left, double top, double width = 400, double height = 20) =>
        new(text, left, top, width, height);

    [Fact]
    public void Paragraph_lines_group_together_and_a_distant_column_stays_separate()
    {
        var blocks = TextBlockPlanner.Group(
        [
            Line("The cat is a small domesticated carnivorous mammal.", 100, 100),
            Line("It is the only domesticated species of the family Felidae.", 100, 122),
            Line("Trending now: celebrity gossip and shopping deals", 700, 400, 260),
            Line("Sponsored offers you may like today", 700, 422, 260),
        ]);

        Assert.Equal(2, blocks.Count);
        var article = Assert.Single(blocks, block => block.Text.StartsWith("The cat", StringComparison.Ordinal));
        Assert.Contains("family Felidae", article.Text, StringComparison.Ordinal);
        Assert.Equal(100, article.Left);
        Assert.Equal(100, article.Top);
        Assert.Equal(42, article.Height);
        var rail = Assert.Single(blocks, block => block.Text.StartsWith("Trending", StringComparison.Ordinal));
        Assert.Equal(700, rail.Left);
        Assert.Contains("Sponsored", rail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wide_vertical_gap_starts_a_new_block_even_in_the_same_column()
    {
        var blocks = TextBlockPlanner.Group(
        [
            Line("Behaviour of the domestic cat in the home", 100, 100),
            Line("See also: list of cat breeds and other articles", 100, 400),
        ]);

        Assert.Equal(2, blocks.Count);
        Assert.NotEqual(blocks[0].Key, blocks[1].Key);
    }

    [Fact]
    public void Keys_are_stable_for_the_same_text_and_ignore_case_and_spacing()
    {
        var first = TextBlockPlanner.Group([Line("Read the behaviour section", 10, 10)]);
        var second = TextBlockPlanner.Group([Line("READ   the Behaviour  section", 600, 900)]);

        Assert.Equal(first[0].Key, second[0].Key);
    }

    [Fact]
    public void Noise_lines_are_dropped_and_long_blocks_are_bounded()
    {
        var lines = new List<ScreenLine> { Line("ok", 10, 10), Line(" ", 10, 40) };
        for (var i = 0; i < 60; i++)
        {
            lines.Add(Line(new string('a', 40), 10, 100 + (i * 22)));
        }

        var blocks = TextBlockPlanner.Group(lines);

        Assert.All(blocks, block => Assert.True(block.Text.Length <= TextBlockPlanner.MaxBlockChars));
        Assert.True(blocks.Count <= TextBlockPlanner.MaxBlocks);
        Assert.DoesNotContain(blocks, block => block.Text.Trim() == "ok");
    }

    [Fact]
    public void Local_grading_never_dims_text()
    {
        var blocks = TextBlockPlanner.Group([Line("Trending now: shopping deals and gossip", 10, 10)]);
        var grading = TextBlockPlanner.LocalGrade(new TextGradingRequest("Learn about cats", "Read Behaviour", "chrome", "Cat - Wikipedia", blocks));

        Assert.True(grading.IsFallback);
        Assert.All(grading.Verdicts.Values, verdict => Assert.Equal(TextRelevance.OnTask, verdict));
    }
}
