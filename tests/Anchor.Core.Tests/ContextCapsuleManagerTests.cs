using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class ContextCapsuleManagerTests
{
    [Fact]
    public void Freeze_keeps_last_confident_anchor_not_distracting_window()
    {
        var sessionId = Guid.NewGuid();
        var manager = new ContextCapsuleManager(sessionId, "Compare experiment results");
        manager.Observe(new ContextObservation(
            "PDF Reader", "paper.pdf", "section 3",
            "Highlighted the control-group result",
            "Compare it with the treatment group",
            "selected sentence", "paper.pdf#page=8", 0.95, false));
        manager.Observe(new ContextObservation(
            "Browser", "social.example", "social feed",
            "Scrolled", "Keep scrolling", null, null, 0.1, false));

        var capsule = manager.Freeze(DistractionReason.AppSwitch);

        Assert.Equal("section 3", capsule.Location);
        Assert.Equal("Compare it with the treatment group", capsule.NextAction);
        Assert.Equal(sessionId, capsule.SessionId);
    }

    [Fact]
    public void Observe_redacts_sensitive_selected_text_before_capsule_creation()
    {
        var manager = new ContextCapsuleManager(Guid.NewGuid(), "Write report");
        manager.Observe(new ContextObservation(
            "Editor", "report.docx", "conclusion",
            "Drafted conclusion", "Review wording",
            "Contact brian@example.com with token sk-abcdefghijklmnopqrstuvwxyz1234",
            "report.docx", 0.9, false));

        var capsule = manager.Freeze(DistractionReason.ManualReport);

        Assert.DoesNotContain("brian@example.com", capsule.SelectedText);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz1234", capsule.SelectedText);
        Assert.Contains("[redacted-email]", capsule.SelectedText);
        Assert.Contains("[redacted-secret]", capsule.SelectedText);
    }

    [Fact]
    public void Sensitive_fields_are_never_accepted_as_anchors()
    {
        var manager = new ContextCapsuleManager(Guid.NewGuid(), "Sign in");

        var result = manager.Observe(new ContextObservation(
            "Browser", "accounts.example", "password field",
            "Typed password", "Submit", "secret", null, 1, true));

        Assert.Null(result);
        Assert.Throws<InvalidOperationException>(() => manager.Freeze(DistractionReason.ManualReport));
    }

    [Fact]
    public void Progress_tracker_counts_focus_interruptions_and_recovery_time()
    {
        var sessionId = Guid.NewGuid();
        var tracker = new ProgressTracker(sessionId);
        tracker.Apply(DerivedEvent.Create(sessionId, DateTimeOffset.UnixEpoch, "attention", "focused"));
        tracker.Apply(DerivedEvent.Create(sessionId, DateTimeOffset.UnixEpoch.AddSeconds(10), "attention", "distracted"));
        var snapshot = tracker.Apply(DerivedEvent.Create(sessionId, DateTimeOffset.UnixEpoch.AddSeconds(16), "attention", "focused"));

        Assert.Equal(10, snapshot.FocusedSeconds);
        Assert.Equal(1, snapshot.InterruptionCount);
        Assert.Equal(6, snapshot.RecoverySeconds);
    }
}
