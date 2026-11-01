using System.Buffers.Binary;
using DeziroJoy.App.Ipc;
using Xunit;

namespace DeziroJoy.App.Tests.Ipc;

/// <summary>
/// Length-prefixed framing over a plain <see cref="Stream"/>: partial, split and
/// back-to-back frames, deterministic rejection of oversize and invalid lengths, and
/// cancellation. No pipe is opened here - the framing layer is stream-agnostic, so a
/// memory stream proves the same contract a named pipe will.
/// </summary>
public class PipeFramingTests
{
    private static readonly byte[] Payload = [1, 2, 3, 4, 5];

    [Fact]
    public async Task A_Frame_Written_To_A_Stream_Reads_Back_Identically()
    {
        using var stream = new MemoryStream();

        await PipeFraming.WriteFrameAsync(stream, Payload, TestContext.Current.CancellationToken);
        stream.Position = 0;

        Assert.Equal(Payload, await PipeFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Consecutive_Frames_Are_Read_Back_In_Order()
    {
        byte[] first = [10, 11], second = [20, 21, 22], third = [30];
        using var stream = new MemoryStream();

        await PipeFraming.WriteFrameAsync(stream, first, TestContext.Current.CancellationToken);
        await PipeFraming.WriteFrameAsync(stream, second, TestContext.Current.CancellationToken);
        await PipeFraming.WriteFrameAsync(stream, third, TestContext.Current.CancellationToken);
        stream.Position = 0;

        Assert.Equal(first, await PipeFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken));
        Assert.Equal(second, await PipeFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken));
        Assert.Equal(third, await PipeFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_Frame_Delivered_One_Byte_At_A_Time_Is_Reassembled()
    {
        using var stream = new ChunkedStream(FrameOf(Payload), maxChunk: 1);

        Assert.Equal(Payload, await PipeFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_Frame_Delivered_In_Odd_Chunks_Is_Reassembled()
    {
        using var stream = new ChunkedStream(FrameOf(Payload), maxChunk: 3);

        Assert.Equal(Payload, await PipeFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_Truncated_Payload_Throws_EndOfStreamException()
    {
        var frame = FrameOf(Payload);
        using var stream = new MemoryStream(frame.AsSpan(0, frame.Length - 1).ToArray());

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => PipeFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task A_Truncated_Length_Prefix_Throws_EndOfStreamException()
    {
        using var stream = new MemoryStream([0, 0]);

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => PipeFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task A_Length_Beyond_The_Maximum_Is_Rejected()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, PipeFraming.MaxFramePayloadBytes + 1);
        using var stream = new MemoryStream(prefix);

        await Assert.ThrowsAsync<FrameProtocolException>(
            () => PipeFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task A_NonPositive_Length_Is_Rejected(int length)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        using var stream = new MemoryStream(prefix);

        await Assert.ThrowsAsync<FrameProtocolException>(
            () => PipeFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task A_Payload_At_The_Maximum_Is_Accepted()
    {
        var payload = new byte[PipeFraming.MaxFramePayloadBytes];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        using var stream = new MemoryStream();
        await PipeFraming.WriteFrameAsync(stream, payload, TestContext.Current.CancellationToken);
        stream.Position = 0;

        Assert.Equal(payload, await PipeFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Writing_A_Payload_Beyond_The_Maximum_Is_Rejected()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<FrameProtocolException>(
            () => PipeFraming.WriteFrameAsync(
                stream,
                new byte[PipeFraming.MaxFramePayloadBytes + 1],
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_Writer_Refuses_An_Empty_Payload_The_Reader_Refuses_A_Zero_Length_For()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<FrameProtocolException>(
            () => PipeFraming.WriteFrameAsync(
                stream,
                ReadOnlyMemory<byte>.Empty,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadFrame_Throws_When_The_Token_Is_Already_Canceled()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        using var stream = new MemoryStream(FrameOf(Payload));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PipeFraming.ReadFrameAsync(stream, cancellation.Token).AsTask());
    }

    [Fact]
    public async Task ReadFrame_Propagates_Cancellation_From_A_Blocked_Read()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var stream = new BlockingStream();

        var read = PipeFraming.ReadFrameAsync(stream, cancellation.Token);

        await stream.ReadEntered;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.AsTask());
    }

    private static byte[] FrameOf(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(), payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>
    /// A read-only stream that hands out at most <paramref name="maxChunk"/> bytes per
    /// read, so a framing reader that assumes one read per frame fails deterministically
    /// instead of passing by luck.
    /// </summary>
    private sealed class ChunkedStream(byte[] data, int maxChunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var chunk = Math.Min(maxChunk, Math.Min(count, data.Length - _position));
            if (chunk <= 0)
            {
                return 0;
            }

            Array.Copy(data, _position, buffer, offset, chunk);
            _position += chunk;
            return chunk;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = Math.Min(maxChunk, Math.Min(buffer.Length, data.Length - _position));
            if (chunk <= 0)
            {
                return new ValueTask<int>(0);
            }

            data.AsSpan(_position, chunk).CopyTo(buffer.Span);
            _position += chunk;
            return new ValueTask<int>(chunk);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A stream whose reads never complete until the caller's token is canceled. It
    /// signals when a read has been entered so the test cancels a read that is
    /// provably in flight instead of racing a sleep.
    /// </summary>
    private sealed class BlockingStream : Stream
    {
        private readonly TaskCompletionSource _readEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadEntered => _readEntered.Task;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            _readEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
