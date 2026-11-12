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

    /// <summary>Pause between tray-vanish polls.</summary>
    private static readonly TimeSpan TrayPollPause = TimeSpan.FromMilliseconds(50);

    /// <summary>How long the tray gets to actually disappear after accepting an Exit request.</summary>
    private static readonly TimeSpan TrayVanishBudget = TimeSpan.FromSeconds(10);

    /// <summary>The probe budget of one liveness check - short, because it is polled.</summary>
    private static readonly GuiActivationOptions ProbeOptions = new(
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Stops the resident daemon. A daemon that is not running is a success - there is
    /// nothing to stop - and one that acknowledged but has not released its pipe yet is
    /// waited for, because dpkg/rpm cannot replace a binary the daemon still has mapped and
    /// on Windows the elevated copy is only reachable over this pipe in the first place.
    /// </summary>
    public static async Task<bool> StopDaemonAsync(CancellationToken cancellationToken)
    {
        if (!await DaemonAnswersAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        try
        {
            await using var controller = new DaemonController(new NamedPipeDaemonEndpoint());
            await controller.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!await controller.RequestShutdownAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }
        catch (Exception exception) when (exception
            is DaemonUnavailableException or DaemonHandshakeException or DaemonDisconnectedException
            or IOException or TimeoutException or ObjectDisposedException)
        {
            return false;
        }

        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (!await DaemonAnswersAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (elapsed.Elapsed >= DaemonStopBudget)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether a daemon answers the mapping pipe right now.</summary>
    private static async Task<bool> DaemonAnswersAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var controller = new DaemonController(new NamedPipeDaemonEndpoint());
            await controller.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return true;
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
