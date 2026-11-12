using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
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
/// The runtime identity of one GUI: the instance lock and the activation channel that belong to
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

    /// <summary>
    /// Where the current lease holder publishes the activation endpoint a launch should reach.
    /// Only meaningful on Linux, where the endpoint is a per-instance abstract socket that has
    /// to be discovered; Windows keeps its well-known named pipe and never reads this. The path
    /// is derived from the lock so every process of one user/session agrees on it.
    /// </summary>
    public string DiscoveryPath => Path.ChangeExtension(LockPath, ".activation");
}

public enum GuiInstanceRole
{
    Render,
    Tray,
}

/// <summary>
/// What a peer asks this GUI to do over the activation channel: show itself, answer a
/// readiness probe, or exit. Exit exists for the update flow - an installer cannot replace a
/// binary the tray is still running, and a relaunch would otherwise wake the old tray.
/// </summary>
public enum GuiActivationRequestKind
{
    Show,
    Probe,
    Exit,
}

/// <summary>
/// GUI to GUI: show yourself, answer a readiness probe, or exit. The added fields have
/// defaults so a version-1 Show written by an older build retains its meaning, and an older
/// peer that does not know <see cref="GuiActivationRequestKind.Exit"/> simply refuses it -
/// which the caller turns into "close the tray by hand" instead of installing behind it.
/// </summary>
public sealed record GuiShowRequest(
    int Version,
    GuiActivationRequestKind Kind = GuiActivationRequestKind.Show,
    GuiInstanceRole? RequiredRole = null);

/// <summary>
/// GUI to GUI: the answer to a <see cref="GuiShowRequest"/>. <see cref="Accepted"/> is false
/// when this peer cannot answer - another protocol version, the wrong process role, or no
/// window to restore - and <see cref="Reason"/> says why.
/// </summary>
public sealed record GuiShowAck(
    int Version,
    bool Accepted,
    string? Reason = null,
    GuiInstanceRole Role = GuiInstanceRole.Render);

/// <summary>The bounded timeouts of one Show/ack exchange.</summary>
public sealed class GuiActivationOptions
{
    public static GuiActivationOptions Default { get; } = new(
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(10));

    public GuiActivationOptions(TimeSpan connectTimeout, TimeSpan ackTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(connectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ackTimeout, TimeSpan.Zero);

        ConnectTimeout = connectTimeout;
        AckTimeout = ackTimeout;
    }

