using Anchor.Infrastructure.Browser;

namespace Anchor.Infrastructure.Tests;

public sealed class BrowserContextTrackerTests
{
    [Fact]
    public void Page_and_progress_messages_build_a_coarse_restore_anchor()
    {
        var tracker = new BrowserContextTracker();
        tracker.Apply("{\"source\":\"anchor-content\",\"event\":{\"type\":\"page-context\",\"origin\":\"https://usaco.guide\",\"title\":\"Silver Guide\"}}");
        tracker.Apply("{\"source\":\"anchor-content\",\"event\":{\"type\":\"reading-progress\",\"progress\":0.42,\"paragraphIndex\":7}}");

        var snapshot = tracker.Snapshot();

        Assert.Equal("https://usaco.guide", snapshot?.Origin);
        Assert.Equal("Silver Guide", snapshot?.Title);
        Assert.Equal(0.42, snapshot?.Progress);
        Assert.Equal(7, snapshot?.ParagraphIndex);
    }

    [Fact]
    public void Invalid_or_non_http_restore_targets_are_rejected()
    {
        var tracker = new BrowserContextTracker();

        Assert.False(tracker.Apply("{\"source\":\"anchor-content\",\"event\":{\"type\":\"page-context\",\"origin\":\"file:///secret.txt\",\"title\":\"Secret\"}}"));
        Assert.Null(tracker.Snapshot());
    }
}
