using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace DeziroJoy.App.Ipc;

/// <summary>
/// The framed transport half of <see cref="DaemonServer"/>: one loop per client connection,
/// over any stream. Kept apart from the protocol logic so the contract stays testable with a
/// fake <see cref="IDaemonHost"/> and no pipe, while this half is exercised through a scripted
/// stream instead.
/// </summary>
public sealed partial class DaemonServer
{
    private readonly List<Connection> _connections = [];

    private readonly Lock _connectionsGate = new();

    /// <summary>
    /// Serves one client connection over a framed stream: read a request, answer it, repeat
    /// until the peer disconnects. A disconnect only ends this loop - it never stops the
    /// engine, releases the mutex or touches the daemon's state.
    /// </summary>
    public Task ServeConnectionAsync(Stream stream, CancellationToken cancellationToken = default)
        => ServeConnectionAsync(stream, peerSessionId: null, cancellationToken);

    public async Task ServeConnectionAsync(
        Stream stream,
        int? peerSessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var connection = new Connection(stream);
        var handshakeAccepted = false;
        lock (_connectionsGate)
        {
            _connections.Add(connection);
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] frame;
                try
                {
                    frame = await PipeFraming.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    break;
                }

                var request = IpcSerializer.Deserialize(Encoding.UTF8.GetString(frame));
                if (!handshakeAccepted)
                {
                    if (request is not HelloMessage hello)
                    {
                        await connection.SendAsync(
                            new ErrorMessage(string.Empty, "handshake-required", "send a valid hello before any control request"),
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (ResolvePeerSessionId(stream, peerSessionId) is { } actualSessionId)
                    {
                        hello = hello with { SessionId = actualSessionId };
                    }
                    else if (OperatingSystem.IsWindows() && stream is NamedPipeServerStream)
                    {
                        hello = hello with { SessionId = -1 };
                    }

                    var helloResponse = await ProcessAsync(hello, connection.Id).ConfigureAwait(false);
                    await connection.SendAsync(helloResponse, cancellationToken).ConfigureAwait(false);
                    if (helloResponse is not HelloAckMessage { Accepted: true })
                    {
                        return;
                    }

                    connection.InputSamplesEnabled = hello.SubscribeInputSamples;
                    handshakeAccepted = true;
                    if (connection.InputSamplesEnabled)
                    {
                        ResetInputSampleBaselines();
                        _host.SamplePresence();
                    }

                    continue;
                }

                if (request is HelloMessage)
                {
                    await connection.SendAsync(
                        new ErrorMessage(string.Empty, "already-handshaken", "a connection can perform only one hello"),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                var response = await ProcessAsync(request, connection.Id).ConfigureAwait(false);
                await connection.SendAsync(response, cancellationToken).ConfigureAwait(false);
                if (request is ShutdownDaemonMessage && response is MutationAckMessage { Accepted: true })
                {
                    RequestShutdown();
                    return;
                }
            }
        }
        finally
        {
            ReleaseConnection(connection.Id);
            lock (_connectionsGate)
            {
                _connections.Remove(connection);
            }

            connection.Dispose();
        }
    }

    private static int? ResolvePeerSessionId(Stream stream, int? peerSessionId)
    {
        if (peerSessionId.HasValue)
        {
            return peerSessionId;
        }

        if (!OperatingSystem.IsWindows() || stream is not NamedPipeServerStream pipe)
        {
            return null;
        }

        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var processId))
        {
            return null;
        }

        try
        {
            using var client = System.Diagnostics.Process.GetProcessById(checked((int)processId));
            return client.SessionId;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);

    /// <summary>
    /// One client connection with a single ordered writer. Responses and pushed snapshots
    /// share the same queue, so concurrent publishers cannot reorder sequence numbers. Only
    /// consecutive input samples with identical digital buttons coalesce: analog traffic cannot
    /// build a backlog, while every press and release transition remains observable.
    /// </summary>
    private sealed class Connection : IDisposable
    {
        private readonly Channel<Outbound> _outbound = Channel.CreateUnbounded<Outbound>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly object _inputGate = new();
        private readonly Stream _stream;
        private readonly Task _writer;
        private InputSampleSlot? _lastQueuedInputSample;

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public volatile bool InputSamplesEnabled;

        public Connection(Stream stream)
        {
            _stream = stream;
            _writer = Task.Run(WriteLoopAsync, CancellationToken.None);
        }

        public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_outbound.Writer.TryWrite(new Outbound(message, completion, cancellationToken)))
            {
                throw new EndOfStreamException();
            }

            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Post(IpcMessage message)
            => _outbound.Writer.TryWrite(new Outbound(message, null, CancellationToken.None));

        public void PostInputSample(InputSampleMessage message)
        {
            Outbound outbound;
            lock (_inputGate)
            {
                if (_lastQueuedInputSample is { } queued && queued.HasSameDigitalState(message))
                {
                    queued.Message = message;
                    return;
                }

                var slot = new InputSampleSlot(message);
                _lastQueuedInputSample = slot;
                outbound = Outbound.ForInputSample(slot);
            }

            _outbound.Writer.TryWrite(outbound);
        }

        private InputSampleMessage TakeInputSample(InputSampleSlot slot)
        {
            lock (_inputGate)
            {
                if (ReferenceEquals(_lastQueuedInputSample, slot))
                {
                    _lastQueuedInputSample = null;
                }

                return slot.Message;
            }
        }

        private async Task WriteLoopAsync()
        {
            await foreach (var item in _outbound.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var message = item.InputSample is { } slot ? TakeInputSample(slot) : item.Message;
                if (message is null)
                {
                    continue;
                }

                try
                {
                    var payload = Encoding.UTF8.GetBytes(IpcSerializer.Serialize(message));
                    await PipeFraming.WriteFrameAsync(_stream, payload, item.CancellationToken).ConfigureAwait(false);
                    item.Completion?.TrySetResult();
                }
                catch (Exception exception)
                {
                    item.Completion?.TrySetException(exception);
                    // Keep draining: a response queued behind a failed push must also
                    // complete, or ServeConnectionAsync would wait on it forever.
                }
            }
        }

        public void Dispose() => _outbound.Writer.TryComplete();

        private sealed class InputSampleSlot(InputSampleMessage message)
        {
            public InputSampleMessage Message { get; set; } = message;

            public bool HasSameDigitalState(InputSampleMessage other)
                => Message.PressedButtons == other.PressedButtons
                    && (Message.ExtraButtons ?? Array.Empty<int>()).SequenceEqual(
                        other.ExtraButtons ?? Array.Empty<int>());
        }

        private sealed record Outbound(
            IpcMessage? Message,
            TaskCompletionSource? Completion,
            CancellationToken CancellationToken,
            InputSampleSlot? InputSample = null)
        {
            public static Outbound ForInputSample(InputSampleSlot slot)
                => new(null, null, CancellationToken.None, slot);
        }
    }

    private void PublishToConnections(StateChangedMessage message)
    {
        List<Connection> connections;
        lock (_connectionsGate)
        {
            connections = [.. _connections];
        }

        foreach (var connection in connections)
        {
            connection.Post(message);
        }
    }

    private void PublishInputSample(InputSampleMessage message)
    {
        List<Connection> connections;
        lock (_connectionsGate)
        {
            connections = [.. _connections];
        }

        foreach (var connection in connections)
        {
            if (connection.InputSamplesEnabled)
            {
                connection.PostInputSample(message);
            }
        }
    }
}
