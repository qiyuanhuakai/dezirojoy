using System.Text;

namespace DeziroJoy.App.Ipc;

/// <summary>
/// The framed transport half of <see cref="DaemonServer"/>: one client connection at a time,
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
    public async Task ServeConnectionAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var connection = new Connection(stream);
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
                var response = await ProcessAsync(request).ConfigureAwait(false);
                await connection.SendAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_connectionsGate)
            {
                _connections.Remove(connection);
            }

            connection.Dispose();
        }
    }

    /// <summary>
    /// One client connection. Writes are serialised by a semaphore so a push raised on
    /// another connection's thread can never interleave with this connection's own response.
    /// </summary>
    private sealed class Connection : IDisposable
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly Stream _stream;

        public Connection(Stream stream) => _stream = stream;

        public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            var payload = Encoding.UTF8.GetBytes(IpcSerializer.Serialize(message));

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await PipeFraming.WriteFrameAsync(_stream, payload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        /// <summary>Queues a push without blocking the thread that raised the state change.</summary>
        public void Post(IpcMessage message)
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await SendAsync(message, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // A dead peer is reaped by its own ServeConnectionAsync loop.
                    }
                },
                CancellationToken.None);
        }

        public void Dispose() => _writeGate.Dispose();
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
}
