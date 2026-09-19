using Anchor.Core.Models;
using Anchor.Infrastructure.Persistence;

namespace Anchor.Infrastructure.Tests;

public sealed class SqliteEventStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"anchor-tests-{Guid.NewGuid():N}");
    private SqliteEventStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _store = new SqliteEventStore(Path.Combine(_directory, "events.db"));
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Append_and_query_preserve_timestamp_order()
    {
        var sessionId = Guid.NewGuid();
        await _store.AppendAsync(DerivedEvent.Create(
            sessionId, DateTimeOffset.UnixEpoch.AddSeconds(2), "window", "changed",
            new Dictionary<string, string> { ["process"] = "reader" }));
        await _store.AppendAsync(DerivedEvent.Create(
            sessionId, DateTimeOffset.UnixEpoch.AddSeconds(1), "attention", "focused"));

        var events = await _store.QuerySessionAsync(sessionId);

        Assert.Equal(["focused", "changed"], events.Select(item => item.Type));
    }

    [Fact]
    public async Task Sensitive_feature_names_and_values_are_not_persisted()
    {
        var sessionId = Guid.NewGuid();
        await _store.AppendAsync(DerivedEvent.Create(
            sessionId, DateTimeOffset.UnixEpoch, "keyboard", "activity",
            new Dictionary<string, string>
            {
                ["key_count"] = "4",
                ["raw_key"] = "hunter2",
                ["selected_text"] = "brian@example.com"
            }));

        var stored = Assert.Single(await _store.QuerySessionAsync(sessionId));

        Assert.Equal("4", stored.Features["key_count"]);
        Assert.False(stored.Features.ContainsKey("raw_key"));
        Assert.DoesNotContain("brian@example.com", stored.Features.Values);
    }

    [Fact]
    public async Task Delete_session_does_not_delete_other_sessions()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await _store.AppendAsync(DerivedEvent.Create(first, DateTimeOffset.UnixEpoch, "test", "first"));
        await _store.AppendAsync(DerivedEvent.Create(second, DateTimeOffset.UnixEpoch, "test", "second"));

        await _store.DeleteSessionAsync(first);

        Assert.Empty(await _store.QuerySessionAsync(first));
        Assert.Single(await _store.QuerySessionAsync(second));
    }

    [Fact]
    public async Task Delete_all_removes_every_event()
    {
        var sessionId = Guid.NewGuid();
        await _store.AppendAsync(DerivedEvent.Create(sessionId, DateTimeOffset.UnixEpoch, "test", "event"));

        await _store.DeleteAllAsync();

        Assert.Empty(await _store.QuerySessionAsync(sessionId));
    }

    [Fact]
    public async Task Cancelled_append_does_not_write()
    {
        var sessionId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.AppendAsync(
                DerivedEvent.Create(sessionId, DateTimeOffset.UnixEpoch, "test", "cancelled"),
                cancellation.Token));

        Assert.Empty(await _store.QuerySessionAsync(sessionId));
    }
}