    /// <summary>
    /// How long a launch waits for the running GUI's endpoint to appear. Covers the winner's
    /// startup window: it takes the instance lease microseconds before it publishes the channel.
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
/// Accepts one activation connection at a time and hands back the connected stream. Windows
/// implements this with a same-session named pipe (one server instance per accept, exactly as
/// the OS intends); Linux with an abstract Unix domain socket. The abstraction exists so the
/// accept loop, the Show/ack exchange and the drain never have to know which is in play.
/// </summary>
internal interface IActivationListener : IAsyncDisposable
{
    Task<Stream> AcceptAsync(CancellationToken cancellationToken);
}

/// <summary>Opens the client half of the activation channel to a named endpoint.</summary>
internal interface IActivationPortal
{
    Task<Stream> ConnectAsync(string endpoint, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Serves the activation channel for the life of one GUI: every Show request is answered
/// only after this GUI has restored its window, an Exit request only after the exit path has
/// run, and a peer speaking another protocol version is refused instead of guessed at.
/// Avalonia-free on purpose - it starts before the desktop lifetime does, and a channel
/// failure must never be able to take the GUI down.
/// </summary>
/// <remarks>
/// <para>
/// Windows listens on the well-known named pipe (<see cref="GuiInstanceIdentity.PipeName"/>):
/// named pipes are kernel objects, so a retiring holder's exit never disturbs a successor that
/// binds the same name.
/// </para>
/// <para>
/// Linux cannot share one Unix socket path across the handover: .NET maps a named pipe to a
/// filesystem socket at a fixed path and unlinks it on dispose, so a retiring holder would
/// delete the very socket its successor just bound (a fresh launch then waits out the whole
/// connect budget). Linux therefore listens on a <b>per-instance abstract socket</b> - the
/// kernel frees an abstract name the moment its last descriptor closes, so there is no file to
/// unlink and no stale entry to accumulate - and publishes that endpoint through a small
/// discovery file. A launch reads the discovery file to reach the current holder; the holder's
/// own exit removes only its own descriptor and can never touch the successor's.
/// </para>
/// </remarks>
public sealed class GuiActivationServer : IDisposable
{
    /// <summary>How long a retiring holder lets an in-flight Show finish writing its ack.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How often a launch re-checks the discovery file while waiting for a holder.</summary>
    internal static readonly TimeSpan DiscoveryPollInterval = TimeSpan.FromMilliseconds(25);

    private readonly GuiInstanceIdentity _identity;
    private readonly GuiActivationOptions _options;

    /// <summary>Stops the accept loop from taking new connections once the holder retires.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// Cancels the connections still being served. Left running through a retire so an in-flight
    /// Show can finish; cancelled only when the bounded drain gives up on a straggler.
    /// </summary>
    private readonly CancellationTokenSource _serving = new();

    private readonly object _serveGate = new();
    private readonly object _drainGate = new();
    private readonly HashSet<Task> _inFlight = [];

    private IActivationListener? _listener;
    private Task? _acceptLoop;
    private string? _endpoint;
    private bool _disposed;

    public GuiActivationServer(
        GuiInstanceIdentity identity,
        GuiActivationOptions? options = null,
        GuiInstanceRole role = GuiInstanceRole.Render)
        : this(identity, options, autoStart: true, role)
    {
    }

    private GuiActivationServer(
        GuiInstanceIdentity identity,
        GuiActivationOptions? options,
        bool autoStart,
        GuiInstanceRole role)
    {
        ArgumentNullException.ThrowIfNull(identity);

        _identity = identity;
        _options = options ?? GuiActivationOptions.Default;
        Role = role;

        if (autoStart)
        {
            BeginServing();
        }
    }

    public GuiInstanceRole Role { get; }

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

    /// <summary>
    /// Raised when a peer asked this GUI to exit, which is how an updater clears a tray that
    /// still runs the old binary. The handler runs the same exit path the tray menu uses -
    /// stop the daemon, stop the render, retire - and the ack is written only after it
    /// completes, so a peer that is told "accepted" can rely on the exit being under way.
    /// </summary>
    public event Func<Task>? ExitRequested;

    internal static GuiActivationServer StartActive(
        GuiInstanceIdentity identity,
        GuiInstanceRole role = GuiInstanceRole.Render)
    {
        var server = new GuiActivationServer(identity, null, autoStart: false, role);
        ActiveInstance = server;
        return server;
    }

    /// <summary>
    /// Creates a server that neither serves nor becomes the active instance until
    /// <see cref="BeginServing"/> is called. Models a leaseholder that has taken the instance lock
    /// but not yet published its channel, without disturbing the process-wide active instance.
    /// </summary>
    internal static GuiActivationServer CreatePending(
        GuiInstanceIdentity identity,
        GuiInstanceRole role = GuiInstanceRole.Render)
        => new(identity, null, autoStart: false, role);

    /// <summary>
    /// Starts accepting activation connections. Called once the GUI can actually answer a Show
    /// request, so a launch arriving while the runtime and window are still being built waits
    /// for the live listener instead of being refused. On Linux this is also the moment the
    /// per-instance endpoint is published to the discovery file.
    /// </summary>
    public void BeginServing()
    {
        lock (_serveGate)
        {
            if (_disposed || _acceptLoop is not null)
            {
                return;
            }

            var endpoint = ServerEndpoint(_identity);
            try
            {
                _listener = CreateListener(endpoint);
                _endpoint = endpoint;
                PublishEndpoint(_identity, endpoint);
            }
            catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException)
            {
                // The activation channel is how a second launch reaches this GUI, never something
                // the mapping depends on: a channel that cannot be opened must not take the GUI
                // down. Leave the listener unset so the channel simply stays unavailable.
                _listener = null;
                Trace.WriteLine($"[DeziroJoy] activation channel unavailable: {exception.Message}");
                return;
            }

            _acceptLoop = Task.Run(AcceptLoopAsync, CancellationToken.None);
        }
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

        // Stop taking new connections first, so the drain below only has to wait for the
        // exchanges already under way.
        _lifetime.Cancel();

        if (_acceptLoop is { } acceptLoop)
        {
            try
            {
                acceptLoop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is AggregateException or OperationCanceledException)
            {
                // The loop is best-effort; a wedged accept must not block process exit.
            }
        }

        // Let every in-flight Show finish writing its ack before the channel is torn down: a
        // launch that reached this holder microseconds before it retired must still be answered,
        // not dropped by the lifetime cancellation above.
        DrainInFlight();

        // Anything still stuck after the bounded drain is a peer that never completed its
        // exchange; cancel it so its stream is released instead of blocking process exit.
        _serving.Cancel();

        if (_listener is { } listener)
        {
            try
            {
                listener.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is AggregateException or OperationCanceledException)
            {
                // A listener that will not close must not block process exit either.
            }
        }

        // Discovery is deliberately never deleted on exit. A read-then-delete here could not be
        // made atomic with a successor's publish, so it could erase a just-published endpoint and
        // wedge second launches. Instead a stale entry is replaced atomically by the next
        // publisher, and the client retries past a stale name until then.
        _lifetime.Dispose();
        _serving.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            Stream stream;
            try
            {
                stream = await _listener!.AcceptAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // A channel failure must never take the GUI down: the activation channel is how
                // a second launch reaches this one, not something the mapping depends on.
                // Back off so a permanently unusable channel cannot spin the loop.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            var serve = ServeConnectionAsync(stream, _serving.Token);
            Track(serve);
        }
    }

    /// <summary>
    /// Registers an in-flight exchange so <see cref="DrainInFlight"/> can wait for it, and drops
    /// the reference - observing any fault - once it completes.
    /// </summary>
    private void Track(Task serve)
    {
        lock (_drainGate)
        {
            _inFlight.Add(serve);
        }

        _ = serve.ContinueWith(static (completed, state) =>
        {
            var server = (GuiActivationServer)state!;
            _ = completed.Exception; // Observe, so a cancelled straggler never goes unobserved.
            lock (server._drainGate)
            {
                server._inFlight.Remove(completed);
            }
        }, this, TaskScheduler.Default);
    }

    private void DrainInFlight()
    {
        Task[] pending;
        lock (_drainGate)
        {
            pending = _inFlight.ToArray();
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            Task.WaitAll(pending, (int)DrainTimeout.TotalMilliseconds);
        }
        catch (AggregateException)
        {
            // A peer vanished mid-exchange: there is nobody left to answer, which is fine.
        }
    }

    private async Task ServeConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using (stream)
        {
            try
            {
                var payload = await PipeFraming.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                var request = Deserialize<GuiShowRequest>(payload);
                var (accepted, reason) = await AnswerAsync(request, cancellationToken).ConfigureAwait(false);

                await PipeFraming.WriteFrameAsync(
                    stream,
                    Serialize(new GuiShowAck(GuiActivationContract.ProtocolVersion, accepted, reason, Role)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The peer vanished mid-exchange, or the channel was torn down while draining a
                // retiring holder: either way there is nobody left to answer.
            }
        }
    }

    private async Task<(bool Accepted, string? Reason)> AnswerAsync(GuiShowRequest request, CancellationToken cancellationToken)
    {
        if (request.Version != GuiActivationContract.ProtocolVersion)
        {
            return (false, $"unsupported activation protocol version {request.Version}");
        }

        if (request.RequiredRole is { } requiredRole && requiredRole != Role)
        {
            return (false, $"this activation peer is {Role}, not {requiredRole}");
        }

        if (request.Kind == GuiActivationRequestKind.Probe)
        {
            return (true, null);
        }

        if (request.Kind == GuiActivationRequestKind.Exit)
        {
            var exitHandler = ExitRequested;
            if (exitHandler is null)
            {
                return (false, "this GUI cannot be asked to exit");
            }

            try
            {
                await exitHandler().WaitAsync(_options.AckTimeout, cancellationToken).ConfigureAwait(false);
                return (true, null);
            }
            catch (TimeoutException)
            {
                return (false, "the exit request timed out");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return (false, $"the exit request failed: {exception.Message}");
            }
        }

        if (request.Kind != GuiActivationRequestKind.Show)
        {
            return (false, $"unsupported activation request kind {request.Kind}");
        }

        var handler = ShowRequested;
        if (handler is null)
        {
            return (false, "this GUI has no window to show yet");
        }

        try
        {
            await handler().WaitAsync(_options.AckTimeout, cancellationToken).ConfigureAwait(false);
            return (true, null);
        }
        catch (TimeoutException)
        {
            return (false, "the show request timed out");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (false, $"the show request failed: {exception.Message}");
        }
    }

    /// <summary>
    /// The endpoint this process listens on. Windows reuses the well-known pipe name (named pipes
    /// are per-kernel-object and survive a handover); Linux mints a per-instance abstract name so
    /// a retiring holder can never unlink its successor.
    /// </summary>
    private static string ServerEndpoint(GuiInstanceIdentity identity)
        => OperatingSystem.IsWindows()
            ? identity.PipeName
            : $"{identity.PipeName}.{Guid.NewGuid():N}";

    private static IActivationListener CreateListener(string endpoint)
        => OperatingSystem.IsWindows()
            ? new NamedPipeListener(endpoint)
            : new AbstractUnixListener(endpoint);

    /// <summary>
    /// The endpoint a launch should reach. Windows answers with the well-known pipe; Linux reads
    /// the discovery file the current holder published. Null means no holder has published yet.
    /// </summary>
    internal static string? ClientEndpoint(GuiInstanceIdentity identity)
        => OperatingSystem.IsWindows()
            ? identity.PipeName
            : ReadEndpoint(identity);

    private static void PublishEndpoint(GuiInstanceIdentity identity, string endpoint)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var path = identity.DiscoveryPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Publish atomically: a launch must never read a half-written endpoint.
            var temporary = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temporary, endpoint);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Discovery is best-effort; a GUI that cannot publish it simply cannot be woken.
        }
    }

