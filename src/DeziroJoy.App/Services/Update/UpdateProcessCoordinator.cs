using System.ComponentModel;
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

    /// <summary>
    /// How long the daemon *process* gets to exit once its pipe is gone. The pipe is the
    /// daemon's own and it closes it as part of shutting down, so a daemon that still holds the
    /// pipe has not begun to exit - but the last of it, the part still holding the install
    /// directory's files open, can outlive the pipe by a moment. Windows will not let an
    /// installer replace a file a live process has open: its DeleteFile fails with access denied
    /// (error 5) and it rolls back, which is the failure this wait exists to prevent.
    /// </summary>
    private static readonly TimeSpan DaemonExitBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to wait for a daemon that never named its process - one that was already gone
    /// when the handshake ran, or a snapshot reporting no injector at all. Its pipe is gone,
    /// which is the same evidence a vanished process gives, so the grace stands in for the
    /// process that could not be waited on.
    /// </summary>
    private static readonly TimeSpan DaemonExitGrace = TimeSpan.FromMilliseconds(500);

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
    /// <remarks>
    /// The stop is not finished when the pipe is gone. The daemon releases the pipe as part of
    /// exiting, and the elevated process that owned it can still be unwinding behind that - still
    /// holding <c>DeziroJoy.exe</c> and the libraries beside it open - while the installer is
    /// already trying to replace them. So once the pipe has vanished, the process the daemon's
    /// own snapshot named is waited on, bounded, and only then is the daemon declared gone.
    /// </remarks>
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
    /// <param name="awaitProcessExit">
    /// Waits for a daemon process to exit and answers whether it did within the budget.
    /// Production waits on the real process the daemon named; tests inject a fake so no real
    /// process is waited on and the wait itself is what gets asserted.
    /// </param>
    public static async Task<bool> StopDaemonAsync(
        CancellationToken cancellationToken,
        IDaemonEndpoint? endpoint = null,
        TimeSpan? stopBudget = null,
        TimeSpan? requestTimeout = null,
        Func<int, TimeSpan, CancellationToken, Task<bool>>? awaitProcessExit = null)
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
        var processId = await RequestShutdownAsync(endpoint, requestTimeout, cancellationToken)
            .ConfigureAwait(false);

        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (!await endpoint.EnsureReadyAsync(allowLaunch: false, cancellationToken).ConfigureAwait(false))
            {
                // The pipe is gone, but the daemon process may still be exiting behind it and
                // holding the files the installer is about to replace. Wait for the process
                // itself before declaring the daemon stopped.
                return await AwaitDaemonExitAsync(processId, awaitProcessExit, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (elapsed.Elapsed >= budget)
            {
                return false;
            }

            await Task.Delay(DaemonProbePause, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for the daemon process to be gone, not merely for its pipe. The PID is the one the
    /// daemon's own handshake snapshot reported; without one the pipe's vanish is all the
    /// evidence there is, and a bounded grace stands in for the process that cannot be named.
    /// </summary>
    private static async Task<bool> AwaitDaemonExitAsync(
        int? processId,
        Func<int, TimeSpan, CancellationToken, Task<bool>>? awaitProcessExit,
        CancellationToken cancellationToken)
    {
        if (processId is not { } pid || pid == Environment.ProcessId)
        {
            // Nothing to wait on: a daemon that named no process, or - were one ever to serve
            // the pipe in-process - this very GUI, which is about to exit on its own.
            await Task.Delay(DaemonExitGrace, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return await (awaitProcessExit ?? AwaitProcessExitAsync)(pid, DaemonExitBudget, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The production daemon-exit wait: the real process the daemon named, bounded. A process
    /// that is already gone is a success - that is the goal state, reached before the wait began
    /// - and one this process cannot open falls back to the same bounded grace an unnamed daemon
    /// gets, because the pipe is gone either way.
    /// </summary>
    private static async Task<bool> AwaitProcessExitAsync(
        int processId,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);

            // WaitForExitAsync has no budget of its own, so the budget is a linked token: a
            // timeout and the caller's own cancellation are told apart by the token, exactly as
            // everywhere else in this flow.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(budget);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The budget ran out and the caller is still patient: the daemon is still there.
                return false;
            }
        }
        catch (ArgumentException)
        {
            // The daemon exited before the wait started: the goal state, reached early.
            return true;
        }
        catch (Exception exception) when (exception
            is InvalidOperationException or Win32Exception)
        {
            // No handle for it - an elevated daemon a medium-integrity GUI may not be able to
            // open, or a process this user may not query. The pipe is already gone, so the grace
            // is what is left to give.
            await Task.Delay(DaemonExitGrace, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>
    /// Asks the daemon to exit over IPC and reports the PID of the daemon that answered, so the
    /// caller can wait for that process to be gone rather than only for its pipe. A daemon that
    /// vanished between the probe and the handshake is not a failure here - the caller's vanish
    /// loop decides what the goal state is - so the exceptions of a connection that never
    /// happened are swallowed rather than reported as "could not stop the daemon".
    /// </summary>
    /// <remarks>
    /// The filter is the one <see cref="GuiActivationClient"/> uses for its exchange, and for
    /// the same reason: a protocol failure and a timeout are facts about the peer, not about the
    /// caller. The cancellation case is the subtle one - the controller links its own
    /// request timeout into the handshake, so a daemon that takes the pipe and then goes quiet
    /// raises <see cref="OperationCanceledException"/> with the caller's token perfectly
    /// healthy. Letting that through would tell the host the user cancelled, and its message
    /// would claim nothing was stopped while the daemon is in fact still running.
    /// </remarks>
    private static async Task<int?> RequestShutdownAsync(
        IDaemonEndpoint endpoint,
        TimeSpan? requestTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var controller = new DaemonController(
                endpoint,
                requestTimeout: requestTimeout);
            var snapshot = await controller.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await controller.RequestShutdownAsync(cancellationToken).ConfigureAwait(false);

            // A snapshot that names no injector leaves the daemon unnamed: the pipe's vanish is
            // then the only evidence there is, and the caller's grace stands in for the process.
            return snapshot.InjectorPid is > 0 ? snapshot.InjectorPid : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // This service's own handshake budget expired, not the caller's patience: the
            // vanish loop decides what the goal state is, exactly as for a refused shutdown.
            return null;
        }
        catch (Exception exception) when (exception
            is DaemonUnavailableException or DaemonHandshakeException or DaemonDisconnectedException
            or FrameProtocolException or JsonException
            or IOException or TimeoutException or ObjectDisposedException)
        {
            return null;
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
