namespace DeziroJoy.App.Services;

/// <summary>
/// The outcome of GUI single-instance arbitration. Either this process owns the instance and
/// must build the desktop lifetime, or it must exit - with 0 when the running GUI was asked
/// to show itself and answered, and non-zero when it could not be reached.
/// </summary>
public readonly record struct GuiArbitration
{
    private GuiArbitration(bool isWinner, GuiInstanceLease? lease, int exitCode)
    {
        IsWinner = isWinner;
        Lease = lease;
        ExitCode = exitCode;
    }

    /// <summary>True when this process is the one GUI and must launch.</summary>
    public bool IsWinner { get; }

    /// <summary>The instance lock a winner holds for the life of its GUI.</summary>
    public GuiInstanceLease? Lease { get; }

    /// <summary>The process exit code a loser returns.</summary>
    public int ExitCode { get; }

    public static GuiArbitration Winner(GuiInstanceLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new GuiArbitration(true, lease, 0);
    }

    public static GuiArbitration Exit(int exitCode) => new(false, null, exitCode);
}

/// <summary>
/// GUI single-instance arbitration, run before any Avalonia or runtime construction. The
/// instance lock decides: the process that takes it is the one GUI and serves the activation
/// channel; every other launch asks that GUI to show itself and exits. A launch that cannot
/// reach the winner exits too - a failed activation is never a reason to construct a second
/// GUI, because two GUIs would double every injection.
/// </summary>
public static class GuiInstanceGuard
{
    public static async Task<GuiArbitration> ArbitrateAsync(
        GuiInstanceIdentity identity,
        GuiActivationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var lease = GuiInstanceLease.TryAcquire(identity);
        if (lease is not null)
        {
            return GuiArbitration.Winner(lease);
        }

        var shown = await GuiActivationClient.TryShowAsync(
            identity,
            options ?? GuiActivationOptions.Default,
            cancellationToken).ConfigureAwait(false);

        return GuiArbitration.Exit(shown ? 0 : 1);
    }
}

/// <summary>
/// Owns the two instance resources separately during a process handover. The lease moves first
/// so the successor can start, while the current activation server stays available until the
/// successor has acknowledged its own role. A failed handover can therefore reclaim the lease
/// without ever having taken the current UI's activation channel offline.
/// </summary>
internal sealed class GuiInstanceOwner : IDisposable
{
    private readonly object _gate = new();
    private readonly GuiInstanceIdentity _identity;
    private readonly GuiActivationServer _activation;
    private GuiInstanceLease? _lease;
    private bool _disposed;

    internal GuiInstanceOwner(GuiInstanceIdentity identity, GuiInstanceLease lease, GuiActivationServer activation)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(activation);
        _identity = identity;
        _lease = lease;
        _activation = activation;
    }

    internal void ReleaseLease()
    {
        lock (_gate)
        {
            _lease?.Dispose();
            _lease = null;
        }
    }

    internal bool TryReacquireLease()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _lease ??= GuiInstanceLease.TryAcquire(_identity);
            return _lease is not null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activation.Dispose();
            _lease?.Dispose();
            _lease = null;
        }
    }
}
