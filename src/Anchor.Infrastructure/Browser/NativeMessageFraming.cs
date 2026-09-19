using System.Buffers.Binary;
using System.Text;

namespace Anchor.Infrastructure.Browser;

public static class NativeMessageFraming
{
    public const int MaximumMessageBytes = 1_048_576;

    public static async Task<string?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        var headerRead = await ReadExactAsync(stream, header, allowCleanEnd: true, cancellationToken);
        if (!headerRead)
        {
            return null;
        }
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumMessageBytes)
        {
            throw new InvalidDataException($"Native message length {length} is invalid.");
        }
        var payload = new byte[length];
        await ReadExactAsync(stream, payload, allowCleanEnd: false, cancellationToken);
        return new UTF8Encoding(false, true).GetString(payload);
    }

    public static async Task WriteAsync(
        Stream stream,
        string json,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("Native message exceeds the maximum size.");
        }
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<bool> ReadExactAsync(
        Stream stream,
        Memory<byte> destination,
        bool allowCleanEnd,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await stream.ReadAsync(destination[read..], cancellationToken);
            if (count == 0)
            {
                if (allowCleanEnd && read == 0)
                {
                    return false;
                }
                throw new EndOfStreamException("Native message ended before its declared length.");
            }
            read += count;
        }
        return true;
    }
}

public sealed class NativeBridgeSnapshotStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _snapshots = new(StringComparer.Ordinal);
    private long _revision;
    private TaskCompletionSource<long> _changed = CreateChangedSource();

    public void Update(string type, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        TaskCompletionSource<long> changed;
        long revision;
        lock (_gate)
        {
            _snapshots[type] = json;
            revision = ++_revision;
            changed = _changed;
            _changed = CreateChangedSource();
        }
        changed.TrySetResult(revision);
    }

    public IReadOnlyList<string> GetForReconnect()
        => GetSnapshot().Messages;

    public NativeBridgeSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return GetSnapshotUnsafe();
        }
    }

    public async Task<NativeBridgeSnapshot> WaitForChangeAsync(
        long afterRevision,
        CancellationToken cancellationToken = default)
    {
        Task<long> changed;
        lock (_gate)
        {
            if (_revision > afterRevision)
            {
                return GetSnapshotUnsafe();
            }
            changed = _changed.Task;
        }
        await changed.WaitAsync(cancellationToken);
        return GetSnapshot();
    }

    private static TaskCompletionSource<long> CreateChangedSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private NativeBridgeSnapshot GetSnapshotUnsafe()
    {
        var messages = _snapshots
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => item.Value)
            .ToArray();
        return new NativeBridgeSnapshot(_revision, messages);
    }
}

public sealed record NativeBridgeSnapshot(long Revision, IReadOnlyList<string> Messages);
