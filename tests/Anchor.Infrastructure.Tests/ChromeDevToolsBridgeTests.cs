using System.Text.Json;
using Anchor.Infrastructure.Browser;

namespace Anchor.Infrastructure.Tests;

public sealed class ChromeDevToolsBridgeTests
{
    [Fact]
    public void Edit_state_carries_the_task_keywords_into_the_page()
    {
        var state = ChromeDevToolsBridge.BuildState(new PageEditRequest(
            PixelateOffTaskPictures: true,
            DeleteOffTaskBlocks: true,
            SimplifySentences: false,
            Threshold: 0.4,
            MaxWords: 20,
            Keywords: ["usaco", "dynamic", "programming"]));

        using var document = JsonDocument.Parse(state);
        var root = document.RootElement;
        Assert.True(root.GetProperty("imageBlur").GetBoolean());
        Assert.True(root.GetProperty("clutterRemoval").GetBoolean());
        Assert.False(root.GetProperty("simplifyText").GetBoolean());
        Assert.Equal(20, root.GetProperty("maxWords").GetInt32());
        Assert.Equal(3, root.GetProperty("keywords").GetArrayLength());
    }

    [Fact]
    public void Turning_the_tools_off_asks_for_no_edits()
    {
        var state = ChromeDevToolsBridge.BuildState(PageEditRequest.Off);

        using var document = JsonDocument.Parse(state);
        Assert.False(document.RootElement.GetProperty("imageBlur").GetBoolean());
        Assert.Equal(0, document.RootElement.GetProperty("keywords").GetArrayLength());
    }

    [Fact]
    public void Page_reading_is_translated_into_the_messages_the_tracker_understands()
    {
        var messages = ChromeDevToolsBridge.ParseContextMessages(
            """{"origin":"https://usaco.org/index.php?page=problems","title":"USACO Problems","progress":0.42}""");
        var tracker = new BrowserContextTracker();

        Assert.All(messages, message => Assert.True(tracker.Apply(message)));

        var snapshot = tracker.Snapshot();
        Assert.NotNull(snapshot);
        Assert.Equal("https://usaco.org", snapshot.Origin);
        Assert.Equal("USACO Problems", snapshot.Title);
        Assert.Equal(0.42, snapshot.Progress, 3);
    }

    [Fact]
    public void An_evaluate_reply_without_a_value_reads_as_nothing()
    {
        Assert.Null(ChromeDevToolsBridge.ReadEvaluateValue("""{"id":1,"result":{"result":{"type":"undefined"}}}"""));
        Assert.Equal(
            "hello",
            ChromeDevToolsBridge.ReadEvaluateValue("""{"id":1,"result":{"result":{"type":"string","value":"hello"}}}"""));
    }
}