    private static string? ReadEndpoint(GuiInstanceIdentity identity)
    {
        try
        {
            var path = identity.DiscoveryPath;
            if (!File.Exists(path))
            {
                return null;
            }

            var endpoint = File.ReadAllText(path).Trim();
            return endpoint.Length == 0 ? null : endpoint;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Re-asserts this server's endpoint in the discovery file. Called when a holder that lost the
    /// lease during a handover reclaims it after the successor died or was rolled back: discovery
    /// must name the surviving holder again, not the dead successor. A no-op unless this server is
    /// actually serving (Linux only; Windows has no discovery).
    /// </summary>
    internal void RepublishEndpoint()
    {
        var endpoint = _endpoint;
        if (_disposed || endpoint is null)
        {
            return;
        }

        PublishEndpoint(_identity, endpoint);
    }

    internal static byte[] Serialize<T>(T message)
        => JsonSerializer.SerializeToUtf8Bytes(message, IpcSerializer.Options);

    internal static T Deserialize<T>(byte[] payload)
        => JsonSerializer.Deserialize<T>(payload, IpcSerializer.Options)
            ?? throw new JsonException($"the activation message deserialized to null ({typeof(T).Name}).");

    /// <summary>
    /// A same-session named pipe, one server instance per accept. <c>CurrentUserOnly</c> keeps
    /// the GUI-to-GUI signaling inside this user's integrity level.
    /// </summary>
    private sealed class NamedPipeListener(string pipeName) : IActivationListener
    {
        public async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
        {
            var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await server.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return server;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A per-instance abstract Unix domain socket. The leading NUL puts the name in the abstract
    /// namespace: there is no filesystem entry to unlink, and the kernel reclaims the name as soon
    /// as this process closes its descriptor, so a retiring holder leaves neither a race nor a
    /// stale socket behind.
    /// </summary>
    private sealed class AbstractUnixListener(string endpoint) : IActivationListener
    {
        private const int Backlog = 16;

        private readonly Socket _listener = Create(endpoint);

        private static Socket Create(string endpoint)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint("\0" + endpoint));
            socket.Listen(Backlog);
            return socket;
        }

        public async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
        {
            var client = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            return new NetworkStream(client, ownsSocket: true);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Dispose();
            await ValueTask.CompletedTask;
        }
    }
}

/// <summary>Connects a launch to a Windows same-session named pipe.</summary>
internal sealed class NamedPipePortal : IActivationPortal
{
    public async Task<Stream> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return pipe;
    }
}

/// <summary>Connects a launch to a per-instance abstract Unix domain socket.</summary>
internal sealed class AbstractUnixPortal : IActivationPortal
{
    public async Task<Stream> ConnectAsync(string endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(timeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint("\0" + endpoint), bound.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new NetworkStream(socket, ownsSocket: true);
    }
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

    /// <summary>Floor for one connect attempt so a nearly-spent budget still gets a fair try.</summary>
    private static readonly TimeSpan MinConnectBudget = TimeSpan.FromMilliseconds(25);

    private static readonly IActivationPortal Portal =
        OperatingSystem.IsWindows()
            ? new NamedPipePortal()
            : new AbstractUnixPortal();

    public static Task<bool> TryShowAsync(
        GuiInstanceIdentity identity,
        GuiActivationOptions options,
        CancellationToken cancellationToken = default)
        => TryExchangeAsync(
            identity,
            options,
            new GuiShowRequest(GuiActivationContract.ProtocolVersion),
            expectedRole: null,
            cancellationToken);

    public static Task<bool> TryProbeAsync(
        GuiInstanceIdentity identity,
        GuiInstanceRole requiredRole,
        GuiActivationOptions options,
        CancellationToken cancellationToken = default)
        => TryExchangeAsync(
            identity,
            options,
            new GuiShowRequest(GuiActivationContract.ProtocolVersion, GuiActivationRequestKind.Probe, requiredRole),
            requiredRole,
            cancellationToken);

    /// <summary>
    /// Asks the resident tray to exit. The answer is true only when a Tray-role peer accepted,
    /// so a render process that happens to hold the lease never satisfies the request - and a
    /// tray from an older build, which refuses the kind it does not know, is a plain false
    /// that its caller reports as "close the tray by hand".
    /// </summary>
    public static Task<bool> TryExitAsync(
        GuiInstanceIdentity identity,
        GuiActivationOptions options,
        CancellationToken cancellationToken = default)
        => TryExchangeAsync(
            identity,
            options,
            new GuiShowRequest(GuiActivationContract.ProtocolVersion, GuiActivationRequestKind.Exit, GuiInstanceRole.Tray),
            GuiInstanceRole.Tray,
            cancellationToken);

    private static async Task<bool> TryExchangeAsync(
        GuiInstanceIdentity identity,
        GuiActivationOptions options,
        GuiShowRequest request,
        GuiInstanceRole? expectedRole,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(options);

        for (var attempt = 1; ; attempt++)
        {
            Stream stream;
            try
            {
                stream = await ConnectAsync(identity, options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                // No GUI is reachable within the connect budget: not a reason to start a second.
                return false;
            }

            try
            {
                await PipeFraming.WriteFrameAsync(
                    stream,
                    GuiActivationServer.Serialize(request),
                    cancellationToken).ConfigureAwait(false);

                var payload = await PipeFraming.ReadFrameAsync(stream, cancellationToken)
                    .AsTask()
                    .WaitAsync(options.AckTimeout, cancellationToken)
                    .ConfigureAwait(false);
                var ack = GuiActivationServer.Deserialize<GuiShowAck>(payload);

                return ack is { Version: GuiActivationContract.ProtocolVersion, Accepted: true }
                    && (expectedRole is null || ack.Role == expectedRole);
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
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Connects to the current holder. Windows connects straight to the well-known pipe, blocking
    /// up to <paramref name="connectTimeout"/> while the winner finishes starting. Linux re-reads
    /// discovery and retries within <paramref name="connectTimeout"/>: an absent endpoint, or one
    /// that refuses the connection (a retired/crashed holder, or a winner that has taken the lease
    /// but not begun serving yet), is not a failure - the current holder may still be publishing.
    /// Only when nothing live answers within the budget does this raise, which the caller turns
    /// into a plain "unreachable". Exposed internally so tests can drive a raw exchange.
    /// </summary>
    internal static async Task<Stream> ConnectAsync(
        GuiInstanceIdentity identity,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return await Portal.ConnectAsync(identity.PipeName, connectTimeout, cancellationToken)
                .ConfigureAwait(false);
        }

        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var endpoint = GuiActivationServer.ClientEndpoint(identity);
            if (endpoint is not null)
            {
                var remaining = connectTimeout - elapsed.Elapsed;
                if (remaining < MinConnectBudget)
                {
                    remaining = MinConnectBudget;
                }

                try
                {
                    return await Portal.ConnectAsync(endpoint, remaining, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsTransient(exception, cancellationToken))
                {
                    // A stale or not-yet-live endpoint: fall through, re-read discovery and retry
                    // inside the connect budget rather than giving up on a single refusal.
                }
            }

            if (elapsed.Elapsed >= connectTimeout)
            {
                throw new TimeoutException("no live activation endpoint answered within the connect budget");
            }

            await Task.Delay(GuiActivationServer.DiscoveryPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
        => exception is TimeoutException or IOException or UnauthorizedAccessException or ObjectDisposedException
            or SocketException or FrameProtocolException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);
}
