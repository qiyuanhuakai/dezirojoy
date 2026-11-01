using System.Buffers.Binary;

namespace DeziroJoy.App.Ipc;

/// <summary>Raised when a frame's length prefix is not a usable payload size.</summary>
public sealed class FrameProtocolException : Exception
{
    public FrameProtocolException(string message)
        : base(message)
    {
    }

    public FrameProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Length-prefixed framing over any <see cref="Stream"/>: a 4-byte little-endian
/// payload length, then exactly that many payload bytes. One frame per call, so a
/// named pipe carries whole messages without the caller re-assembling them. The layer
/// is stream-agnostic on purpose - it opens no pipe and runs no loop; that is the
/// client's and the server's job.
/// </summary>
public static class PipeFraming
{
    /// <summary>Bytes of the little-endian length prefix that precedes every payload.</summary>
    public const int LengthPrefixBytes = 4;

    /// <summary>
    /// Largest payload accepted in either direction. A profile JSON is a few kilobytes,
    /// so this is generous headroom while keeping one frame's memory bounded.
    /// </summary>
    public const int MaxFramePayloadBytes = 1 << 20;

    /// <summary>
    /// Writes one frame. A payload outside 1..<see cref="MaxFramePayloadBytes"/> is
    /// refused here rather than written as a frame the reader would have to reject.
    /// </summary>
    public static async Task WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        ValidatePayloadLength(payload.Length);

        var prefix = new byte[LengthPrefixBytes];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);

        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    /// <summary>
    /// Reads exactly one frame. A length prefix outside 1..<see cref="MaxFramePayloadBytes"/>
    /// is refused deterministically - the same bytes always produce the same exception,
    /// and nothing beyond the prefix is read or allocated. A stream that ends mid-frame
    /// raises <see cref="EndOfStreamException"/>.
    /// </summary>
    public static async ValueTask<byte[]> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        var prefix = new byte[LengthPrefixBytes];
        await ReadExactlyAsync(stream, prefix, cancellationToken);

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        ValidatePayloadLength(length);

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);

        return payload;
    }

    /// <summary>
    /// The single size rule both directions share, so the writer can never emit a
    /// frame its own reader refuses.
    /// </summary>
    private static void ValidatePayloadLength(int length)
    {
        if (length <= 0 || length > MaxFramePayloadBytes)
        {
            throw new FrameProtocolException(
                $"frame payload of {length} bytes is outside 1..{MaxFramePayloadBytes}");
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (chunk == 0)
            {
                throw new EndOfStreamException("the stream ended in the middle of a frame");
            }

            read += chunk;
        }
    }
}
