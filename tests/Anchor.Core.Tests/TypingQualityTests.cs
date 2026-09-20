using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public class TypingQualityTests
{
    [Fact]
    public void Prose_reads_as_words()
    {
        var quality = TypingQualityAnalyzer.Assess(
            "The mitochondria produce most of the chemical energy the cell needs.");

        Assert.False(quality.IsGibberish);
    }

    [Fact]
    public void Source_code_is_not_gibberish()
    {
        var quality = TypingQualityAnalyzer.Assess(
            "for (var i = 0; i < nodes.Length; i++) { visitNode(nodes[i], depth + 1); }");

        Assert.False(quality.IsGibberish);
    }

    [Fact]
    public void Keyboard_mashing_is_gibberish()
    {
        var quality = TypingQualityAnalyzer.Assess("asdkjh sdlkfjh qwkjhds lkjhsdf mnbvcxz");

        Assert.True(quality.IsGibberish);
    }

    [Fact]
    public void A_held_key_is_gibberish()
    {
        var quality = TypingQualityAnalyzer.Assess("aaaaaaaaaa jjjjjjjj kkkkkkkk llllllll");

        Assert.True(quality.IsGibberish);
    }

    [Fact]
    public void A_couple_of_characters_are_not_judged()
    {
        var quality = TypingQualityAnalyzer.Assess("hm ok");

        Assert.False(quality.IsGibberish);
    }

    [Fact]
    public void Thinking_pause_while_coding_is_not_distraction()
    {
        var machine = AttentionStateMachine.CreateDefault();

        var prediction = machine.Update(SensorWindow.Create(
            keyCount: 0,
            mouseDistance: 0,
            idleSeconds: 25,
            appRelevance: 0.9,
            progressObserved: false,
            noProgressSustained: true,
            activity: ActivityKind.Coding));

        Assert.DoesNotContain("idle_pause", prediction.ReasonCodes);
        Assert.DoesNotContain("no_progress_sustained", prediction.ReasonCodes);
        Assert.Equal(AttentionState.Focused, prediction.State);
    }

    [Fact]
    public void The_same_pause_while_browsing_still_counts()
    {
        var machine = AttentionStateMachine.CreateDefault();

        var prediction = machine.Update(SensorWindow.Create(
            keyCount: 0,
            mouseDistance: 0,
            idleSeconds: 25,
            appRelevance: 0.9,
            progressObserved: false,
            noProgressSustained: true,
            activity: ActivityKind.Browsing));

        Assert.Contains("idle_pause", prediction.ReasonCodes);
    }

    [Fact]
    public void A_long_enough_pause_while_coding_is_still_noticed()
    {
        var machine = AttentionStateMachine.CreateDefault();

        var prediction = machine.Update(SensorWindow.Create(
            keyCount: 0,
            mouseDistance: 0,
            idleSeconds: 90,
            appRelevance: 0.9,
            activity: ActivityKind.Writing));

        Assert.Contains("idle_pause", prediction.ReasonCodes);
    }

    [Fact]
    public void Gibberish_typing_is_reported_even_on_a_relevant_window()
    {
        var machine = AttentionStateMachine.CreateDefault();

        var prediction = machine.Update(SensorWindow.Create(
            keyCount: 40,
            mouseDistance: 0,
            idleSeconds: 0,
            appRelevance: 0.9,
            activity: ActivityKind.Writing,
            gibberishTyping: true));

        Assert.Contains("gibberish_typing", prediction.ReasonCodes);
        Assert.NotEqual(AttentionState.Focused, prediction.State);
    }

    [Fact]
    public void Real_writing_on_a_relevant_window_stays_focused()
    {
        var machine = AttentionStateMachine.CreateDefault();

        var prediction = machine.Update(SensorWindow.Create(
            keyCount: 40,
            mouseDistance: 0,
            idleSeconds: 0,
            appRelevance: 0.9,
            activity: ActivityKind.Writing));

        Assert.DoesNotContain("gibberish_typing", prediction.ReasonCodes);
        Assert.Equal(AttentionState.Focused, prediction.State);
    }

    [Fact]
    public void Fusion_flags_mashed_text_and_leaves_prose_alone()
    {
        var mashed = new AttentionFusion().Apply(AttentionEvidence.At(
            DateTimeOffset.UnixEpoch,
            keyCount: 30,
            adapterRelevance: 0.9,
            activity: ActivityKind.Writing,
            typedText: "asdkjh sdlkfjh qwkjhds lkjhsdf"));
        var prose = new AttentionFusion().Apply(AttentionEvidence.At(
            DateTimeOffset.UnixEpoch,
            keyCount: 30,
            adapterRelevance: 0.9,
            activity: ActivityKind.Writing,
            typedText: "the second paragraph explains why the sample size was small"));

        Assert.True(mashed.Window.GibberishTyping);
        Assert.Contains("gibberish_typing", mashed.Prediction.ReasonCodes);
        Assert.False(prose.Window.GibberishTyping);
    }

    [Fact]
    public void Recovery_card_for_mashing_points_back_at_the_line()
    {
        var capsule = new ContextCapsule(
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            "write the solver",
            "Code.exe",
            "solver.py",
            "line 42",
            "typing",
            "finish the loop",
            SelectedText: null,
            RestoreTarget: null,
            Reason: DistractionReason.GibberishTyping,
            CurrentSubtask: "finish the loop",
            Activity: ActivityKind.Coding,
            FocusText: "for row in grid:");

        var reminder = LocalContextReminder.Compose(capsule);

        Assert.Contains("solver.py", reminder.Headline, StringComparison.Ordinal);
        Assert.Contains("for row in grid:", reminder.WhereYouWere, StringComparison.Ordinal);
        Assert.Contains("finish the loop", reminder.ResumeWith, StringComparison.Ordinal);
    }
}
