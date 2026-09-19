using System.Text.Json;
using Anchor.Core.Models;
using Anchor.Core.Services;
using Microsoft.Data.Sqlite;

namespace Anchor.Infrastructure.Persistence;

public sealed class SqliteEventStore : IEventStore, IAsyncDisposable
{
    private const int MaximumPayloadBytes = 65_536;
    private readonly string _connectionString;
    private bool _disposed;

    public SqliteEventStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS events (
                id TEXT PRIMARY KEY NOT NULL,
                session_id TEXT NOT NULL,
                timestamp_ms INTEGER NOT NULL,
                source TEXT NOT NULL,
                type TEXT NOT NULL,
                features_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_events_session_timestamp
                ON events(session_id, timestamp_ms, id);
            PRAGMA user_version=1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendAsync(DerivedEvent item, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = JsonSerializer.Serialize(item.Features);
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes)
        {
            throw new ArgumentException("Event feature payload exceeds the storage limit.", nameof(item));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO events(id, session_id, timestamp_ms, source, type, features_json)
            VALUES ($id, $sessionId, $timestamp, $source, $type, $features);
            """;
        command.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        command.Parameters.AddWithValue("$sessionId", item.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$timestamp", item.Timestamp.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$source", item.Source);
        command.Parameters.AddWithValue("$type", item.Type);
        command.Parameters.AddWithValue("$features", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DerivedEvent>> QuerySessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, timestamp_ms, source, type, features_json
            FROM events
            WHERE session_id = $sessionId
            ORDER BY timestamp_ms ASC, id ASC;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString("D"));

        var result = new List<DerivedEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var features = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4))
                ?? new Dictionary<string, string>();
            result.Add(new DerivedEvent(
                Guid.Parse(reader.GetString(0)),
                sessionId,
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.GetString(2),
                reader.GetString(3),
                features));
        }

        return result;
    }

    public Task DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        ExecuteDeleteAsync("DELETE FROM events WHERE session_id = $sessionId;", sessionId, cancellationToken);

    public Task DeleteAllAsync(CancellationToken cancellationToken = default) =>
        ExecuteDeleteAsync("DELETE FROM events;", null, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private async Task ExecuteDeleteAsync(
        string sql,
        Guid? sessionId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = sql;
        if (sessionId is not null)
        {
            command.Parameters.AddWithValue("$sessionId", sessionId.Value.ToString("D"));
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
