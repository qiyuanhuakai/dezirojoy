using System.IO.Pipes;
using DeziroJoy.App.Ipc;

namespace DeziroJoy.App.Services;

/// <summary>
/// The daemon endpoint the update flow talks through: it answers the mapping pipe and nothing
/// else. The production endpoint launches an elevated daemon when the pipe is absent
/// (<see cref="NamedPipeDaemonEndpoint.EnsureReadyAsync"/> calls RelaunchElevated - a UAC
/// prompt on Windows), which is right for a GUI whose job is to bring the mapping authority up
/// and exactly wrong for an updater that must only ever ask a daemon that is already there to
/// go away. A "nothing to stop" probe that starts a daemon just to stop it again, and a
/// post-shutdown confirmation that resurrects the daemon it just stopped and then reports the
/// stop as failed, are both that mistake.
/// </summary>
/// <remarks>
/// The probe mirrors <see cref="DaemonSupervisor.IsDaemonReachable"/> - the raw pipe connect
/// the tray uses for the same reason - including its three-attempt budget, which is what keeps
/// a daemon that is still creating its pipe from being mistaken for an absent one.
/// </remarks>
internal sealed class ProbeOnlyDaemonEndpoint : IDaemonEndpoint
{
    /// <summary>How long one connect attempt waits for the pipe to answer.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Pause between probe attempts.</summary>
    private static readonly TimeSpan ProbeRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Attempts before concluding that nothing answers. The daemon-mid-startup race is
    /// exactly this wide: the process exists but has not created its pipe yet, so one failed
    /// attempt proves nothing.
    /// </summary>
    private const int ProbeAttempts = 3;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="allowLaunch"/> is deliberately ignored: the update flow never starts a
    /// daemon, whatever it is asked for. Honouring it would turn the liveness probes of
    /// <see cref="UpdateProcessCoordinator.StopDaemonAsync"/> into UAC prompts.
    /// </remarks>
    public async Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ProbeAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(ProbeRetryDelay, cancellationToken).ConfigureAwait(false);
            }

            if (await ProbeAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
    {
        var pipe = CreateClient();
        try
        {
            await pipe.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            return new NamedPipeDaemonChannel(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = CreateClient();
            await pipe.ConnectAsync(ProbeTimeout, cancellationToken).ConfigureAwait(false);
            return pipe.IsConnected;
        }
        catch (Exception exception) when (exception
            is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // Nothing answered within the budget: that is not an answer either way. A
            // cancelled caller token is deliberately not swallowed here - "no answer" must
            // never be mistaken for "the daemon is gone" when the caller gave up.
            return false;
        }
    }

    private static NamedPipeClientStream CreateClient() => new(
        ".",
        IpcContract.PipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous);
}
