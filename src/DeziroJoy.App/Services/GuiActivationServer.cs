using System.IO.Pipes;
using System.Text.Json;
using DeziroJoy.App.Ipc;

namespace DeziroJoy.App.Services;

/// <summary>
/// The GUI-only activation channel: how a second launch asks the first GUI to show itself.
/// Deliberately separate from <see cref="IpcContract.PipeName"/> - the elevated daemon's
/// mapping control channel - so GUI instance arbitration never depends on, or disturbs, the
/// daemon. Both names carry the runtime identity (session on Windows, user on Linux) so a
/// GUI only ever talks to a peer of its own user/session.
/// </summary>
public static class GuiActivationContract
{
    /// <summary>The Show/ack protocol this assembly speaks; bumped only on a breaking change.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Prefix of the activation pipe name; the runtime identity is appended.</summary>
    public const string PipeNamePrefix = "DeziroJoy.Gui.Activation";
}

/// <summary>
/// The runtime identity of one GUI: the instance lock and the activation pipe that belong to
/// the same user/session. Windows scopes both to the logon session (a second RDP logon is a
/// separate instance by design); Linux scopes both to the user.
/// </summary>
public sealed record GuiInstanceIdentity(string LockPath, string PipeName)
{
    public static GuiInstanceIdentity ForCurrentProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            // LocalApplicationData, never the roaming profile: the lock is machine-local
            // state and must not be synced. The session id keeps the lock - and the pipe -
            // scoped to one logon session.
            var session = IpcSession.Current;
            return new GuiInstanceIdentity(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeziroJoy",
                    $"gui-{session}.lock"),
                $"{GuiActivationContract.PipeNamePrefix}.{session}");
        }

        var user = Environment.UserName;
        return new GuiInstanceIdentity(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeziroJoy",
                "gui.lock"),
            $"{GuiActivationContract.PipeNamePrefix}.{user}");
    }
}

/// <summary>GUI to GUI: show yourself. Versioned so a peer speaking another protocol is refused.</summary>
public sealed record GuiShowRequest(int Version);

/// <summary>
/// GUI to GUI: the answer to a <see cref="GuiShowRequest"/>. <see cref="Accepted"/> is false
/// when this peer cannot show itself - another protocol version, or no window to restore -
/// and <see cref="Reason"/> says why.
/// </summary>
public sealed record GuiShowAck(int Version, bool Accepted, string? Reason = null);

/// <summary>The bounded timeouts of one Show/ack exchange.</summary>
public sealed class GuiActivationOptions
{
    public static GuiActivationOptions Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10));

    public GuiActivationOptions(TimeSpan connectTimeout, TimeSpan ackTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(connectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ackTimeout, TimeSpan.Zero);

        ConnectTimeout = connectTimeout;
        AckTimeout = ackTimeout;
    }

    /// <summary>
    /// How long a launch waits for the running GUI's pipe to appear. Covers the winner's
    /// startup window: it takes the instance lease microseconds before it opens the pipe.
    /// </summary>
    public TimeSpan ConnectTimeout { get; }

    /// <summary>How long the running GUI may take to restore its window before the ack gives up.</summary>
    public TimeSpan AckTimeout { get; }
}

/// <summary>
/// The instance lock: exactly one GUI per user/session. Held for the whole life of the GUI
/// that won it. The OS refuses a second handle - from another process or from this one -
/// while the first is open, and releases it automatically if the owning process dies, so a
/// crashed GUI never blocks the next launch.
/// </summary>
public sealed class GuiInstanceLease : IDisposable
{
    private FileStream? _lock;

    private GuiInstanceLease(FileStream lockStream) => _lock = lockStream;

