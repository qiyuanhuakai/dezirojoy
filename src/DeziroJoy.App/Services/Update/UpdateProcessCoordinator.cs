using System.Diagnostics;
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
    public static async Task<bool> StopDaemonAsync(
        CancellationToken cancellationToken,
        IDaemonEndpoint? endpoint = null,
        TimeSpan? stopBudget = null)
    {
        endpoint ??= new ProbeOnlyDaemonEndpoint();
        var budget = stopBudget ?? DaemonStopBudget;

        if (!await endpoint.EnsureReadyAsync(allowLaunch: false, cancellationToken).ConfigureAwait(false))
        {
            // Nothing resident: there is nothing to stop, and nothing to wait for.
            return true;
        }

        var acknowledged = await RequestShutdownAsync(endpoint, cancellationToken).ConfigureAwait(false);

        // Either way the goal state is the same - nothing resident - so that is what the
        // bounded wait checks. A daemon that refused the shutdown is still running when the
        // budget expires, and that is the honest failure; one that died despite a lost ack is
        // gone, and that is a success.
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
    private static async Task<bool> RequestShutdownAsync(
        IDaemonEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var controller = new DaemonController(endpoint);
            await controller.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return await controller.RequestShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception
            is DaemonUnavailableException or DaemonHandshakeException or DaemonDisconnectedException
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
