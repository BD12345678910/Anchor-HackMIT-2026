using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Anchor.Infrastructure.Browser;

namespace Anchor.Infrastructure.Tests;

public sealed class NativeBridgeServerTests
{
    [Fact]
    public async Task Fragmented_native_frame_reads_one_exact_json_message()
    {
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"version\":1}");
        var framed = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(framed, payload.Length);
        payload.CopyTo(framed.AsSpan(4));
        await using var stream = new FragmentedReadStream(framed, 2);

        var message = await NativeMessageFraming.ReadAsync(stream);

        Assert.Equal("{\"type\":\"hello\",\"version\":1}", message);
    }

    [Fact]
    public async Task Oversized_native_frame_is_rejected_before_allocating_payload()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 1_048_577);
        await using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeMessageFraming.ReadAsync(stream));
    }

    [Fact]
    public void Reconnected_client_receives_latest_toolkit_snapshot()
    {
        var snapshots = new NativeBridgeSnapshotStore();
        snapshots.Update("toolkitState", "{\"type\":\"toolkitState\",\"imageBlur\":true}");

        var first = snapshots.GetForReconnect();
        var second = snapshots.GetForReconnect();

        Assert.Equal(first, second);
        Assert.Contains("\"imageBlur\":true", second.Single());
    }

    [Fact]
    public async Task Connected_client_can_wait_for_a_new_snapshot_without_polling()
    {
        var snapshots = new NativeBridgeSnapshotStore();
        var initial = snapshots.GetSnapshot();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var pending = snapshots.WaitForChangeAsync(initial.Revision, timeout.Token);
        snapshots.Update("toolkitState", "{\"type\":\"toolkitState\",\"imageBlur\":true}");
        var updated = await pending;

        Assert.True(updated.Revision > initial.Revision);
        Assert.Contains("\"imageBlur\":true", updated.Messages.Single());
    }

    [Fact]
    public async Task Authenticated_pipe_exchanges_live_snapshots_and_page_context()
    {
        var endpoint = Path.Combine(Path.GetTempPath(), $"anchor-bridge-{Guid.NewGuid():N}.json");
        await using var server = new NativeBridgeServer(endpoint);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MessageReceived += (_, message) => received.TrySetResult(message);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var client = new NamedPipeClientStream(
            ".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await client.ConnectAsync(timeout.Token);
        await NativeMessageFraming.WriteAsync(client, JsonSerializer.Serialize(new
        {
            type = "hello",
            token = server.Token,
        }), timeout.Token);

        server.UpdateSnapshot("toolkitState", "{\"type\":\"toolkitState\",\"futureTextMask\":true}");
        var snapshot = await NativeMessageFraming.ReadAsync(client, timeout.Token);
        await NativeMessageFraming.WriteAsync(client, "{\"type\":\"pageContext\",\"title\":\"USACO Guide\"}", timeout.Token);

        Assert.Contains("\"futureTextMask\":true", snapshot);
        Assert.Contains("USACO Guide", await received.Task.WaitAsync(timeout.Token));
    }

    private sealed class FragmentedReadStream(byte[] bytes, int maximumChunk) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(Span<byte> buffer) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Math.Min(Math.Min(maximumChunk, buffer.Length), bytes.Length - _position);
            if (count <= 0) return ValueTask.FromResult(0);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