    /// <summary>
    /// Takes the instance lock, or returns null when another GUI of this user/session already
    /// holds it.
    /// </summary>
    public static GuiInstanceLease? TryAcquire(GuiInstanceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        try
        {
            var directory = Path.GetDirectoryName(identity.LockPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new FileStream(
                identity.LockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            return new GuiInstanceLease(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another GUI holds the lock, or the file is momentarily locked elsewhere. Either
            // way this process is not the one GUI.
            return null;
        }
    }

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }
}

/// <summary>
/// Serves the activation channel for the life of one GUI: every Show request is answered
/// only after this GUI has restored its window, and a peer speaking another protocol
/// version is refused instead of guessed at. Avalonia-free on purpose - it starts before
/// the desktop lifetime does, and a pipe failure must never be able to take the GUI down.
/// </summary>
public sealed class GuiActivationServer : IDisposable
{
    private readonly GuiInstanceIdentity _identity;
    private readonly GuiActivationOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _acceptLoop;
    private bool _disposed;

    public GuiActivationServer(GuiInstanceIdentity identity, GuiActivationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(identity);

        _identity = identity;
        _options = options ?? GuiActivationOptions.Default;
        _acceptLoop = Task.Run(AcceptLoopAsync, CancellationToken.None);
    }

    /// <summary>
    /// The server of this process's own GUI instance, set by the launcher before the desktop
    /// lifetime starts so the application can answer Show requests. Null in tests, which
    /// construct servers directly.
    /// </summary>
    internal static GuiActivationServer? ActiveInstance { get; private set; }

    /// <summary>
    /// Raised when a peer asked this GUI to show itself. The handler restores the window; the
    /// ack is written only after it completes, bounded by <see cref="GuiActivationOptions.AckTimeout"/>.
    /// </summary>
    public event Func<Task>? ShowRequested;

    internal static GuiActivationServer StartActive(GuiInstanceIdentity identity)
    {
        var server = new GuiActivationServer(identity);
        ActiveInstance = server;
        return server;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (ReferenceEquals(ActiveInstance, this))
        {
            ActiveInstance = null;
        }

        _lifetime.Cancel();

        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is AggregateException or OperationCanceledException)
        {
            // The loop is best-effort; a wedged accept must not block process exit.
        }

        _lifetime.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var server = CreatePipe(_identity.PipeName);
                try
                {
                    await server.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                _ = ServeConnectionAsync(server);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // A pipe failure must never take the GUI down: the activation channel is how
                // a second launch reaches this one, not something the mapping depends on.
                // Back off so a permanently unusable name cannot spin the loop.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream server)
    {
        await using (server)
        {
            try
            {
                var payload = await PipeFraming.ReadFrameAsync(server, _lifetime.Token).ConfigureAwait(false);
                var request = Deserialize<GuiShowRequest>(payload);
                var (accepted, reason) = await AnswerAsync(request).ConfigureAwait(false);

                await PipeFraming.WriteFrameAsync(
                    server,
                    Serialize(new GuiShowAck(GuiActivationContract.ProtocolVersion, accepted, reason)),
                    _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The peer vanished mid-exchange: there is nobody left to answer.
            }
        }
    }

    private async Task<(bool Accepted, string? Reason)> AnswerAsync(GuiShowRequest request)
    {
        if (request.Version != GuiActivationContract.ProtocolVersion)
        {
            return (false, $"unsupported activation protocol version {request.Version}");
        }

        var handler = ShowRequested;
        if (handler is null)
        {
            return (false, "this GUI has no window to show yet");
        }

        try
        {
            await handler().WaitAsync(_options.AckTimeout, _lifetime.Token).ConfigureAwait(false);
            return (true, null);
        }
        catch (TimeoutException)
        {
            return (false, "the show request timed out");
        }
    }

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }

        // Same-integrity GUI-to-GUI signaling: only this user's processes may connect. The
        // daemon's mapping pipe is a separate channel with its own ACL.
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    internal static byte[] Serialize<T>(T message)
        => JsonSerializer.SerializeToUtf8Bytes(message, IpcSerializer.Options);

    internal static T Deserialize<T>(byte[] payload)
        => JsonSerializer.Deserialize<T>(payload, IpcSerializer.Options)
            ?? throw new JsonException($"the activation message deserialized to null ({typeof(T).Name}).");
}

/// <summary>
/// The launch side of the activation channel: asks a running GUI to show itself and reports
/// whether it answered. Every failure mode - no GUI, a wedged GUI, a peer speaking another
/// protocol - is a plain false, so the caller's only options are "exit, it is showing" and
/// "exit, it could not be reached". Neither is a reason to start a second GUI.
/// </summary>
internal static class GuiActivationClient
{
    /// <summary>
    /// Attempts of one Show exchange. A launch that arrives while the running GUI is tearing
    /// down a dead peer can land on the connection that is being reset; the next attempt
    /// reaches the live listener. Show is idempotent, so a retry is always safe.
    /// </summary>
    private const int Attempts = 3;

    public static async Task<bool> TryShowAsync(
        GuiInstanceIdentity identity,
        GuiActivationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(options);

        for (var attempt = 1; ; attempt++)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                identity.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.ConnectAsync(options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                // Nothing is listening: there is no GUI to ask, and retrying would only
                // delay the launch that is about to become the one GUI.
                await pipe.DisposeAsync().ConfigureAwait(false);
                return false;
            }

            try
            {
                await PipeFraming.WriteFrameAsync(
                    pipe,
                    GuiActivationServer.Serialize(new GuiShowRequest(GuiActivationContract.ProtocolVersion)),
                    cancellationToken).ConfigureAwait(false);

                var payload = await PipeFraming.ReadFrameAsync(pipe, cancellationToken)
                    .AsTask()
                    .WaitAsync(options.AckTimeout, cancellationToken)
                    .ConfigureAwait(false);
                var ack = GuiActivationServer.Deserialize<GuiShowAck>(payload);

                return ack is { Version: GuiActivationContract.ProtocolVersion, Accepted: true };
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                if (attempt >= Attempts)
                {
                    return false;
                }
            }
            finally
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
        => exception is TimeoutException or IOException or UnauthorizedAccessException or ObjectDisposedException
            or FrameProtocolException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);
}
