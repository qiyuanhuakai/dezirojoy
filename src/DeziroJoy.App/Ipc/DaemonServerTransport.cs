using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

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

                    handshakeAccepted = true;
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
    /// One client connection. Writes are serialised by a semaphore so a push raised on
    /// another connection's thread can never interleave with this connection's own response.
    /// </summary>
    private sealed class Connection : IDisposable
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly Stream _stream;

        public string Id { get; } = Guid.NewGuid().ToString("N");

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
