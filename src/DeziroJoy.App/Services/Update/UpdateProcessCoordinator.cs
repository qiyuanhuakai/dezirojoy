using System.Diagnostics;
using System.Text.Json;
using DeziroJoy.App.Ipc;

namespace DeziroJoy.App.Services;

/// <summary>
/// The production implementations of the two "stop what holds the old binary" steps the
/// update orchestration needs. Both are injectable seams, so tests drive the orchestrator
/// without a daemon, a tray or a registry.
/// </summary>
internal static class UpdateProcessCoordinator
{
    /// <summary>
    /// How long the daemon gets to release the mapping pipe after it acknowledged the
    /// shutdown. The tray's own supervisor uses the same budget.
    /// </summary>
    private static readonly TimeSpan DaemonStopBudget = TimeSpan.FromSeconds(5);

    /// <summary>Pause between daemon-vanish polls.</summary>
    private static readonly TimeSpan DaemonProbePause = TimeSpan.FromMilliseconds(50);

    /// <summary>Pause between tray-vanish polls.</summary>
    private static readonly TimeSpan TrayPollPause = TimeSpan.FromMilliseconds(50);

    /// <summary>How long the tray gets to actually disappear after accepting an Exit request.</summary>
    private static readonly TimeSpan TrayVanishBudget = TimeSpan.FromSeconds(10);

    /// <summary>The probe budget of one tray liveness check - short, because it is polled.</summary>
    private static readonly GuiActivationOptions ProbeOptions = new(
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Stops the resident daemon. Every daemon interaction goes through
    /// <see cref="ProbeOnlyDaemonEndpoint"/>, which never starts one: on Windows a
    /// launch-capable endpoint would pop UAC for the "is anything to stop?" probe, and - worse
    /// - resurrect the daemon from the post-shutdown confirmation and then report the stop as
    /// failed once the budget expired.
    /// </summary>
    /// <param name="endpoint">
    /// The daemon transport to use. Production leaves it null and gets the launch-free probe
    /// endpoint; tests pass a fake and assert what the stop did.
    /// </param>
    /// <param name="stopBudget">How long the daemon gets to release the pipe after the shutdown.</param>
    /// <param name="requestTimeout">
    /// How long one daemon request may take. The controller links this into the handshake, so a
    /// daemon that takes the pipe but never answers Hello gives up on this budget - which is a
    /// timeout of this service, never a cancellation of the caller.
    /// </param>
    public static async Task<bool> StopDaemonAsync(
        CancellationToken cancellationToken,
        IDaemonEndpoint? endpoint = null,
        TimeSpan? stopBudget = null,
        TimeSpan? requestTimeout = null)
    {
        endpoint ??= new ProbeOnlyDaemonEndpoint();
        var budget = stopBudget ?? DaemonStopBudget;

        if (!await endpoint.EnsureReadyAsync(allowLaunch: false, cancellationToken).ConfigureAwait(false))
        {
            // Nothing resident: there is nothing to stop, and nothing to wait for.
            return true;
        }

        // The shutdown's own result is deliberately not acted on: either way the goal state is
        // the same - nothing resident - so that is what the bounded wait below checks. A daemon
        // that refused the shutdown is still running when the budget expires, and that is the
        // honest failure; one that died despite a lost ack is gone, and that is a success.
        await RequestShutdownAsync(endpoint, requestTimeout, cancellationToken).ConfigureAwait(false);

        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (!await endpoint.EnsureReadyAsync(allowLaunch: false, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (elapsed.Elapsed >= budget)
            {
                return false;
            }

            await Task.Delay(DaemonProbePause, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks the daemon to exit over IPC. A daemon that vanished between the probe and the
    /// handshake is not a failure here - the caller's vanish loop decides what the goal state
    /// is - so the exceptions of a connection that never happened are swallowed rather than
    /// reported as "could not stop the daemon".
    /// </summary>
    /// <remarks>
    /// The filter is the one <see cref="GuiActivationClient"/> uses for its exchange, and for
    /// the same reason: a protocol failure and a timeout are facts about the peer, not about
    /// the caller. The cancellation case is the subtle one - the controller links its own
    /// request timeout into the handshake, so a daemon that takes the pipe and then goes quiet
    /// raises <see cref="OperationCanceledException"/> with the caller's token perfectly
    /// healthy. Letting that through would tell the host the user cancelled, and its message
    /// would claim nothing was stopped while the daemon is in fact still running.
    /// </remarks>
    private static async Task<bool> RequestShutdownAsync(
        IDaemonEndpoint endpoint,
        TimeSpan? requestTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var controller = new DaemonController(
                endpoint,
                requestTimeout: requestTimeout);
            await controller.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return await controller.RequestShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // This service's own handshake budget expired, not the caller's patience: the
            // vanish loop decides what the goal state is, exactly as for a refused shutdown.
            return false;
        }
        catch (Exception exception) when (exception
            is DaemonUnavailableException or DaemonHandshakeException or DaemonDisconnectedException
            or FrameProtocolException or JsonException
            or IOException or TimeoutException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks the tray process to exit over the activation channel. Three outcomes, and only
    /// the middle one means work was needed:
    /// <list type="number">
    /// <item>No tray is resident - the render process owns the instance lease while its
    /// window is up, and then no tray exists at all - so there is nothing to stop.</item>
    /// <item>The tray accepted the Exit request: wait for it to actually disappear, because
    /// an installer cannot replace a binary a retiring tray is still running.</item>
    /// <item>The tray refused (an older build that does not know the request) or never left:
    /// the caller reports an honest failure instead of installing behind a live tray.</item>
    /// </list>
    /// </summary>
    public static async Task<bool> AskTrayExitAsync(CancellationToken cancellationToken)
    {
        var identity = GuiInstanceIdentity.ForCurrentProcess();

        if (!await GuiActivationClient.TryProbeAsync(
                identity, GuiInstanceRole.Tray, ProbeOptions, cancellationToken)
            .ConfigureAwait(false))
        {
            return true;
        }

        if (!await GuiActivationClient.TryExitAsync(
                identity, GuiActivationOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TrayVanishBudget)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await GuiActivationClient.TryProbeAsync(
                    identity, GuiInstanceRole.Tray, ProbeOptions, cancellationToken)
                .ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(TrayPollPause, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
