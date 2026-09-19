using Anchor.Core.Models;
using Anchor.Infrastructure.Windows;

namespace Anchor.Infrastructure.Tests;

public sealed class WindowsFeatureAggregationTests
{
    [Fact]
    public void Input_snapshot_contains_aggregates_but_no_coordinates_or_key_values()
    {
        var sensor = new InputActivitySensor(Guid.NewGuid());
        sensor.RecordMouseDelta(3, 4);
        sensor.RecordMouseDelta(0, 12);
        sensor.RecordKeyDown(VirtualKeyCategory.Letter);
        sensor.RecordKeyDown(VirtualKeyCategory.Navigation);
        sensor.RecordScrollDelta(120);
        sensor.RecordScrollDelta(-120);

        var item = sensor.Snapshot(DateTimeOffset.UnixEpoch);

        Assert.Equal("17", item.Features["mouse_distance"]);
        Assert.Equal("2", item.Features["key_count"]);
        Assert.Equal("1", item.Features["key_letter_count"]);
        Assert.Equal("1", item.Features["key_navigation_count"]);
        Assert.Equal("1", item.Features["scroll_reversal_count"]);
        Assert.DoesNotContain(item.Features.Keys, key =>
            key.Contains("coordinate", StringComparison.OrdinalIgnoreCase)
            || key.Contains("raw", StringComparison.OrdinalIgnoreCase)
            || key.Contains("key_value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Foreground_tracker_reports_rapid_switch_count_and_redacts_titles()
    {
        var sensor = new ForegroundWindowSensor(Guid.NewGuid());
        sensor.Observe("reader", "Paper - brian@example.com", DateTimeOffset.UnixEpoch);
        sensor.Observe("browser", "News", DateTimeOffset.UnixEpoch.AddSeconds(2));
        var item = sensor.Observe("chat", "Messages", DateTimeOffset.UnixEpoch.AddSeconds(4));

        Assert.Equal("2", item.Features["app_switch_count"]);
        Assert.DoesNotContain("brian@example.com", string.Join(" ", item.Features.Values));
    }

    [Fact]
    public void Secure_window_classifier_recognizes_system_and_password_surfaces()
    {
        Assert.True(SecureWindowClassifier.IsSecure("CredentialUIBroker", "Windows Security"));
        Assert.True(SecureWindowClassifier.IsSecure("browser", "Enter password"));
        Assert.False(SecureWindowClassifier.IsSecure("WINWORD", "Research notes"));
    }

    [Fact]
    public async Task Bounded_channel_drops_stale_events_instead_of_blocking()
    {
        var sessionId = Guid.NewGuid();
        var channel = SensorEventChannel.Create(capacity: 2);
        Assert.True(channel.Writer.TryWrite(DerivedEvent.Create(sessionId, DateTimeOffset.UnixEpoch, "test", "first")));
        Assert.True(channel.Writer.TryWrite(DerivedEvent.Create(sessionId, DateTimeOffset.UnixEpoch.AddSeconds(1), "test", "second")));
        Assert.True(channel.Writer.TryWrite(DerivedEvent.Create(sessionId, DateTimeOffset.UnixEpoch.AddSeconds(2), "test", "third")));
        channel.Writer.Complete();

        var events = new List<DerivedEvent>();
        await foreach (var item in channel.Reader.ReadAllAsync())
        {
            events.Add(item);
        }

        Assert.Equal(["second", "third"], events.Select(item => item.Type));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1_500, 1.5)]
    public void Idle_duration_is_non_negative(long milliseconds, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, IdleTimeSensor.NormalizeMilliseconds(milliseconds));
    }
}
