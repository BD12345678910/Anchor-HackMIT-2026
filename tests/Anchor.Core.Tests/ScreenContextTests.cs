using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class ScreenContextTests
{
    private static readonly TaskStep Step = new("s1", "Solve 2021 December Bronze problem 1", "Accepted submission on usaco.org");

    [Theory]
    [InlineData("Code", "main.cpp - Visual Studio Code", 40, 0, ActivityKind.Coding)]
    [InlineData("chrome", "USACO 2021 December Contest, Bronze Problem 1", 0, 1, ActivityKind.ProblemSolving)]
    [InlineData("chrome", "Cute cats compilation - YouTube", 0, 0, ActivityKind.Watching)]
    [InlineData("Acrobat", "chapter4.pdf - Adobe Acrobat", 0, 3, ActivityKind.Reading)]
    [InlineData("WINWORD", "essay.docx - Word", 25, 0, ActivityKind.Writing)]
    [InlineData("chrome", "Photosynthesis - Wikipedia", 0, 4, ActivityKind.Reading)]
    [InlineData("chrome", "Photosynthesis - Wikipedia", 30, 0, ActivityKind.Writing)]
    [InlineData("chrome", "Photosynthesis - Wikipedia", 0, 0, ActivityKind.Browsing)]
    public void ActivityClassifier_uses_process_title_and_input_pattern(
        string process, string title, int keys, int reversals, ActivityKind expected)
    {
        Assert.Equal(expected, ActivityClassifier.Infer(process, title, keys, reversals, mouseDistance: 0));
    }

    [Fact]
    public void Analyzer_prefers_gaze_then_caret_then_pointer_then_viewport()
    {
        var lines = new List<ScreenLine>
        {
            new("First line of the page", 10, 10, 300, 16),
            new("Second line that the eyes are on", 10, 40, 300, 16),
            new("Third line under the caret", 10, 70, 300, 16),
            new("Fourth line near the pointer", 10, 100, 300, 16)
        };
        var now = DateTimeOffset.UtcNow;

        var gaze = ScreenSnapshotAnalyzer.Build(now, "chrome", "Page", lines, (150, 48), (150, 78), (150, 108), 800, 600);
        var caret = ScreenSnapshotAnalyzer.Build(now, "chrome", "Page", lines, null, (150, 78), (150, 108), 800, 600);
        var pointer = ScreenSnapshotAnalyzer.Build(now, "chrome", "Page", lines, null, null, (150, 108), 800, 600);
        var viewport = ScreenSnapshotAnalyzer.Build(now, "chrome", "Page", lines, null, null, null, 800, 600);

        Assert.Equal(FocusSource.Gaze, gaze.FocusSource);
        Assert.Equal("Second line that the eyes are on", gaze.FocusLine!.Text);
        Assert.Equal(FocusSource.Caret, caret.FocusSource);
        Assert.Equal("Third line under the caret", caret.FocusLine!.Text);
        Assert.Equal(FocusSource.Pointer, pointer.FocusSource);
        Assert.Equal("Fourth line near the pointer", pointer.FocusLine!.Text);
        Assert.Equal(FocusSource.Viewport, viewport.FocusSource);
        Assert.NotNull(viewport.FocusLine);
    }

    [Fact]
    public void Analyzer_bounds_excerpt_and_flattened_text()
    {
        var lines = Enumerable.Range(0, 400)
            .Select(i => new ScreenLine(new string('x', 60) + i, 0, i * 20, 500, 16))
            .ToList();

        var snapshot = ScreenSnapshotAnalyzer.Build(DateTimeOffset.UtcNow, "chrome", "Page", lines, null, null, (10, 4000), 800, 8000);

        Assert.True(snapshot.Excerpt.Length <= ScreenSnapshotAnalyzer.MaxExcerptChars);
        Assert.True(ScreenSnapshotAnalyzer.FlattenText(lines).Length <= ScreenSnapshotAnalyzer.MaxScreenTextChars);
        Assert.Equal(16, snapshot.ContentHash.Length);
        Assert.Equal(snapshot.ContentHash, ScreenSnapshotAnalyzer.Build(DateTimeOffset.UtcNow.AddMinutes(1), "chrome", "Page", lines, null, null, (10, 4000), 800, 8000).ContentHash);
    }

    [Fact]
    public void LocalProgressJudge_suggests_but_never_auto_completes()
    {
        var evidence = new ProgressEvidence(
            "do 3 usaco problems", [Step], 0, Step,
            "chrome", "USACO 2021 December Bronze Problem 1",
            "Problem 1. Result: Accepted! All test cases passed. Bronze December 2021",
            ActivityKind.ProblemSolving, []);

        var judgment = LocalProgressJudge.Judge(evidence);

        Assert.True(judgment.StepCompleted);
        Assert.True(judgment.IsFallback);
        Assert.Equal("Local rules", judgment.Source);
        Assert.True(judgment.Confidence >= ProgressJudgment.SuggestThreshold);
        Assert.True(judgment.Confidence < ProgressJudgment.AutoCompleteThreshold);
    }

    [Theory]
    [InlineData("Result: Wrong Answer on test 3. Bronze December 2021 Problem 1")]
    [InlineData("Problem 1 statement. Bronze December 2021. Submit your solution.")]
    [InlineData("Accepted! Nothing here matches the step at all.")]
    public void LocalProgressJudge_rejects_failures_and_unrelated_screens(string screenText)
    {
        var evidence = new ProgressEvidence(
            "do 3 usaco problems", [Step], 0, Step, "chrome", "Some page", screenText, ActivityKind.ProblemSolving, []);

        Assert.False(LocalProgressJudge.Judge(evidence).StepCompleted);
    }

    [Fact]
    public void LocalContextReminder_varies_with_activity()
    {
        var reading = Capsule(ActivityKind.Reading, FocusSource.Gaze, "The mitochondria is the powerhouse of the cell.");
        var coding = Capsule(ActivityKind.Coding, FocusSource.Caret, "for (int i = 0; i < n; i++) {");
        var solving = Capsule(ActivityKind.ProblemSolving, FocusSource.Pointer, "Farmer John has N cows");
        var watching = Capsule(ActivityKind.Watching, FocusSource.None, null);

        var r = LocalContextReminder.Compose(reading);
        var c = LocalContextReminder.Compose(coding);
        var s = LocalContextReminder.Compose(solving);
        var w = LocalContextReminder.Compose(watching);

        Assert.StartsWith("You were reading", r.Headline);
        Assert.Contains("your eyes were on", r.WhereYouWere);
        Assert.Contains("mitochondria", r.WhereYouWere);
        Assert.StartsWith("You were coding", c.Headline);
        Assert.Contains("for (int i", c.WhereYouWere);
        Assert.StartsWith("You were working on", s.Headline);
        Assert.Contains("Farmer John", s.WhereYouWere);
        Assert.StartsWith("You were watching", w.Headline);
        Assert.All(new[] { r, c, s, w }, reminder =>
        {
            Assert.True(reminder.IsFallback);
            Assert.Equal("Local recall", reminder.Source);
            Assert.False(string.IsNullOrWhiteSpace(reminder.ResumeWith));
        });
        Assert.Equal(4, new[] { r.Headline, c.Headline, s.Headline, w.Headline }.Distinct().Count());
    }

    [Fact]
    public void CapsuleManager_redacts_and_keeps_screen_fields()
    {
        var manager = new ContextCapsuleManager(Guid.NewGuid(), "Write report");
        manager.Observe(new ContextObservation(
            "Word", "report.docx", "page 2", "Typing", "Continue", null, null, 0.9, false,
            Activity: ActivityKind.Writing,
            FocusText: "Send it to brian@example.com tomorrow",
            FocusSource: FocusSource.Caret,
            ScreenExcerpt: new string('a', 5_000),
            KeyCount: 42,
            ScrollReversalCount: 1));

        var capsule = manager.Freeze(DistractionReason.AppSwitch);

        Assert.Equal(ActivityKind.Writing, capsule.Activity);
        Assert.Equal(FocusSource.Caret, capsule.FocusSource);
        Assert.DoesNotContain("brian@example.com", capsule.FocusText);
        Assert.True(capsule.ScreenExcerpt!.Length <= 1_600);
        Assert.Equal(42, capsule.KeyCount);
    }

    private static ContextCapsule Capsule(ActivityKind activity, FocusSource source, string? focus) =>
        new(
            Guid.NewGuid(), DateTimeOffset.UtcNow, "Study biology", "chrome", "Chapter 4 - Cells",
            "page 3", "Reading", "Finish chapter 4", null, null, DistractionReason.AppSwitch,
            "Finish chapter 4", "matched", DateTimeOffset.UtcNow, false,
            activity, focus, source, focus, 12, 2);
}
