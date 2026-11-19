using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Profiles;
using DeziroJoy.Platform.Windows.Elevation;

namespace DeziroJoy.App.Hosting;

/// <summary>
/// One child process the tray supervises. The abstraction exists so the supervisors can be
/// driven headlessly: tests hand them a fake and no real process is ever spawned.
/// </summary>
internal interface IProcessHandle : IDisposable
{
    bool HasExited { get; }

    /// <summary>Raised when the child ended, so a supervisor can drop its cached handle.</summary>
    event Action? Exited;

    void Kill(bool entireTree);

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The one injectable process-launch seam. Production passes
/// <see cref="ProcessLauncher.Start"/>; every test passes a recorder, which is why no
/// supervisor ever spawns a real process.
/// </summary>
internal delegate IProcessHandle? ProcessLaunch(ProcessStartInfo startInfo);

internal enum DaemonSupervisionState
{
    Stopped,
    Running,
    Stopping,
    StoppedGracefully,
    Killed,
    StartFailed,
    StopFailed,
}

/// <summary>
/// One daemon process the recovery can see, with the identity a kill has to confirm.
/// </summary>
internal sealed record DaemonProcessIdentity(int ProcessId, DateTimeOffset StartTime);

/// <summary>
/// The daemon identity the supervisor learned from a served snapshot: the PID the
/// daemon named for itself, and the moment it was last seen alive. The stamp is an
/// upper bound on the process's start time - a daemon that served a snapshot started
/// before it - which is what makes a recycled PID detectable: a process that started
/// after the stamp is not the daemon the snapshot named.
/// </summary>
internal sealed record DaemonIdentity(int ProcessId, DateTimeOffset SeenAtUtc);

/// <summary>
/// What a supervised recovery reports: whether a daemon answers again, the phases it
/// took to get there, and why it failed when it did. The reconnect button shows both,
/// so a recovery that only claims success is not one.
/// </summary>
public sealed record DaemonRecoveryReport(bool Recovered, IReadOnlyList<string> Phases, string? Failure);

/// <summary>
/// The OS-facing seam the supervisor's recovery drives: identity-confirmed process
/// ends, the daemon instance and injection slots, and the platform's own resident
/// service controls (the Windows logon planned task, the Linux systemd user unit).
/// Production implements it over the real process table and schedulers; every test
/// passes a fake, so no recovery ever touches a real process.
/// </summary>
internal interface IDaemonRecovery
{
    /// <summary>Every daemon process visible right now, by entry path and argument.</summary>
    IReadOnlyList<DaemonProcessIdentity> FindDaemonProcesses();

    /// <summary>The start time of a live process, or null when it is gone or unreadable.</summary>
    DateTimeOffset? TryGetStartTime(int processId);

    /// <summary>True when the process runs this product's daemon entry.</summary>
    bool IsDaemonExecutable(int processId);

    /// <summary>Ends one identified daemon process; false when the OS refused.</summary>
    bool EndProcess(int processId);

    /// <summary>True when the daemon instance slot is free - no live daemon holds it.</summary>
    bool IsDaemonInstanceSlotFree();

    /// <summary>True when the injection mutex is free - no engine is injecting.</summary>
    bool IsInjectionSlotFree();

    /// <summary>Windows: the logon planned task for the daemon is registered.</summary>
    bool IsDaemonTaskInstalled();

    /// <summary>Windows: ends the planned task's running instance.</summary>
    bool EndDaemonTask();

    /// <summary>Windows: starts the planned task again.</summary>
    bool StartDaemonTask();

    /// <summary>Linux: stops the systemd user unit.</summary>
    bool StopDaemonUnit();

    /// <summary>
    /// Windows: hands the identified daemon to an elevated copy of this product, which
    /// verifies the same identity with its own token, ends the old process, waits for
    /// the slots and becomes the replacement daemon. True when the hand-off was made -
    /// the caller's wait for the mapping pipe is what proves it landed. False when no
    /// elevated copy could be asked (a declined UAC prompt), which is a failure, never
    /// a reason to end something else.
    /// </summary>
    bool StartElevatedRecovery(int processId, DateTimeOffset seenAt);
}

/// <summary>
/// What this process can tell about a daemon process. The three cases are the whole point: a
/// process that is not running is gone, one that is running is not, and one this token cannot
/// open is neither - and collapsing that third case into either of the others is how an update
/// came to start an installer against a daemon that was still holding its files.
/// </summary>
internal enum DaemonProcessLiveness
{
    /// <summary>No such process: the goal state.</summary>
    Gone,

    /// <summary>The process is there and this caller can see it.</summary>
    Running,

    /// <summary>The process is there and this caller cannot open it - the elevated daemon.</summary>
    Unreadable,
}

/// <summary>
/// Supervises the resident daemon. Disposing a supervisor only releases its process handle;
/// explicit application exit is the sole path that asks the daemon to stop.
/// <para>
/// Beyond start and stop it owns the recovery: a daemon that stopped answering - the
/// intermittent state where the mapping pipe still accepts a connect but no request
/// ever completes - cannot be fixed by a client reconnect, because the process that
/// holds the pipe, the injection mutex and the instance slot is still there. Only
/// ending that exact process and starting a replacement restores mapping, and only
/// the supervisor can do it against the right owner: the owned child, the systemd
/// user unit, the Windows planned task, or - for the elevated daemon no task owns -
/// an elevated copy of this product that verifies the same identity with its own
/// token, ends the old process and becomes the replacement. <see cref="Recover"/> is
/// that bounded restart, and <see cref="Restart"/> is its boolean form for the
/// backend switch.
/// </para>
/// <para>
/// <see cref="StopForUpdate"/> is the third shape: a stop that has to be provable rather than
/// merely requested, because the update that follows replaces the files the daemon holds open.
/// </para>
/// </summary>
internal sealed class DaemonSupervisor : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Pause between pipe probes while waiting for a unit-managed daemon to serve.</summary>
    private static readonly TimeSpan UnitProbeInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long the supervisor waits for the unit's daemon to open its pipe after asking
    /// systemd to start it. <c>systemctl start</c> returns once the unit is started, not
    /// once the daemon serves, and the daemon's own startup is the same few hundred
    /// milliseconds the GUI's connect grace covers.
    /// </summary>
    private static readonly TimeSpan DefaultUnitStartupWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a recovery waits for the ended daemon to let go of the mapping pipe and
    /// the injection slot. The kernel releases both with the process, so a holder that
    /// outlives this budget is a daemon this process could not end - and a replacement
    /// started beside it would be the second injector the instance slot exists to
    /// prevent, so the recovery reports failure instead.
    /// </summary>
    private static readonly TimeSpan DefaultReleaseWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a recovery waits for its replacement daemon to open the mapping pipe.
    /// Covers the unit's own startup, which is slower than the owned child's.
    /// </summary>
    private static readonly TimeSpan DefaultRecoveryVerifyWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a recovery waits for an elevated copy to finish its takeover. Larger
    /// than every other budget because it covers the UAC consent the relaunch waits on:
    /// a prompt nobody answers holds the copy before it does any work at all, and a
    /// declined one fails the relaunch itself long before this budget matters.
    /// </summary>
    private static readonly TimeSpan DefaultElevatedReplacementWait = TimeSpan.FromSeconds(60);

    private readonly IElevationService _elevation;
    private readonly ProcessLaunch _launcher;
    private readonly Func<bool> _isWindows;
    private readonly Func<bool> _isDaemonReachable;
    private readonly Func<CancellationToken, Task<bool>> _requestShutdown;
    private readonly Func<bool> _isDaemonUnitInstalled;
    private readonly Func<bool> _startDaemonUnit;
    private readonly Func<int, DaemonProcessLiveness> _probeProcessLiveness;
    private readonly Func<int, DateTimeOffset, bool> _stopOnlyElevatedRecovery;
    private readonly TimeSpan _unitStartupWait;
    private readonly TimeSpan _stopTimeout;
    private readonly IDaemonRecovery _recovery;
    private readonly TimeSpan _releaseWait;
    private readonly TimeSpan _verifyWait;
    private readonly TimeSpan _elevatedReplacementWait;
    private IProcessHandle? _daemon;
    private bool _elevatedRequested;
    private bool _disposed;

    /// <summary>
    /// The daemon identity the last served snapshot named, or null when no snapshot has
    /// arrived. The recovery ends the daemon by this identity - never by a PID alone.
    /// </summary>
    private DaemonIdentity? _daemonIdentity;

    public DaemonSupervisor(
        IElevationService? elevation = null,
        ProcessLaunch? launch = null,
        Func<bool>? isWindows = null,
        Func<bool>? isDaemonReachable = null,
        Func<CancellationToken, Task<bool>>? requestShutdown = null,
        TimeSpan? stopTimeout = null,
        Func<bool>? isDaemonUnitInstalled = null,
        Func<bool>? startDaemonUnit = null,
        TimeSpan? unitStartupWait = null,
        IDaemonRecovery? recovery = null,
        TimeSpan? releaseWait = null,
        TimeSpan? verifyWait = null,
        TimeSpan? elevatedReplacementWait = null,
        Func<int, DaemonProcessLiveness>? probeProcessLiveness = null,
        Func<int, DateTimeOffset, bool>? stopOnlyElevatedRecovery = null)
    {
        _elevation = elevation ?? new ElevationService();
        _launcher = launch ?? ProcessLauncher.Start;
        _isWindows = isWindows ?? (static () => OperatingSystem.IsWindows());
        _isDaemonReachable = isDaemonReachable ?? (static () => IsDaemonReachable(IpcContract.PipeName));
        _requestShutdown = requestShutdown ?? RequestDaemonShutdownAsync;
        _stopTimeout = stopTimeout ?? StopTimeout;
        _isDaemonUnitInstalled = isDaemonUnitInstalled ?? IsDaemonUnitInstalledDefault;
        _startDaemonUnit = startDaemonUnit ?? StartDaemonUnitDefault;
        _unitStartupWait = unitStartupWait ?? DefaultUnitStartupWait;
        _recovery = recovery ?? new SystemDaemonRecovery();
        _releaseWait = releaseWait ?? DefaultReleaseWait;
        _verifyWait = verifyWait ?? DefaultRecoveryVerifyWait;
        _elevatedReplacementWait = elevatedReplacementWait ?? DefaultElevatedReplacementWait;

        // A seam rather than an IDaemonRecovery member, so the existing recovery fakes keep
        // compiling: the update's stop is the only caller, and its whole job is to tell
        // "gone" from "cannot tell".
        _probeProcessLiveness = probeProcessLiveness ?? ProbeProcessLiveness;

        // The stop-only elevated copy: same identity, same token, no replacement daemon. A
        // seam for the same reason - production launches a real elevated copy, tests record
        // the request.
        _stopOnlyElevatedRecovery = stopOnlyElevatedRecovery ?? DefaultStopOnlyElevatedRecovery;
    }

    public bool IsRunning => _daemon is { HasExited: false };

    public DaemonSupervisionState State { get; private set; } = DaemonSupervisionState.Stopped;

    public string? LastError { get; private set; }

    /// <summary>
    /// Starts the daemon when this supervisor does not already hold a live one and none is
    /// already reachable. A daemon whose pipe never answers is not something the tray acts on:
    /// it stays resident and the next menu action tries again. There is no local-engine
    /// fallback - the daemon is the only mapping authority, and a tray that injected on its
    /// own behalf would be the UIPI-silent-drop failure the architecture exists to prevent.
    /// <para>
    /// On Linux with the systemd user unit installed, the unit owns the resident daemon: the
    /// supervisor asks systemd for it - the same action the settings card's 启动常驻 takes -
    /// instead of spawning a private child. The child is the section 4.7 competition in
    /// reverse: the unit's own instance exits without competing while mapping keeps running
    /// under the child, which leaves the settings card reading 未运行 for a daemon that is
    /// very much alive, and its start/stop buttons operating on the instance that already
    /// left. The owned child is the fallback only when the unit cannot be started at all
    /// (no session bus, a broken ExecStart) - never once the unit has taken ownership, not
    /// even while its daemon is still opening its pipe: a daemon that maps outranks a
    /// coherent service state, and a second one competes with the first.
    /// </para>
    /// </summary>
    public void EnsureRunning()
    {
        if (_disposed || IsRunning)
        {
            return;
        }

        if (SafeIsDaemonReachable())
        {
            State = DaemonSupervisionState.Running;
            return;
        }

        if (!_isWindows() && SafeIsDaemonUnitInstalled() && TryStartUnitDaemon())
        {
            State = DaemonSupervisionState.Running;
            return;
        }

        if (_isWindows())
        {
            if (_elevatedRequested)
            {
                return;
            }

            _elevatedRequested = true;
            State = _elevation.RelaunchElevated("--daemon")
                ? DaemonSupervisionState.Running
                : DaemonSupervisionState.StartFailed;
            return;
        }

        _daemon = _launcher(ProcessLauncher.DaemonStartInfo());
        State = _daemon is null ? DaemonSupervisionState.StartFailed : DaemonSupervisionState.Running;
        Trace.WriteLine($"[DeziroJoy] daemon supervisor: {State}");
    }

    /// <summary>
    /// Asks the systemd user unit for its daemon and waits for the pipe. The reachability
    /// re-check is not redundant: the probe that sent us here can lose a race against a
    /// daemon that is mid-start, and asking systemd to start an already-active unit is the
    /// no-op that keeps this supervisor from competing with it.
    /// <para>
    /// A start that failed returns false, and the caller falls back to the owned child - the
    /// path that works with no systemd at all. A start that succeeded never does: from that
    /// moment the unit owns the daemon, so a pipe that is late to open still returns true.
    /// Spawning a private child beside a started unit is the section 4.7 competition in
    /// reverse - the unit's own instance exits without competing while mapping keeps running
    /// under the child - which is exactly the state this path exists to prevent. The
    /// controller reports the connection state while the daemon is still on its way.
    /// </para>
    /// </summary>
    private bool TryStartUnitDaemon()
    {
        if (SafeIsDaemonReachable())
        {
            return true;
        }

        if (!SafeStartDaemonUnit())
        {
            return false;
        }

        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < _unitStartupWait)
        {
            Thread.Sleep(UnitProbeInterval);
            if (SafeIsDaemonReachable())
            {
                return true;
            }
        }

        LastError ??= "the unit's daemon did not open the mapping pipe within the startup wait";
        return true;
    }

    /// <summary>
    /// The production unit check: the unit file on disk is the install - the same fact the
    /// settings card reads - so this is that file check behind the refresh the service needs
    /// before its properties answer.
    /// </summary>
    private static bool IsDaemonUnitInstalledDefault()
    {
        var service = new LinuxDaemonService();
        service.Refresh();
        return service.IsDaemonInstalled;
    }

    /// <summary>
    /// The production unit start: the same <c>systemctl --user start</c> the settings card's
    /// 启动常驻 runs, so the daemon has exactly one owner whichever surface asked for it.
    /// </summary>
    private static bool StartDaemonUnitDefault()
    {
        var service = new LinuxDaemonService();
        service.Refresh();
        return service.StartDaemon().Result == DaemonOp.Completed;
    }

    /// <summary>
    /// Asks the daemon to exit over IPC before waiting and, for a child we own, killing only
    /// what survived the bounded graceful wait. A remote daemon that cannot confirm its exit
    /// reports failure so the caller can keep its UI alive instead of pretending to have exited.
    /// </summary>
    public bool Stop()
    {
        if (_daemon is { HasExited: true } exited)
        {
            _daemon = null;
            exited.Dispose();
        }

        var daemon = _daemon;
        if (daemon is null && !_elevatedRequested && !SafeIsDaemonReachable())
        {
            State = DaemonSupervisionState.Stopped;
            LastError = null;
            return true;
        }

        State = DaemonSupervisionState.Stopping;
        LastError = null;
        var acknowledged = false;

        try
        {
            using var timeout = new CancellationTokenSource(_stopTimeout);
            acknowledged = _requestShutdown(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }

        var stopped = false;
        var killed = false;
        if (daemon is not null)
        {
            if (acknowledged)
            {
                TryWait(daemon, _stopTimeout);
            }

            if (!daemon.HasExited)
            {
                killed = true;
                TryKill(daemon);
                TryWait(daemon, _stopTimeout);
            }

            stopped = daemon.HasExited;
            if (stopped)
            {
                _daemon = null;
                daemon.Dispose();
            }
        }
        else if (acknowledged)
        {
            stopped = WaitUntilUnreachable(_stopTimeout);
        }

        State = stopped
            ? killed
                ? DaemonSupervisionState.Killed
                : acknowledged ? DaemonSupervisionState.StoppedGracefully : DaemonSupervisionState.Stopped
            : DaemonSupervisionState.StopFailed;
        if (stopped)
        {
            _elevatedRequested = false;
            LastError = null;
        }
        else
        {
            LastError ??= "the daemon did not confirm its exit within the shutdown budget";
        }

        Trace.WriteLine($"[DeziroJoy] daemon supervisor: {State}; {LastError}");
        return stopped;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _daemon?.Dispose();
        _daemon = null;
    }

    /// <summary>
    /// Stops the daemon for an update: the stop an installer can be started behind. The update
    /// replaces the install directory, so a daemon that is still resident when the setup begins
    /// holds <c>DeziroJoy.exe</c> and the libraries beside it open, and the setup's DeleteFile
    /// fails with access denied (error 5) and rolls back - which is what a user saw after
    /// clicking update: the app closed, nothing was installed, and nothing said why.
    /// <para>
    /// The chain is the graceful stop first, then the end the recovery performs - run directly,
    /// because <see cref="Recover"/>'s own first step is a graceful stop, and a graceful stop
    /// that finds the pipe already gone reports "nothing resident" and skips exactly the end
    /// this chain needs. The platform end is asynchronous, so the proof that follows is a
    /// bounded wait rather than an immediate check. An update never calls <see cref="Recover"/>:
    /// starting a replacement just to stop it again races the install and serves no purpose.
    /// </para>
    /// <para>
    /// The proof is the process, not the pipe. A daemon this caller cannot open - the elevated
    /// one on Windows, whose handle a medium-integrity process is denied - is answered by the
    /// stop-only elevated copy: it verifies the identity with its own token, ends the process
    /// and exits without becoming the replacement, so no second injector is ever started and
    /// nothing is ended without a confirmed identity. A daemon nobody could prove gone is a
    /// failed stop, reported as one, and the caller keeps its UI alive instead of starting an
    /// installer against a daemon that is still holding the files.
    /// </para>
    /// </summary>
    public bool StopForUpdate()
    {
        if (_disposed)
        {
            LastError = "the supervisor is disposed";
            return false;
        }

        // 1. The graceful stop, exactly as the exit path runs it.
        Stop();
        if (IsDaemonGoneForUpdate())
        {
            LastError = null;
            return true;
        }

        // 2. The end the recovery performs, run here rather than through Recover(): its first
        //    step is the graceful stop above, which for a daemon whose pipe is already gone
        //    answers "nothing resident" and skips the identity-confirmed end - the very end
        //    that reaches a daemon this process holds no handle to.
        EndDaemonForUpdate();
        if (IsDaemonGoneForUpdate())
        {
            LastError = null;
            return true;
        }

        // 3. Ending a scheduled task or a service returns before its process necessarily exits.
        //    Wait for that end instead of recovering a replacement which the update would only
        //    have to stop again.
        if (WaitForDaemonGoneForUpdate())
        {
            return true;
        }

        // 4. A daemon this caller still cannot prove gone is the elevated one no planned task
        //    owns; the stop-only elevated copy is the end that reaches it.
        RequestStopOnlyElevatedDaemon();
        return WaitForDaemonGoneForUpdate();
    }

    /// <summary>
    /// The end the recovery performs, without the restart that follows it: the owner's own
    /// service end (the systemd user unit's stop, the Windows planned task's end) and the
    /// identity-confirmed process end. The recovery's elevated end-and-replace is deliberately
    /// excluded: the update's later stop-only elevated request ends the process without starting
    /// anything, which is what makes this the update's end rather than the recovery's.
    /// </summary>
    private void EndDaemonForUpdate()
    {
        var phases = new List<string>();
        TryEndDaemon(phases, out _, out _, allowElevatedReplacement: false);
    }

    /// <summary>
    /// Stops the daemon and brings a fresh one up: the safe restart a backend switch needs.
    /// The provider is built into the daemon's composition at startup and there is no hot
    /// swap of a live one, so a new selection only takes effect on a new process - and the
    /// stop is the graceful IPC one, which releases every held key before the new daemon
    /// starts. Whichever surface owns the daemon brings the replacement up: the elevated
    /// relaunch on Windows, the systemd user unit where it is installed, and the owned child
    /// everywhere else.
    /// <para>
    /// A stop that never confirmed the old daemon's exit no longer ends the restart: the
    /// recovery ends that daemon by identity - or through the owner's own service, the
    /// systemd unit or the planned task - and only starts a replacement once the mapping
    /// pipe, the instance slot and the injection slot prove the old one is gone. A daemon
    /// that cannot be ended is a failed restart and is reported as one: the caller keeps
    /// the old daemon - and the backend it runs - rather than claiming a switch that did
    /// not happen. The return value is also never proof of the requested backend: that
    /// answer lives in the new daemon's own startup line and in the snapshot it serves,
    /// so a caller reports the two separately.
    /// </para>
    /// </summary>
    public bool Restart() => Recover().Recovered;

    /// <summary>
    /// Records the daemon identity a served snapshot named. The stamp is taken here, at
    /// the moment the daemon was seen alive, because it is the upper bound the recovery
    /// needs: a process that started after this stamp is not the daemon the snapshot
    /// named, and ending it would be the PID-reuse mistake this identity exists to
    /// prevent.
    /// </summary>
    public void RecordDaemonPid(int processId)
    {
        if (processId > 0)
        {
            _daemonIdentity = new DaemonIdentity(processId, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// The bounded restart that brings back a daemon which stopped answering. A client
    /// reconnect cannot fix one: the process that holds the mapping pipe, the injection
    /// mutex and the instance slot is still resident, so a new daemon would find the
    /// slot taken and exit without competing - which is exactly why the user ends up in
    /// Task Manager. Every phase is bounded and reported:
    /// the graceful stop; the end for a daemon the graceful stop could not confirm (the
    /// systemd unit's stop, the planned task's end, the identity-confirmed process end,
    /// or the elevated copy's verified end-and-replace); the release wait; the start
    /// through the owner's own path; and the proof - a daemon that never opens its
    /// mapping pipe is not a recovery.
    /// </summary>
    public DaemonRecoveryReport Recover()
    {
        if (_disposed)
        {
            return new DaemonRecoveryReport(false, [], "the supervisor is disposed");
        }

        var phases = new List<string>();

        // 1. The graceful stop. A healthy daemon exits here and the recovery is just a
        // restart; the stop's own bounded wait is also what releases every held key.
        var graceful = Stop();
        if (graceful)
        {
            phases.Add(State == DaemonSupervisionState.Stopped
                ? "没有需要停止的守护进程"
                : "旧守护进程已停止");
        }

        // 2. End what the graceful stop could not confirm.
        var elevatedRecoveryRequested = false;
        if (!graceful)
        {
            if (!TryEndDaemon(phases, out var endFailure, out elevatedRecoveryRequested))
            {
                return new DaemonRecoveryReport(false, phases, endFailure);
            }
        }

        // 3. An elevated copy that took over the daemon is both the end and the start:
        // it verifies the identity with its own token, ends the old process, waits for
        // the slots and becomes the replacement - so this process waits for the
        // replacement, not for a release it would compete with. The budget covers the
        // UAC consent the relaunch waits on, which no smaller bound survives; a
        // declined prompt fails at once instead, when the relaunch itself refuses.
        if (elevatedRecoveryRequested)
        {
            if (!WaitForElevatedReplacement(out var takeoverFailure))
            {
                return new DaemonRecoveryReport(false, phases, takeoverFailure);
            }

            phases.Add("提权副本已接替常驻守护进程");
            return Succeeded(phases);
        }

        // 4. Wait for the old daemon to let go. Starting a replacement beside a live
        // one is the double injection the instance slot exists to prevent, so this
        // wait is the recovery's safety gate, not a formality.
        if (!WaitForDaemonRelease(out var releaseFailure))
        {
            // A daemon that answers while the one we ended stays dead is a daemon the
            // resident service put back on its own - the unit's Restart=on-failure -
            // and that is the recovery. The proof is the release wait's own, minus the
            // release: the ended process is gone, a live daemon owns the instance slot
            // (held for a daemon's whole life, which is what proves the answering pipe
            // belongs to a real daemon and not to something that outlived its process),
            // and the mapping pipe answers. The injection slot needs no separate proof
            // here: the kernel released it with the ended process, so any holder now is
            // the live replacement. A daemon we could not end is not a recovery.
            if (_daemonIdentity is { } identity
                && _recovery.TryGetStartTime(identity.ProcessId) is null
                && SafeIsDaemonReachable()
                && !SafeIsDaemonInstanceSlotFree())
            {
                phases.Add("常驻服务已自行恢复守护进程");
                return Succeeded(phases);
            }

            return new DaemonRecoveryReport(false, phases, releaseFailure);
        }

        phases.Add("映射通道与注入槽位已释放");

        // 5. Bring a fresh daemon up through the owner's own path. On Windows with the
        // planned task installed the task is that path: its start needs no UAC consent,
        // while an elevated relaunch would prompt on every recovery.
        if (_isWindows() && SafeIsDaemonTaskInstalled())
        {
            if (!SafeStartDaemonTask())
            {
                return new DaemonRecoveryReport(false, phases, LastError ?? "计划任务无法启动提权守护进程");
            }

            phases.Add("已通过计划任务启动提权守护进程");
        }
        else
        {
            // The old elevated daemon is gone: resetting the flag is what lets the
            // relaunch below start its replacement instead of standing down.
            _elevatedRequested = false;
            EnsureRunning();
        }

        // 6. Prove it. The owned child counts as ready the moment it is launched; every
        // other owner has to open the mapping pipe first.
        if (IsRunning || WaitUntilReachable(_verifyWait))
        {
            phases.Add("新守护进程已就绪");
            return Succeeded(phases);
        }

        return new DaemonRecoveryReport(
            false,
            phases,
            LastError ?? "替换的守护进程未在启动预算内打开映射通道");
    }

    /// <summary>
    /// The success landing every recovery path shares: the state is reset to match what
    /// was just proven, so a supervisor whose elevated daemon was replaced does not keep
    /// standing down from the relaunch that asked for the old one, and the next
    /// <see cref="EnsureRunning"/> sees a daemon that answers rather than a latched flag.
    /// </summary>
    private DaemonRecoveryReport Succeeded(List<string> phases)
    {
        LastError = null;
        State = DaemonSupervisionState.Running;
        _elevatedRequested = false;
        return new DaemonRecoveryReport(true, phases, null);
    }

    /// <summary>
    /// Waits for the elevated copy to finish the takeover: the old identified process
    /// gone and the mapping pipe answering. Both halves matter - a pipe that answers
    /// while the old process still lives is the daemon that was supposed to be ended,
    /// not its replacement. The wait is bounded by the UAC budget; a copy the user
    /// declined never starts, and the relaunch itself reports that as a failure before
    /// this wait begins.
    /// </summary>
    private bool WaitForElevatedReplacement(out string? failure)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            if (_daemonIdentity is { } identity
                && _recovery.TryGetStartTime(identity.ProcessId) is null
                && SafeIsDaemonReachable())
            {
                failure = null;
                return true;
            }

            if (elapsed.Elapsed >= _elevatedReplacementWait)
            {
                failure = IsIdentifiedDaemonAlive()
                    ? "提权副本未在预算内结束旧守护进程"
                    : "提权副本未在预算内接替守护进程（映射通道未打开）";
                return false;
            }

            Thread.Sleep(UnitProbeInterval);
        }
        while (true);
    }

    /// <summary>
    /// Ends a daemon the graceful stop could not confirm. The platform's own end comes
    /// first and only where it owns the daemon: the systemd user unit's stop and the
    /// planned task's end are the only ends that reach a daemon this process holds no
    /// handle to and cannot signal directly - the elevated one on Windows - so when one
    /// of them was issued it <em>is</em> the end, and the release wait is what proves it
    /// landed. Without one, the daemon is ended by identity: the PID a snapshot named,
    /// confirmed against the process's start time and executable, or every live daemon
    /// process found by entry path and argument. Last of all, on Windows, a daemon that
    /// is alive but cannot be read or ended from this token is handed to an elevated
    /// copy of this product with the same confirmed identity - it re-verifies, ends and
    /// replaces, which is the one end that reaches an elevated daemon no planned task
    /// owns. Nothing is ever ended without a confirmed identity.
    /// </summary>
    private bool TryEndDaemon(
        List<string> phases,
        out string? failure,
        out bool elevatedRecoveryRequested,
        bool allowElevatedReplacement = true)
    {
        failure = null;
        elevatedRecoveryRequested = false;
        var platformEndIssued = false;

        if (!_isWindows() && SafeIsDaemonUnitInstalled() && SafeStopDaemonUnit())
        {
            phases.Add("已通过 systemd 用户单元停止常驻服务");
            platformEndIssued = true;
        }
        else if (_isWindows() && SafeIsDaemonTaskInstalled() && SafeEndDaemonTask())
        {
            phases.Add("已通过计划任务结束提权实例");
            platformEndIssued = true;
        }

        var identified = TryEndIdentifiedDaemon();
        if (identified)
        {
            phases.Add("已按身份确认结束旧守护进程");
        }

        if (platformEndIssued)
        {
            // The platform end was issued for the daemon it owns. A daemon that is not
            // the one the unit or the task owns - a stray child from an earlier session -
            // was ended by identity above when this process could confirm it; an
            // executable this process cannot read is the platform end's to finish, and
            // the release wait adjudicates either way.
            return true;
        }

        if (identified)
        {
            return true;
        }

        if (IsIdentifiedDaemonAlive())
        {
            // Windows, no planned task, and the daemon runs with a token this process
            // does not have: its executable cannot be read and it cannot be ended from
            // here - which is why the identity check above refused rather than killed.
            // An elevated copy of this product, handed the same identity, re-verifies it
            // with its own token before ending anything, so the hand-off is not a blind
            // kill; it is also the replacement, so this supervisor starts nothing beside
            // it. A PID that was reused is never handed over.
            if (allowElevatedReplacement
                && _isWindows()
                && _daemonIdentity is { } identity
                && RecordedIdentityIsLiveAndNotReused()
                && SafeStartElevatedRecovery(identity))
            {
                elevatedRecoveryRequested = true;
                return true;
            }

            failure = LastError ?? "守护进程无法被结束";
            return false;
        }

        if (SafeIsDaemonReachable())
        {
            // Something still answers the pipe but nothing identifiable is running:
            // a replacement started now would be the second injector, and this process
            // cannot prove the old one is gone - so this is a failure, not a start.
            failure = "守护进程仍在响应映射通道，但找不到可确认身份的守护进程可结束";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The update's proof that no daemon is resident. Three things have to hold, because each
    /// one alone is a state a dying daemon passes through: the mapping pipe answers for
    /// nobody, the daemon a snapshot named is verifiably gone, and neither the instance slot
    /// nor the injection mutex is held.
    /// <para>
    /// "Verifiably gone" is the part that was wrong before. A PID this caller cannot open is
    /// not gone - it is an elevated daemon whose handle a medium-integrity process is denied,
    /// and it can still be holding the install directory's files open. So the liveness probe
    /// tells the three cases apart, and only <see cref="DaemonProcessLiveness.Gone"/> counts:
    /// <see cref="DaemonProcessLiveness.Unreadable"/> sends the caller to the stop-only
    /// elevated copy, which proves the end with a token that can see the process.
    /// </para>
    /// </summary>
    private bool IsDaemonGoneForUpdate()
    {
        if (SafeIsDaemonReachable())
        {
            return false;
        }

        if (_daemonIdentity is not null
            && _probeProcessLiveness(_daemonIdentity.ProcessId) != DaemonProcessLiveness.Gone)
        {
            return false;
        }

        // A daemon the owner's own service put back is not the recorded identity - it has a
        // different PID - so the scan is what catches it. On Windows a cross-integrity scan
        // finds nothing, which costs nothing: the pipe and the slots still have to answer.
        if (_recovery.FindDaemonProcesses().Count > 0)
        {
            return false;
        }

        return SafeIsDaemonInstanceSlotFree() && SafeIsInjectionSlotFree();
    }

    /// <summary>Bounded wait for <see cref="IsDaemonGoneForUpdate"/> to hold.</summary>
    private bool WaitForDaemonGoneForUpdate()
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            if (IsDaemonGoneForUpdate())
            {
                LastError = null;
                return true;
            }

            if (elapsed.Elapsed >= _releaseWait)
            {
                // The conclusive answer, whatever the escalation said on the way: an update
                // that cannot prove the daemon gone must not start, and the user is told which
                // of the two it was rather than a generic failure.
                LastError = _probeProcessLiveness(_daemonIdentity?.ProcessId ?? 0) == DaemonProcessLiveness.Running
                    ? "守护进程未在停止预算内退出，更新已取消；映射功能不受影响，请重试。"
                    : "无法确认守护进程已退出（权限不足？），更新已取消；映射功能不受影响，请重试。";
                return false;
            }

            Thread.Sleep(ProbeRetryDelay);
        }
        while (true);
    }

    /// <summary>
    /// Hands the daemon that only an elevated token can end to an elevated copy of this product
    /// that stops it and exits. It is not <see cref="Recover"/>'s copy: nothing is started here,
    /// because an update has no use for a replacement daemon - the install directory is about to
    /// be replaced - and a replacement would be a daemon this method has to stop again. The copy
    /// re-verifies the same identity with its own token before ending anything, so the hand-off
    /// is not a blind kill and a reused PID is never handed over.
    /// </summary>
    private void RequestStopOnlyElevatedDaemon()
    {
        if (!_isWindows() || _daemonIdentity is not { } identity)
        {
            return;
        }

        // A PID that was reused is never handed over, exactly as in TryEndDaemon.
        if (!RecordedIdentityIsLiveAndNotReused())
        {
            return;
        }

        try
        {
            if (!_stopOnlyElevatedRecovery(identity.ProcessId, identity.SeenAtUtc))
            {
                LastError ??= "无法请求提权停止守护进程（用户取消了提权？）";
            }
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
        {
            LastError = exception.Message;
        }
    }

    /// <summary>
    /// The production stop-only elevated request: an elevated copy of this product, handed the
    /// same identity, that ends the daemon with its own token and exits. It is the only end that
    /// reaches a daemon this process cannot open - the elevated one on Windows that no planned
    /// task owns - and it is deliberately not the recovery's copy: nothing is started, so an
    /// update that needs the daemon gone does not get a replacement it would have to stop again.
    /// </summary>
    private static bool DefaultStopOnlyElevatedRecovery(int processId, DateTimeOffset seenAt)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // Nothing but the identity travels, exactly as for the recovery's elevated copy - the
        // PID and the moment it was last seen serving the daemon - plus the stop-only flag that
        // tells the copy to end and exit instead of becoming the replacement.
        var arguments =
            $"{ElevatedDaemonRecovery.StopOnlyFlag} --pid {processId.ToString(CultureInfo.InvariantCulture)} "
            + $"--seen-at {seenAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}";
        return WindowsProcessElevation.RelaunchElevated(arguments);
    }

    /// <summary>
    /// The production liveness probe, and the reason the update's proof is honest: a process
    /// that is not running answers <see cref="ArgumentException"/> from the process table, and a
    /// process that is running but cannot be opened answers <see cref="Win32Exception"/>. The
    /// two used to be one answer - "cannot tell, assume it exited" - and that assumption is what
    /// let an update start an installer against a daemon that was still holding its files.
    /// </summary>
    private static DaemonProcessLiveness ProbeProcessLiveness(int processId)
    {
        if (processId <= 0)
        {
            return DaemonProcessLiveness.Gone;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? DaemonProcessLiveness.Gone : DaemonProcessLiveness.Running;
        }
        catch (ArgumentException)
        {
            // No such process: the goal state.
            return DaemonProcessLiveness.Gone;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // There is a process there and this token cannot open it. Not an answer either way,
            // which is why it is its own case.
            Trace.WriteLine(
                $"[DeziroJoy] daemon supervisor: process {processId} could not be opened ({exception.GetType().Name}); liveness unknown");
            return DaemonProcessLiveness.Unreadable;
        }
    }

    /// <summary>
    /// Ends the daemon by identity. The recorded identity comes first - the PID a
    /// snapshot named, re-confirmed against the process's start time and executable
    /// before any kill, because a PID alone proves nothing: the original daemon may
    /// have died and the number may since have been handed to an unrelated process.
    /// With no usable record, every live daemon process - this product's entry path
    /// with the <c>--daemon</c> argument - is the daemon by identity.
    /// </summary>
    private bool TryEndIdentifiedDaemon()
    {
        if (_daemonIdentity is { } identity
            && _recovery.TryGetStartTime(identity.ProcessId) is { } startTime)
        {
            if (startTime > identity.SeenAtUtc)
            {
                // The process started after this PID was last seen serving the daemon:
                // the daemon died and the number was reused. Ending it would kill a
                // process this supervisor has no authority over.
                LastError = "记录中的守护进程 PID 已属于更新的进程，拒绝结束（PID 复用保护）";
                return false;
            }

            if (!SafeIsDaemonExecutable(identity.ProcessId))
            {
                LastError = "记录中的守护进程 PID 已不再运行守护进程可执行文件，拒绝结束";
                return false;
            }

            if (SafeEndProcess(identity.ProcessId))
            {
                return true;
            }

            LastError = "守护进程无法被结束（可能权限不足）";
            return false;
        }

        var candidates = _recovery.FindDaemonProcesses();
        foreach (var candidate in candidates)
        {
            if (SafeEndProcess(candidate.ProcessId))
            {
                // The scan's identity is recorded so the release wait below can watch
                // exactly the process that was ended.
                _daemonIdentity = new DaemonIdentity(candidate.ProcessId, DateTimeOffset.UtcNow);
                return true;
            }
        }

        LastError = candidates.Count > 0
            ? "守护进程无法被结束（可能权限不足）"
            : "找不到可确认身份的守护进程可结束";
        return false;
    }

    /// <summary>True when the daemon process the recovery identified is still alive.</summary>
    private bool IsIdentifiedDaemonAlive()
        => _daemonIdentity is { } identity && _recovery.TryGetStartTime(identity.ProcessId) is not null;

    /// <summary>
    /// True when the recorded identity is still usable as a hand-off: the process is
    /// alive and started no later than the snapshot that named it. A PID whose process
    /// started after that stamp was reused, and handing it to an elevated copy would be
    /// asking for a kill of a process this supervisor has no authority over.
    /// </summary>
    private bool RecordedIdentityIsLiveAndNotReused()
        => _daemonIdentity is { } identity
            && _recovery.TryGetStartTime(identity.ProcessId) is { } startTime
            && startTime <= identity.SeenAtUtc;

    /// <summary>
    /// Bounded wait for the ended daemon to release everything a replacement would
    /// collide with: the mapping pipe (held for the daemon's whole life), the daemon
    /// instance slot, and the injection mutex - the kernel releases the last two with
    /// the process, so the identified process's exit and the instance slot together
    /// prove the injection mutex is free too. A slot this process cannot open - the
    /// elevated daemon's mutex on Windows - says nothing either way, and the
    /// process-liveness check beside it is what carries the proof there.
    /// </summary>
    private bool WaitForDaemonRelease(out string? failure)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            if (IsDaemonReleased())
            {
                failure = null;
                return true;
            }

            if (elapsed.Elapsed >= _releaseWait)
            {
                failure = IsIdentifiedDaemonAlive()
                    ? "守护进程未在释放预算内退出"
                    : "映射通道或注入槽位未在释放预算内释放";
                return false;
            }

            Thread.Sleep(ProbeRetryDelay);
        }
        while (true);

        bool IsDaemonReleased()
            => !IsIdentifiedDaemonAlive()
                && !SafeIsDaemonReachable()
                && SafeIsDaemonInstanceSlotFree()
                && SafeIsInjectionSlotFree();
    }

    /// <summary>Bounded wait for a daemon - any daemon - to open the mapping pipe.</summary>
    private bool WaitUntilReachable(TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            if (SafeIsDaemonReachable())
            {
                return true;
            }

            if (elapsed.Elapsed >= timeout)
            {
                return false;
            }

            Thread.Sleep(UnitProbeInterval);
        }
        while (true);
    }

    private bool SafeIsDaemonExecutable(int processId)
    {
        try
        {
            return _recovery.IsDaemonExecutable(processId);
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    private bool SafeEndProcess(int processId)
    {
        try
        {
            return _recovery.EndProcess(processId);
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    private bool SafeIsDaemonInstanceSlotFree()
    {
        try
        {
            return _recovery.IsDaemonInstanceSlotFree();
        }
        catch (Exception exception) when (exception
            is UnauthorizedAccessException or IOException or Win32Exception or InvalidOperationException)
        {
            // A slot this process cannot open says nothing: the kernel releases it with
            // the holder, and the process-liveness check beside it carries the proof.
            return !IsIdentifiedDaemonAlive();
        }
    }

    private bool SafeIsInjectionSlotFree()
    {
        try
        {
            return _recovery.IsInjectionSlotFree();
        }
        catch (Exception exception) when (exception
            is UnauthorizedAccessException or IOException or Win32Exception or InvalidOperationException)
        {
            return !IsIdentifiedDaemonAlive();
        }
    }

    private bool SafeIsDaemonTaskInstalled()
    {
        try
        {
            return _recovery.IsDaemonTaskInstalled();
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    private bool SafeEndDaemonTask()
    {
        try
        {
            return _recovery.EndDaemonTask();
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    private bool SafeStartDaemonTask()
    {
        try
        {
            return _recovery.StartDaemonTask();
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    private bool SafeStopDaemonUnit()
    {
        try
        {
            return _recovery.StopDaemonUnit();
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Hands the confirmed daemon identity to an elevated copy of this product. A
    /// relaunch that was declined - the UAC prompt dismissed - is reported as the
    /// failure it is, with the way out, rather than being retried against the daemon
    /// this process cannot end.
    /// </summary>
    private bool SafeStartElevatedRecovery(DaemonIdentity identity)
    {
        try
        {
            if (_recovery.StartElevatedRecovery(identity.ProcessId, identity.SeenAtUtc))
            {
                return true;
            }

            // Overwrite, not ??=: the message that matters now is the one about the
            // hand-off that just failed, not the identity check that sent us here.
            LastError = "未获得管理员权限以结束提权守护进程（可能取消了 UAC 提示）；"
                + "请在提权提示中允许，或以管理员身份运行后重试。";
            return false;
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// How long one connect attempt waits for the daemon's mapping pipe to answer - the same
    /// budget the daemon itself uses to detect a peer. The daemon holds the pipe open for its
    /// whole life, so a connect that cannot complete in this window means no daemon is there.
    /// </summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Pause between probe attempts. The daemon-mid-startup race is exactly this wide: the
    /// process exists but has not created its pipe yet, so one failed attempt proves nothing.
    /// </summary>
    private static readonly TimeSpan ProbeRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Attempts before the probe concludes that no daemon is reachable.</summary>
    private const int ProbeAttempts = 3;

    /// <summary>
    /// True when something answers on <paramref name="pipeName"/>. The pipe, not a process or
    /// a mutex, is the liveness signal: the daemon listens for its whole life, so a connect
    /// that succeeds means the mapping authority is already up.
    /// </summary>
    internal static bool IsDaemonReachable(string pipeName)
    {
        for (var attempt = 0; attempt < ProbeAttempts; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(ProbeRetryDelay);
            }

            try
            {
                using var client = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.InOut, PipeOptions.None);
                client.Connect(ProbeTimeout);
                return client.IsConnected;
            }
            catch (Exception exception) when (exception
                is TimeoutException or IOException or UnauthorizedAccessException)
            {
                // Nothing answered within the budget: retry, then conclude unreachable.
            }
        }

        return false;
    }

    private bool SafeIsDaemonReachable()
    {
        try
        {
            return _isDaemonReachable();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// The unit check behind its own guard: a query that throws says nothing about whether
    /// the unit is installed, so it is reported and treated as "no unit" - which sends the
    /// supervisor down the child path that works without systemd at all.
    /// </summary>
    private bool SafeIsDaemonUnitInstalled()
    {
        try
        {
            return _isDaemonUnitInstalled();
        }
        catch (Exception exception) when (exception
            is IOException or TimeoutException or UnauthorizedAccessException or Win32Exception)
        {
            LastError = exception.Message;
            return false;
        }
    }

    /// <summary>The unit start behind its own guard, with the same "reported, not guessed" rule.</summary>
    private bool SafeStartDaemonUnit()
    {
        try
        {
            return _startDaemonUnit();
        }
        catch (Exception exception) when (exception
            is IOException or TimeoutException or UnauthorizedAccessException or Win32Exception)
        {
            LastError = exception.Message;
            return false;
        }
    }

    private bool WaitUntilUnreachable(TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            if (!SafeIsDaemonReachable())
            {
                return true;
            }

            if (elapsed.Elapsed >= timeout)
            {
                return false;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(50));
        }
        while (true);
    }

    private static async Task<bool> RequestDaemonShutdownAsync(CancellationToken cancellationToken)
    {
        await using var controller = new DaemonController(new NamedPipeDaemonEndpoint());
        await controller.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return await controller.RequestShutdownAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void TryKill(IProcessHandle daemon)
    {
        try
        {
            daemon.Kill(entireTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Already gone, or not ours to terminate.
        }
    }

    private static void TryWait(IProcessHandle daemon, TimeSpan timeout)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(timeout);
            daemon.WaitForExitAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception
            is OperationCanceledException or AggregateException or InvalidOperationException)
        {
            // The fallback path inspects HasExited after this bounded wait.
        }
    }
}

/// <summary>
/// The production recovery surface: the real process table for identity-confirmed
/// ends, the real daemon instance and injection mutexes for the release proof, the
/// logon planned task on Windows and the systemd user unit on Linux for a daemon this
/// process holds no handle to. Every method answers for what it can see and says
/// "cannot tell" as false rather than guessing - a kill is only ever issued against a
/// process whose identity was confirmed here.
/// </summary>
internal sealed class SystemDaemonRecovery : IDaemonRecovery
{
    public IReadOnlyList<DaemonProcessIdentity> FindDaemonProcesses()
    {
        var found = new List<DaemonProcessIdentity>();
        var entry = EntryPath;
        if (entry is null)
        {
            return found;
        }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId || !IsDaemonEntry(process.Id, entry))
                {
                    continue;
                }

                found.Add(new DaemonProcessIdentity(process.Id, process.StartTime));
            }
            catch (Exception exception) when (exception
                is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // A process that exited underneath the scan, or one this user cannot
                // query: neither is a daemon this process may end.
            }
            finally
            {
                process.Dispose();
            }
        }

        return found;
    }

    public DateTimeOffset? TryGetStartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime;
        }
        catch (Exception exception) when (exception
            is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Gone, or a start time this user cannot read: no identity, no kill.
            return null;
        }
    }

    public bool IsDaemonExecutable(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var entry = EntryPath;

            if (entry is null)
            {
                return false;
            }

            if (OperatingSystem.IsLinux())
            {
                // The command line is the identity here: the executable alone also
                // names the render and tray processes.
                return IsDaemonEntry(processId, entry);
            }

            return string.Equals(process.MainModule?.FileName, entry, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception
            is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Gone, or an executable this process cannot read - the elevated daemon on
            // Windows. Neither confirms the identity, so the answer is no.
            return false;
        }
    }

    public bool EndProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: false);
            return true;
        }
        catch (Exception exception) when (exception
            is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, or not ours to terminate - an elevated daemon ended from an
            // unelevated caller is exactly this, and the planned task is its path.
            return false;
        }
    }

    public bool IsDaemonInstanceSlotFree() => IsSlotFree(DaemonHost.DaemonInstanceMutexName);

    public bool IsInjectionSlotFree() => IsSlotFree(InjectionMutex.DefaultName);

    public bool IsDaemonTaskInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // Bounded: a schtasks that never answers must not hold the recovery open. The
        // query itself is the settings card's, run here behind a wait so a hung
        // scheduler reads as "no task" - which sends the recovery down the identity
        // path that works without it - rather than as an indefinite hang.
        try
        {
            var service = new WindowsDaemonService();
            var refresh = Task.Run(service.Refresh);
            if (!refresh.Wait(SchedulerCallTimeout))
            {
                return false;
            }

            return service.IsDaemonInstalled;
        }
        catch (Exception exception) when (exception is AggregateException or Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public bool EndDaemonTask()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return RunSchTasks($"/End /TN \"{DaemonInstaller.TaskName}\" /F");
    }

    public bool StartDaemonTask()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return RunSchTasks($"/Run /TN \"{DaemonInstaller.TaskName}\"");
    }

    public bool StopDaemonUnit()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        // Bounded for the same reason: the unit's own stop is the service's systemctl
        // call, wrapped so a manager that never answers reads as a failed stop - which
        // falls through to the identity-confirmed end - instead of hanging the recovery.
        try
        {
            var service = new LinuxDaemonService();
            service.Refresh();
            var stop = Task.Run(() => service.StopDaemon());
            return stop.Wait(SchedulerCallTimeout) && stop.Result.Result == DaemonOp.Completed;
        }
        catch (Exception exception) when (exception is AggregateException or Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public bool StartElevatedRecovery(int processId, DateTimeOffset seenAt)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // The elevated copy re-enters this product with the recovery entry: it
        // re-verifies the identity with its own token, ends the old daemon, waits for
        // the slots and becomes the replacement. Nothing but the identity travels -
        // the PID and the moment it was last seen serving the daemon.
        var arguments =
            $"{ElevatedDaemonRecovery.Flag} --pid {processId.ToString(CultureInfo.InvariantCulture)} "
            + $"--seen-at {seenAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}";
        return WindowsProcessElevation.RelaunchElevated(arguments);
    }

    /// <summary>
    /// The entry executable this process was started as. A daemon started by this
    /// supervisor runs the same one, with <c>--daemon</c> behind it - under "dotnet run"
    /// that is the muxer, exactly as <see cref="ProcessLauncher"/> builds it.
    /// </summary>
    private static string? EntryPath
    {
        get
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) ? null : path;
        }
    }

    /// <summary>
    /// True when the process runs this product's daemon: the same entry executable with
    /// the <c>--daemon</c> argument. On Linux that is the /proc command line, the one
    /// place the argument is readable without a new dependency. On Windows the command
    /// line is not readable cross-integrity, so the scan deliberately finds nothing
    /// there and the recovery relies on the recorded snapshot identity and the planned
    /// task instead - a scan that matched on the executable alone would name the render
    /// and tray processes too, and ending one of those is not a daemon recovery.
    /// </summary>
    private static bool IsDaemonEntry(int processId, string entry)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var cmdline = ReadLinuxCommandLine(processId);
        if (cmdline is null)
        {
            return false;
        }

        var parts = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0
            && string.Equals(parts[0], entry, StringComparison.Ordinal)
            && parts.Any(part => string.Equals(part, "--daemon", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadLinuxCommandLine(int processId)
    {
        try
        {
            return File.ReadAllText(
                $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/cmdline");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when nothing holds the named slot. A wait that fails means a live holder; an
    /// abandoned mutex means the holder died mid-hold and ownership transferred to this
    /// probe, which is the free case. The probe releases at once - a recovery that held
    /// the slot itself would block the daemon it is about to start.
    /// </summary>
    private static bool IsSlotFree(string name)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, name);
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                return false;
            }

            mutex.ReleaseMutex();
            mutex.Dispose();
            return true;
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died mid-hold: ownership transferred here, so
            // releasing is what hands the slot to the daemon that starts next.
            mutex?.ReleaseMutex();
            mutex?.Dispose();
            return true;
        }
    }

    /// <summary>
    /// How long any scheduler call is given before the recovery treats it as failed. A
    /// schtasks or systemctl that never answers is a hung scheduler, and a recovery that
    /// waits on it forever is the unbounded hang the whole design exists to avoid - so
    /// the call is abandoned and the next recovery path runs instead.
    /// </summary>
    private static readonly TimeSpan SchedulerCallTimeout = TimeSpan.FromSeconds(10);

    private static bool RunSchTasks(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c schtasks {arguments}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(SchedulerCallTimeout))
            {
                // A scheduler that never answers is not an answer: end the call and
                // report the failure so the recovery falls through to its next path.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                    // Already gone.
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// Supervises the render process: the stateless Avalonia window the tray starts on demand.
/// The tray never hosts a window itself, so this is the only thing that can put one on screen.
/// </summary>
internal enum RenderSupervisionState
{
    Stopped,
    Running,
    StartFailed,
    StopFailed,
}

internal sealed class RenderSupervisor : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private readonly ProcessLaunch _launcher;
    private readonly Action _showLive;
    private IProcessHandle? _render;
    private bool _disposed;

    public event Action? Exited;

    public RenderSupervisor(ProcessLaunch? launch = null, Action? showLive = null)
    {
        _launcher = launch ?? ProcessLauncher.Start;
        _showLive = showLive ?? (static () => { });
    }

    public bool IsAlive => _render is { HasExited: false };

    public RenderSupervisionState State { get; private set; } = RenderSupervisionState.Stopped;

    public void Show()
    {
        if (_disposed)
        {
            return;
        }

        if (IsAlive)
        {
            _showLive();
            return;
        }

        Spawn();
    }

    public bool Stop()
    {
        if (_render is not { } render)
        {
            State = RenderSupervisionState.Stopped;
            return true;
        }

        if (!render.HasExited)
        {
            try
            {
                render.Kill(entireTree: true);
                using var timeout = new CancellationTokenSource(StopTimeout);
                render.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception
                is InvalidOperationException or Win32Exception or OperationCanceledException)
            {
                Trace.WriteLine($"[DeziroJoy] render stop: {exception.Message}");
            }
        }

        var stopped = render.HasExited;
        State = stopped ? RenderSupervisionState.Stopped : RenderSupervisionState.StopFailed;
        if (stopped)
        {
            render.Exited -= OnRenderExited;
            _render = null;
            render.Dispose();
        }

        Trace.WriteLine($"[DeziroJoy] render supervisor: {State}");
        return stopped;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_render is { } render)
        {
            render.Exited -= OnRenderExited;
            _render = null;
            render.Dispose();
        }
    }

    private void Spawn()
    {
        var handle = _launcher(ProcessLauncher.RenderStartInfo());

        if (handle is null)
        {
            State = RenderSupervisionState.StartFailed;
            return;
        }

        _render = handle;
        State = RenderSupervisionState.Running;
        handle.Exited += OnRenderExited;
    }

    private void OnRenderExited()
    {
        if (_render is not { } render)
        {
            return;
        }

        render.Exited -= OnRenderExited;

        if (ReferenceEquals(_render, render))
        {
            _render = null;
            State = RenderSupervisionState.Stopped;
            Exited?.Invoke();
        }
    }
}

/// <summary>
/// The resident presentation host: tray, overlay lifecycle, settings mirror and process
/// supervisors. Its icon is hidden while a render window is up, but the process stays alive;
/// the two floating surfaces it owns are built on first need and released when the daemon
/// stops calling for them, so they are resident only while something is on screen.
/// </summary>
internal static class TrayHost
{
    /// <summary>True once this process is the tray rather than a render process.</summary>
    internal static bool IsTrayProcess { get; private set; }

    private static GuiInstanceOwner? _instance;

    public static int Run()
    {
        IsTrayProcess = true;

        var identity = GuiInstanceIdentity.ForCurrentProcess(GuiInstanceRole.Tray);
        var arbitration = GuiInstanceGuard.ArbitrateAsync(identity, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (!arbitration.IsWinner)
        {
            return arbitration.ExitCode;
        }

        var activation = GuiActivationServer.StartActive(identity, GuiInstanceRole.Tray);
        var owner = new GuiInstanceOwner(identity, arbitration.Lease!, activation);
        _instance = owner;

        try
        {
            // The resident host is pinned to the Win32 software renderer by policy: it never
            // closes, so the platform default would keep a GPU stack alive for the whole
            // logon session on behalf of the tray icon and the two floating surfaces. Only an
            // explicit DEZIROJOY_RENDER override can move it, and only for diagnostics.
            Program.BuildTrayAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
            return 0;
        }
        finally
        {
            _instance = null;
            owner.Dispose();
        }
    }

    internal static TrayService InstallTray(
        Application application,
        IClassicDesktopStyleApplicationLifetime lifetime)
        => InstallTray(
            application,
            lifetime,
            new ThemeService(),
            new DaemonSupervisor(),
            new RenderSupervisor(showLive: ShowLiveRenderProcess),
            GuiActivationServer.ActiveInstance,
            // The overlay observes the daemon the supervisor already owns, so it probes the
            // mapping pipe and never starts a daemon of its own - a second elevated relaunch
            // from the tray would be a UAC prompt for a daemon that is already there.
            overlayController: static () => new DaemonController(new ProbeOnlyDaemonEndpoint()));

    internal static TrayService InstallTray(
        Application application,
        IClassicDesktopStyleApplicationLifetime lifetime,
        ThemeService themes,
        DaemonSupervisor daemon,
        RenderSupervisor render,
        GuiActivationServer? activation = null,
        Action? shutdown = null,
        Func<bool>? isDaemonReachable = null,
        Func<DaemonController?>? overlayController = null)
    {
        themes.ApplyPersisted();

        isDaemonReachable ??= static () => DaemonSupervisor.IsDaemonReachable(IpcContract.PipeName);
        Process? observedRender = null;
        IDisposable? settingsWatcher = null;

        // The resident host owns both floating surfaces whether or not a GUI is open, and
        // builds each one only while the daemon calls for it: a tray that never shows a
        // layer hint or a keyboard never pays for the window, the view models or the feed.
        TrayOverlayHost? overlays = null;

        Task ShowAsync()
        {
            try
            {
                daemon.EnsureRunning();
                render.Show();
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"[DeziroJoy] render launch failed: {exception.Message}");
            }

            return Task.CompletedTask;
        }

        // The menu exit and the update flow's Exit request share one path. The difference is
        // the daemon: an updater has already stopped it over IPC - the only way an elevated
        // daemon can be stopped at all - and a second refusal from a daemon that is already
        // gone must not keep the tray, and with it the old binary, resident. When the mapping
        // pipe still answers, the daemon is alive after all and the normal stop path runs, so
        // the skip is verified rather than assumed.
        bool ExitTray(bool daemonAlreadyStopped)
        {
            if (!daemonAlreadyStopped || isDaemonReachable())
            {
                if (!daemon.Stop())
                {
                    return false;
                }
            }

            if (!render.Stop())
            {
                return false;
            }

            Retire(shutdown, lifetime);
            return true;
        }

        var tray = new TrayService(
            application,
            lifetime,
            themes,
            showMainWindow: () => _ = ShowAsync(),
            exit: () => ExitTray(daemonAlreadyStopped: false));

        tray.Install();

        void RenderExited()
        {
            Dispatcher.UIThread.Post(() => tray.SetVisible(true));
        }

        void RenderStarted(int processId)
        {
            observedRender?.Dispose();
            observedRender = null;
            try
            {
                var process = Process.GetProcessById(processId);
                observedRender = process;
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    if (ReferenceEquals(observedRender, process))
                    {
                        observedRender.Dispose();
                        observedRender = null;
                        RenderExited();
                    }
                };
                if (process.HasExited)
                {
                    process.Dispose();
                    observedRender = null;
                    RenderExited();
                    return;
                }
            }
            catch (ArgumentException)
            {
                RenderExited();
                return;
            }

            Dispatcher.UIThread.Post(() => tray.SetVisible(false));
        }

        render.Exited += RenderExited;

        if (activation is not null)
        {
            activation.RenderStarted += processId =>
            {
                RenderStarted(processId);
                return Task.CompletedTask;
            };
            activation.RenderStopped += () =>
            {
                observedRender?.Dispose();
                observedRender = null;
                RenderExited();
                return Task.CompletedTask;
            };
            activation.ShowRequested += ShowAsync;
            activation.RestartRequested += () =>
            {
                if (!render.Stop())
                {
                    return Task.FromException(
                        new InvalidOperationException("the render could not stop for the presentation host restart"));
                }

                Retire(shutdown, lifetime);
                return Task.CompletedTask;
            };
            activation.ExitRequested += () =>
            {
                if (!ExitTray(daemonAlreadyStopped: true))
                {
                    // The server turns a faulted handler into Accepted=false, so the peer
                    // learns at once that the tray is not going away instead of waiting out
                    // its whole vanish budget for the same answer.
                    return Task.FromException(
                        new InvalidOperationException("the tray could not retire"));
                }

                return Task.CompletedTask;
            };
            activation.BeginServing();
        }

        void PreferencesChanged()
        {
            Dispatcher.UIThread.Post(() =>
            {
                themes.ApplyPersisted();
                overlays?.ApplyPreferences();
            });
        }

        tray.Disposed += () =>
        {
            themes.ExternalChanged -= PreferencesChanged;
            settingsWatcher?.Dispose();
            render.Exited -= RenderExited;
            observedRender?.Dispose();
            observedRender = null;
            overlays?.Dispose();
            daemon.Dispose();
            render.Dispose();
        };

        daemon.EnsureRunning();

        // Both overlays stay in this process across GUI exits; the seam keeps process tests
        // headless. The host owns the one connection they share - probe-only, so it observes
        // the daemon the supervisor already owns and never starts one of its own - and
        // creates each surface from the first snapshot that needs it.
        if (overlayController?.Invoke() is { } controller)
        {
            overlays = new TrayOverlayHost(controller, themes);
        }

        themes.ExternalChanged += PreferencesChanged;
        settingsWatcher = themes.WatchForExternalChanges();

        return tray;
    }

    /// <summary>
    /// Builds the tray process's layer hint overlay over the resident connection: a layer hint
    /// and a live pad diagram fed from the daemon's snapshots and input samples, bound to a
    /// click-through overlay whose placement rides the persisted preferences.
    /// <para>
    /// This is only the stack itself - the two view models, the window and the feed that binds
    /// them. <see cref="TrayOverlayHost"/> decides when it is built (the first snapshot that
    /// reports an active layer), owns the connection and the connect retry, and releases it
    /// when the layer lets go. The feed replays the snapshot the controller already holds, so
    /// a layer that was active when the stack was built appears at once.
    /// </para>
    /// </summary>
    internal static TrayLayerHintHandle InstallTrayLayerHint(DaemonController controller, ThemeService themes)
    {
        var layerHint = new LayerHintViewModel
        {
            IsSuppressed = !themes.LayerHintEnabled,
        };
        var visualizer = new GamepadVisualizerViewModel();
        var overlay = new LayerHintOverlay(
            layerHint,
            visualizer,
            () => themes.LayerHintPlacement,
            ScreenCursor.TryGetPosition);
        var feed = new DaemonLayerHintFeed(controller, layerHint, visualizer, new DispatcherFrameClock());
        return new TrayLayerHintHandle(layerHint, overlay, feed);
    }

    /// <summary>
    /// Owns everything the tray's layer hint overlay needs, released when the layer lets go:
    /// the window closes and the feed drops its daemon subscriptions and its frame clock, so
    /// nothing the daemon raises afterwards reaches a window that is gone. The connection
    /// itself belongs to <see cref="TrayOverlayHost"/>, which shares it with the screen
    /// keyboard and outlives either surface.
    /// </summary>
    internal sealed class TrayLayerHintHandle(
        LayerHintViewModel layerHint,
        LayerHintOverlay overlay,
        DaemonLayerHintFeed feed) : IDisposable
    {
        private bool _disposed;

        public LayerHintOverlay Overlay => overlay;

        public void ApplyPreferences(ThemeService themes)
            => layerHint.IsSuppressed = !themes.LayerHintEnabled;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            overlay.Close();
            feed.Dispose();
        }
    }

    /// <summary>
    /// Feeds a layer hint overlay from the daemon in the tray process, mirroring how the render
    /// window's view model drives its own overlay. The tray owns no window and no view model, so
    /// this is the smallest seam that turns daemon snapshots and input samples into the two view
    /// models the overlay binds: the layer hint (rebuilt from every snapshot, so it appears and
    /// disappears with the temporary layer the daemon reports) and the live pad diagram (fed from
    /// input samples, so the held leader lights up on it). Daemon events arrive on the
    /// controller's receive loop, so snapshots are marshalled onto the UI thread exactly as the
    /// render path marshals them.
    /// <para>
    /// The sample stream is the one that arrives faster than the display refreshes, so it does
    /// not follow that shape: the receive loop keeps the latest sample - whichever device sent
    /// it, because the diagram is one pad's picture - and the frame clock drains it once per UI
    /// frame instead of posting every sample across. With no clock wired the feed applies each
    /// sample inline, which is what a test drives.
    /// </para>
    /// </summary>
    internal sealed class DaemonLayerHintFeed : IDisposable
    {
        private readonly DaemonController _daemon;
        private readonly LayerHintViewModel _layerHint;
        private readonly GamepadVisualizerViewModel _visualizer;
        private readonly SynchronizationContext? _uiContext;
        private readonly IUiFrameClock? _frameClock;

        /// <summary>Guards <see cref="_latestSample"/>: the receive loop writes it, the frame tick drains it.</summary>
        private readonly object _sampleGate = new();

        /// <summary>
        /// The sample waiting for the next frame, or null when the frame already took it. One
        /// slot, not one per device: the diagram shows whichever pad sampled last, which is
        /// exactly what the per-sample path's last update won.
        /// </summary>
        private InputSampleMessage? _latestSample;

        private long _queuedSampleCount;

        /// <summary>
        /// How many samples the receive loop has queued for a frame since this feed was built.
        /// Read under <see cref="_sampleGate"/>, which is what makes it the deterministic signal
        /// that a sample's arrival work is finished: the controller stores its latest sample
        /// before raising the event that queues here, so a test waiting on that store alone
        /// could tick a frame before the sample reached the feed.
        /// </summary>
        internal long QueuedSampleCount
        {
            get
            {
                lock (_sampleGate)
                {
                    return _queuedSampleCount;
                }
            }
        }

        private bool _disposed;

        public DaemonLayerHintFeed(
            DaemonController daemon,
            LayerHintViewModel layerHint,
            GamepadVisualizerViewModel visualizer,
            IUiFrameClock? frameClock = null)
        {
            _daemon = daemon;
            _layerHint = layerHint;
            _visualizer = visualizer;
            _uiContext = SynchronizationContext.Current;
            _frameClock = frameClock;
            if (_frameClock is not null)
            {
                _frameClock.Tick += FlushSample;
            }

            _daemon.SnapshotChanged += OnSnapshot;
            _daemon.InputSampled += OnInputSample;
            _daemon.Disconnected += OnDisconnected;
            _daemon.Connected += OnConnected;

            if (_daemon.Snapshot is { } snapshot)
            {
                OnSnapshot(snapshot);
            }

            foreach (var sample in _daemon.InputSamples)
            {
                OnInputSample(sample);
            }

            // A controller that was already connected when this feed was built runs the clock
            // from the start, so the samples the constructor replayed flush on it rather than
            // waiting for a connect that already happened.
            if (_daemon.State == DaemonConnectionState.Connected)
            {
                StartClock();
            }
        }

        private void OnSnapshot(StateSnapshot snapshot)
        {
            if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
            {
                _uiContext.Post(_ => OnSnapshot(snapshot), null);
                return;
            }

            _layerHint.Update(ParseProfile(snapshot), snapshot);
            _visualizer.SetConnected(snapshot.HasController);
        }

        private void OnInputSample(InputSampleMessage sample)
        {
            if (_frameClock is null)
            {
                ApplySample(sample);
                return;
            }

            lock (_sampleGate)
            {
                _latestSample = sample;
                _queuedSampleCount++;
            }
        }

        /// <summary>The frame clock's tick, on the UI thread: applies the sample the frame kept.</summary>
        private void FlushSample()
        {
            InputSampleMessage? latest;
            lock (_sampleGate)
            {
                latest = _latestSample;
                _latestSample = null;
            }

            if (latest is null)
            {
                return;
            }

            ApplySample(latest);
        }

        private void ApplySample(InputSampleMessage sample)
        {
            if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
            {
                _uiContext.Post(_ => ApplySample(sample), null);
                return;
            }

            _visualizer.Update(sample.ToGamepadState());
            _visualizer.SetConnected(true);
        }

        private void OnDisconnected()
        {
            // A job queued before the disposal still runs; a released stack has no clock to
            // stop and no view model left to clear.
            if (_disposed)
            {
                return;
            }

            if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
            {
                _uiContext.Post(_ => OnDisconnected(), null);
                return;
            }

            // No daemon means no active layer: the hint empties and the overlay hides, exactly as
            // the render window clears its own hint on disconnect. The samples still queued
            // belong to the connection that just went and are dropped with the clock.
            StopClock();

            _layerHint.Update(null, null);
            _visualizer.SetConnected(false);
        }

        private void OnConnected()
        {
            if (_disposed)
            {
                return;
            }

            StartClock();
        }

        private void StartClock() => _frameClock?.StartTicking();

        private void StopClock()
        {
            if (_frameClock is null)
            {
                return;
            }

            lock (_sampleGate)
            {
                _latestSample = null;
                _frameClock.StopTicking();
            }
        }

        /// <summary>
        /// The profile the snapshot carries, or null when it is absent or unreadable. The layer
        /// hint resolves the layer's mapping ids against this body; a body this build cannot parse
        /// leaves the hint empty, which hides the overlay rather than naming a wrong action.
        /// </summary>
        private static GamepadProfile? ParseProfile(StateSnapshot snapshot)
        {
            if (string.IsNullOrEmpty(snapshot.ProfileJson))
            {
                return null;
            }

            try
            {
                return ProfileSerializer.Load(snapshot.ProfileJson).Profile;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _daemon.SnapshotChanged -= OnSnapshot;
            _daemon.InputSampled -= OnInputSample;
            _daemon.Disconnected -= OnDisconnected;
            _daemon.Connected -= OnConnected;
            if (_frameClock is not null)
            {
                _frameClock.Tick -= FlushSample;
                StopClock();
                _frameClock.Dispose();
            }
        }
    }

    /// <summary>
    /// Builds the tray process's floating screen keyboard over the resident connection: the
    /// keyboard a 屏幕键盘 special key summons, which types into whatever application has focus.
    /// The tray owns no window, so - exactly as it does for the layer hint overlay - it hosts the
    /// window itself, because the resident mode is where the keyboard matters most: the user is
    /// in the target application with the pad in hand and no editor card in sight.
    /// <para>
    /// This is only the stack itself - the view model, the window and the feed that binds them.
    /// <see cref="TrayOverlayHost"/> decides when it is built (the first snapshot that reports
    /// the daemon's own open flag), owns the connection it reads, and releases it when the flag
    /// clears. Everything the keyboard does still goes through the daemon: the flag the engine's
    /// toggle flips arrives in a snapshot and is mirrored here, every key press is a request the
    /// daemon answers through its own output path.
    /// </para>
    /// </summary>
    internal static TrayScreenKeyboardHandle InstallTrayScreenKeyboard(DaemonController controller)
    {
        var keyboard = new ScreenKeyboardViewModel();
        var feed = new DaemonScreenKeyboardFeed(controller, keyboard, new DispatcherFrameClock());
        var window = new ScreenKeyboardWindow(keyboard);
        return new TrayScreenKeyboardHandle(window, feed);
    }

    /// <summary>
    /// Owns the tray's screen keyboard window and the feed that drives it, released when the
    /// daemon stops reporting the keyboard open. The connection the feed reads belongs to
    /// <see cref="TrayOverlayHost"/>, which shares it with the layer hint and outlives either
    /// surface.
    /// </summary>
    internal sealed class TrayScreenKeyboardHandle(
        ScreenKeyboardWindow window,
        DaemonScreenKeyboardFeed feed) : IDisposable
    {
        private bool _disposed;

        public ScreenKeyboardWindow Window => window;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            feed.Dispose();
            window.Close();
        }
    }

    /// <summary>
    /// Drives the tray's floating screen keyboard from the daemon: the open flag from the
    /// snapshots, the cursor and the pages from the input samples, and every output back to the
    /// daemon as a request. The counterpart of <see cref="DaemonLayerHintFeed"/> for the one
    /// surface that also talks back - which is why this one owns the input lease as well.
    /// <para>
    /// The sample stream is coalesced onto the frame clock like every other one, with one
    /// difference: the keyboard is a typing surface whose navigation reads press edges, so the
    /// frame keeps its samples in order rather than only the last one - a press that lands and
    /// releases inside one frame still moves the cursor. While the keyboard is closed the feed
    /// has no consumer at all, so its samples are dropped on arrival rather than queued for a
    /// drain that would skip them anyway.
    /// </para>
    /// </summary>
    internal sealed class DaemonScreenKeyboardFeed : IDisposable
    {
        private readonly DaemonController _daemon;
        private readonly ScreenKeyboardViewModel _keyboard;
        private readonly SynchronizationContext? _uiContext;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Dictionary<string, GamepadState> _baselines = new(StringComparer.Ordinal);
        private readonly IUiFrameClock? _frameClock;

        /// <summary>Guards <see cref="_pendingSamples"/>: the receive loop writes it, the frame tick drains it.</summary>
        private readonly object _sampleGate = new();

        /// <summary>The frame's samples in arrival order; only filled while the keyboard is open.</summary>
        private readonly List<InputSampleMessage> _pendingSamples = [];

        private long _queuedSampleCount;

        /// <summary>
        /// How many samples the receive loop has queued for a frame while the keyboard was open.
        /// Read under <see cref="_sampleGate"/>, which is what makes it the deterministic signal
        /// that a sample's arrival work is finished: the controller stores its latest sample
        /// before raising the event that queues here, so a test waiting on that store alone
        /// could tick a frame before the sample reached the feed. Samples dropped while the
        /// keyboard is closed are not queued and not counted.
        /// </summary>
        internal long QueuedSampleCount
        {
            get
            {
                lock (_sampleGate)
                {
                    return _queuedSampleCount;
                }
            }
        }

        /// <summary>The profile the last snapshot carried, for the key that summons the keyboard.</summary>
        private GamepadProfile? _profile;

        private bool _disposed;

        public DaemonScreenKeyboardFeed(
            DaemonController daemon,
            ScreenKeyboardViewModel keyboard,
            IUiFrameClock? frameClock = null)
        {
            ArgumentNullException.ThrowIfNull(daemon);
            ArgumentNullException.ThrowIfNull(keyboard);

            _daemon = daemon;
            _keyboard = keyboard;
            _uiContext = SynchronizationContext.Current;
            _frameClock = frameClock;
            if (_frameClock is not null)
            {
                _frameClock.Tick += FlushSamples;
            }

            // The daemon is the sole injection authority, so the keyboard asks and the daemon
            // injects - the same two requests the render window's view model makes, over the
            // same connection this feed already owns.
            keyboard.SetOutput(
                text => InjectTextAsync(text),
                (key, modifiers) => InjectKeyAsync(key, modifiers));
            keyboard.CloseRequested += OnCloseRequested;

            _daemon.SnapshotChanged += OnSnapshot;
            _daemon.InputSampled += OnInputSample;
            _daemon.Disconnected += OnDisconnected;
            _daemon.Connected += OnConnected;

            if (_daemon.Snapshot is { } snapshot)
            {
                OnSnapshot(snapshot);
            }

            foreach (var sample in _daemon.InputSamples)
            {
                OnInputSample(sample);
            }

            // A controller that was already connected when this feed was built runs the clock
            // from the start, so the samples the constructor replayed flush on it rather than
            // waiting for a connect that already happened.
            if (_daemon.State == DaemonConnectionState.Connected)
            {
                StartClock();
            }
        }

        /// <summary>
        /// The keyboard asks to be dismissed - its 完成 key, or the 返回 button the window
        /// carries. The daemon owns the flag, so this asks the daemon to clear it rather than
        /// hiding a window the daemon still believes is open.
        /// </summary>
        private void OnCloseRequested() => _ = CloseAsync();

        private void OnSnapshot(StateSnapshot snapshot)
        {
            // A mirror queued before the disposal still runs: the host releases this stack
            // from the snapshot that cleared the flag, ahead of the job this handler posted.
            // The view model and the lease are both handled by then - the lease release by
            // the disposal itself - so a late mirror has nothing left to move.
            if (_disposed)
            {
                return;
            }

            if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
            {
                _uiContext.Post(_ => OnSnapshot(snapshot), null);
                return;
            }

            _profile = ParseProfile(snapshot);
            _baselines.Clear();

            // The daemon owns the flag; this only mirrors it, and moves the input lease with it.
            // The lease is the same per-connection hijack lease the render window takes, so while
            // the keyboard is open the mapping stops and the pad drives the caps.
            if (_keyboard.IsOpen != snapshot.ScreenKeyboardOpen)
            {
                _keyboard.IsOpen = snapshot.ScreenKeyboardOpen;
                _ = SetLeaseAsync(snapshot.ScreenKeyboardOpen, _lifetime.Token);
            }
        }

        private void OnInputSample(InputSampleMessage sample)
        {
            if (_frameClock is null)
            {
                ApplySample(sample);
                return;
            }

            if (!_keyboard.IsOpen)
            {
                return;
            }

            lock (_sampleGate)
            {
                _pendingSamples.Add(sample);
                _queuedSampleCount++;
            }
        }

        /// <summary>
        /// The frame clock's tick, on the UI thread: applies the frame's samples in arrival
        /// order. The order is what keeps the press edges - a release taken against the press
        /// the frame before it saw, not against the frame's last state.
        /// </summary>
        private void FlushSamples()
        {
            List<InputSampleMessage> batch;
            lock (_sampleGate)
            {
                if (_pendingSamples.Count == 0)
                {
                    return;
                }

                batch = _pendingSamples.ToList();
                _pendingSamples.Clear();
            }

            foreach (var sample in batch)
            {
                ApplySample(sample);
            }
        }

        private void ApplySample(InputSampleMessage sample)
        {
            if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
            {
                _uiContext.Post(_ => ApplySample(sample), null);
                return;
            }

            if (!_keyboard.IsOpen)
            {
                return;
            }

            var state = sample.ToGamepadState();
            if (!_baselines.TryGetValue(sample.DeviceId, out var previous))
            {
                // The first sample of one device establishes its baseline without navigating.
                _baselines[sample.DeviceId] = state;
                return;
            }

            var edges = state.PressedButtons & ~previous.PressedButtons;
            _baselines[sample.DeviceId] = state;

            if (edges == GamepadButtonMask.None)
            {
                return;
            }

            // The engine is suspended while the keyboard is open, so the key that summoned it
            // cannot reach the engine to toggle a second time - the window answers it instead.
            if (edges.Has(GamepadButton.B) || IsScreenKeyboardKey(edges))
            {
                _ = CloseAsync();
                return;
            }

            _keyboard.Navigate(edges);
        }

        private void OnDisconnected()
        {
            // A job queued before the disposal still runs; a released stack has no clock to
            // stop and no view model left to clear.
            if (_disposed)
            {
                return;
            }

            if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
            {
                _uiContext.Post(_ => OnDisconnected(), null);
                return;
            }

            // No daemon means no flag and no output channel: the window goes away rather than
            // sitting there typing into nothing. A reconnect that still reports the keyboard open
            // brings it back, because the daemon is the authority. The samples still queued
            // belong to the connection that just went and are dropped with the clock.
            StopClock();

            _baselines.Clear();
            _profile = null;
            _keyboard.IsOpen = false;
        }

        private void OnConnected()
        {
            if (_disposed)
            {
                return;
            }

            StartClock();
        }

        private void StartClock() => _frameClock?.StartTicking();

        private void StopClock()
        {
            if (_frameClock is null)
            {
                return;
            }

            lock (_sampleGate)
            {
                _pendingSamples.Clear();
                _frameClock.StopTicking();
            }
        }

        /// <summary>
        /// True when one of the newly pressed buttons carries the profile's own 屏幕键盘 key, on
        /// the click or on either gesture action.
        /// </summary>
        private bool IsScreenKeyboardKey(GamepadButtonMask edges)
        {
            if (_profile is not { } profile)
            {
                return false;
            }

            foreach (var mapping in profile.Mappings)
            {
                if (mapping.Source is not ButtonBinding binding || !edges.Has(binding.Button))
                {
                    continue;
                }

                if (CarriesScreenKeyboard(mapping.Target)
                    || binding.Gesture is { } gesture
                    && (CarriesScreenKeyboard(gesture.DoubleClickAction) || CarriesScreenKeyboard(gesture.LongPressAction)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CarriesScreenKeyboard(MappingTarget? target)
            => target is SpecialKeyTarget { Kind: SpecialKeyKind.Keyboard };

        /// <summary>
        /// Takes or releases this connection's hijack lease for the keyboard. The request is
        /// fire-and-forget: a lease that could not be taken leaves the mapping running, which is
        /// the safe direction to fail in, and the daemon's own snapshot says what actually holds.
        /// </summary>
        private async Task SetLeaseAsync(bool suspended, CancellationToken cancellationToken)
        {
            try
            {
                await _daemon.SetInjectionSuspendedAsync(suspended, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception
                is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                // The connection is going away; the daemon releases a disconnected client's lease
                // on its own, so there is nothing to retry.
            }
        }

        private async Task InjectTextAsync(string text)
        {
            try
            {
                var result = await _daemon.InjectTextAsync(text, _lifetime.Token).ConfigureAwait(false);
                if (result.Outcome != MutationOutcome.Accepted)
                {
                    ReportFailure(result, "屏幕键盘输入");
                }
            }
            catch (Exception exception) when (exception
                is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                ReportDaemonUnavailable(exception);
            }
        }

        private async Task InjectKeyAsync(GamepadKey key, KeyModifiers modifiers)
        {
            try
            {
                var result = await _daemon.InjectKeyAsync(key, modifiers, _lifetime.Token).ConfigureAwait(false);
                if (result.Outcome != MutationOutcome.Accepted)
                {
                    ReportFailure(result, "屏幕键盘按键");
                }
            }
            catch (Exception exception) when (exception
                is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                ReportDaemonUnavailable(exception);
            }
        }

        /// <summary>Dismisses the keyboard, which means asking the daemon to clear its flag.</summary>
        private async Task CloseAsync()
        {
            try
            {
                var result = await _daemon.SetScreenKeyboardAsync(false, _lifetime.Token).ConfigureAwait(false);
                if (result.Outcome != MutationOutcome.Accepted)
                {
                    ReportFailure(result, "关闭屏幕键盘");
                }
            }
            catch (Exception exception) when (exception
                is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                ReportDaemonUnavailable(exception);
            }
        }

        private void ReportFailure(MutationResult result, string operation)
            => _keyboard.ReportFailure(
                $"{operation}被拒绝：{result.ErrorMessage ?? result.ErrorCode ?? "守护进程拒绝"}");

        private void ReportDaemonUnavailable(Exception exception)
            => _keyboard.ReportFailure($"守护进程不可用：{exception.Message}");

        /// <summary>
        /// The profile the snapshot carries, or null when it is absent or unreadable. The
        /// summoning key has to be found in it; a body this build cannot parse leaves the window
        /// answering B alone, which is the safe subset.
        /// </summary>
        private static GamepadProfile? ParseProfile(StateSnapshot snapshot)
        {
            if (string.IsNullOrEmpty(snapshot.ProfileJson))
            {
                return null;
            }

            try
            {
                return ProfileSerializer.Load(snapshot.ProfileJson).Profile;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _keyboard.CloseRequested -= OnCloseRequested;
            _daemon.SnapshotChanged -= OnSnapshot;
            _daemon.InputSampled -= OnInputSample;
            _daemon.Disconnected -= OnDisconnected;
            _daemon.Connected -= OnConnected;
            if (_frameClock is not null)
            {
                _frameClock.Tick -= FlushSamples;
                StopClock();
                _frameClock.Dispose();
            }

            // The host releases this stack from the same snapshot that cleared the daemon's
            // flag, and its release job is queued ahead of the mirror this feed posted behind
            // it: that mirror would ask for the lease release with a lifetime already cancelled
            // below, and a daemon that never hears the request keeps mapping suspended for
            // good. The release is therefore sent from the disposal itself - fire-and-forget
            // IPC that needs no UI thread - whenever this feed still holds the lease, which
            // is exactly what the flag it mirrors says. A daemon that cannot be reached
            // releases a disconnected client's lease on its own, so a failed request is
            // nothing to retry.
            if (_keyboard.IsOpen)
            {
                _ = SetLeaseAsync(suspended: false, CancellationToken.None);
            }

            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }

    /// <summary>
    /// Owns the tray process's two floating surfaces for the host's whole life, and builds each
    /// one only while the daemon calls for it. The resident host used to build both windows, both
    /// view models and both feeds at startup and hold them until exit - memory a tray spends
    /// while no layer is held and no keyboard is open. Here the connection is the only resident
    /// piece: a surface is created from the first snapshot that needs it and released when the
    /// need clears, so a closed layer or a dismissed keyboard gives the window, its bindings and
    /// its subscriptions back instead of parking them hidden.
    /// <para>
    /// The need is read from the daemon's own state, never derived locally: the layer the
    /// snapshot reports and the keyboard flag the daemon owns are the same two facts the
    /// surfaces themselves mirror, so a surface exists exactly while its content would be on
    /// screen. A disconnect releases both - no daemon means no layer and no keyboard - and the
    /// reconnect that follows is the controller's own, which re-sends the authoritative state;
    /// the stacks are rebuilt from it at most once each, because creation is keyed on the stack
    /// not yet existing and a reconnect registers nothing a second time.
    /// </para>
    /// <para>
    /// Every create and release runs on the UI thread: the daemon's receive loop is what asks
    /// for them, so its events are marshalled rather than acted on where they land. Reuse was
    /// the other option - keep a hidden window and show it again - and was rejected for the
    /// resident case this exists for: the whole cost of these two windows is the point, and a
    /// parked one is the eager behaviour with extra steps. The frame clocks stay per-stack and
    /// are the P3 <see cref="DispatcherFrameClock"/>, so a released surface takes its timer with
    /// it and an idle tray runs none.
    /// </para>
    /// </summary>
    internal sealed class TrayOverlayHost : IDisposable
    {
        /// <summary>How many times the host retries the overlay connection before giving up.</summary>
        private const int ConnectAttempts = 6;

        /// <summary>Pause between connect retries, covering the daemon child's pipe startup.</summary>
        private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(500);

        private readonly DaemonController _controller;
        private readonly ThemeService _themes;
        private readonly CancellationTokenSource _connectLifetime = new();
        private TrayLayerHintHandle? _layerHint;
        private TrayScreenKeyboardHandle? _screenKeyboard;
        private bool _disposed;

        public TrayOverlayHost(DaemonController controller, ThemeService themes)
        {
            _controller = controller;
            _themes = themes;

            // Subscribed once, here: a reconnect is the controller's own and keeps its
            // subscriptions, so no daemon restart can register a second watcher.
            _controller.SnapshotChanged += OnSnapshot;
            _controller.Disconnected += OnDisconnected;
            ConnectWithRetry();
        }

        /// <summary>
        /// How many layer hint stacks this host has built - one per appearance, never a parked
        /// second one beside a live one.
        /// </summary>
        internal int LayerHintCreations { get; private set; }

        /// <summary>How many screen keyboard stacks this host has built - one per appearance.</summary>
        internal int ScreenKeyboardCreations { get; private set; }

        /// <summary>The layer hint window while one exists, or null when no layer calls for it.</summary>
        internal LayerHintOverlay? LayerHintWindow => _layerHint?.Overlay;

        /// <summary>
        /// The screen keyboard window while one exists, or null when the daemon reports it closed.
        /// </summary>
        internal ScreenKeyboardWindow? KeyboardWindow => _screenKeyboard?.Window;

        /// <summary>True while a layer hint stack is built.</summary>
        internal bool HasLayerHint => _layerHint is not null;

        /// <summary>True while a screen keyboard stack is built.</summary>
        internal bool HasScreenKeyboard => _screenKeyboard is not null;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connectLifetime.Cancel();
            _controller.SnapshotChanged -= OnSnapshot;
            _controller.Disconnected -= OnDisconnected;
            ReleaseLayerHint();
            ReleaseScreenKeyboard();
            _connectLifetime.Dispose();
            _ = _controller.DisposeAsync().AsTask();
        }

        /// <summary>
        /// The persisted preferences changed: the hint's own suppression follows the preference,
        /// and the need gate is re-evaluated, so turning the hint off releases a surface that is
        /// on screen and turning it back on builds one for the layer that is already held - the
        /// snapshot the controller holds is what the fresh stack is built from. Called after the
        /// caller has reloaded the preferences, so the gate reads the values now on disk.
        /// </summary>
        internal void ApplyPreferences()
        {
            _layerHint?.ApplyPreferences(_themes);
            Evaluate();
        }

        private void OnSnapshot(StateSnapshot snapshot) => Post(Evaluate);

        /// <summary>
        /// The daemon is gone: both surfaces go away now rather than sitting resident for a
        /// daemon that may not come back. The daemon's own flags - the active layer, the open
        /// keyboard - survive a disconnect, so a reconnect that still reports them rebuilds the
        /// stacks from the state it re-sends.
        /// </summary>
        private void OnDisconnected() => Post(ReleaseAll);

        private void Post(Action job)
        {
            if (_disposed)
            {
                return;
            }

            Dispatcher.UIThread.Post(job);
        }

        /// <summary>
        /// Builds each surface the latest daemon state calls for and releases the ones it does
        /// not. Runs on the UI thread, and reads the controller's current snapshot rather than
        /// the one that queued this job: a snapshot that arrived while the job waited is the
        /// newer truth, and a stack built for a layer that already let go is a window that would
        /// show and hide inside one dispatcher drain.
        /// </summary>
        private void Evaluate()
        {
            if (_disposed)
            {
                return;
            }

            var snapshot = _controller.Snapshot;

            if (LayerHintNeeded(snapshot))
            {
                _layerHint ??= CreateLayerHint();
            }
            else
            {
                ReleaseLayerHint();
            }

            if (ScreenKeyboardNeeded(snapshot))
            {
                _screenKeyboard ??= CreateScreenKeyboard();
            }
            else
            {
                ReleaseScreenKeyboard();
            }
        }

        /// <summary>
        /// Whether the latest snapshot calls for the layer hint: the preference is on and a
        /// device is running a temporary layer. The rows the hint shows are the feed's to build
        /// from the profile the snapshot carries - this is only the gate that decides whether
        /// the surface exists at all.
        /// </summary>
        private bool LayerHintNeeded(StateSnapshot? snapshot)
            => _themes.LayerHintEnabled
                && snapshot is { } current
                && current.Layers.Any(layer => layer.LayerId > 0);

        /// <summary>
        /// Whether the latest snapshot calls for the screen keyboard: the flag the daemon owns,
        /// mirrored exactly as the feed mirrors it onto the window.
        /// </summary>
        private static bool ScreenKeyboardNeeded(StateSnapshot? snapshot)
            => snapshot?.ScreenKeyboardOpen == true;

        private TrayLayerHintHandle CreateLayerHint()
        {
            LayerHintCreations++;
            return InstallTrayLayerHint(_controller, _themes);
        }

        private TrayScreenKeyboardHandle CreateScreenKeyboard()
        {
            ScreenKeyboardCreations++;
            return InstallTrayScreenKeyboard(_controller);
        }

        /// <summary>
        /// Releases the layer hint: the window closes, the feed drops its daemon subscriptions
        /// and its frame clock, and the view models go with them. The field is cleared first so
        /// a snapshot that arrives while the release runs cannot hand the dying stack another
        /// update - the next appearance builds a fresh one from the state that asked for it.
        /// </summary>
        private void ReleaseLayerHint()
        {
            var handle = _layerHint;
            if (handle is null)
            {
                return;
            }

            _layerHint = null;
            handle.Dispose();
        }

        private void ReleaseScreenKeyboard()
        {
            var handle = _screenKeyboard;
            if (handle is null)
            {
                return;
            }

            _screenKeyboard = null;
            handle.Dispose();
        }

        private void ReleaseAll()
        {
            if (_disposed)
            {
                return;
            }

            ReleaseLayerHint();
            ReleaseScreenKeyboard();
        }

        /// <summary>
        /// Connects the overlays' own probe-only controller, retrying within a bounded budget
        /// because the daemon child the supervisor just launched may still be opening its pipe.
        /// A connection that never lands simply leaves both surfaces unbuilt - they are created
        /// from snapshots, and no snapshot means nothing to show - rather than taking the tray
        /// down. One loop for the host's whole life: the reconnects after the first one are the
        /// controller's own, so no second loop is ever started.
        /// </summary>
        private void ConnectWithRetry()
        {
            _ = Task.Run(async () =>
            {
                for (var attempt = 1; attempt <= ConnectAttempts; attempt++)
                {
                    if (_connectLifetime.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        await _controller.ConnectAsync(_connectLifetime.Token).ConfigureAwait(false);
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        Trace.WriteLine($"[DeziroJoy] 托图层提示连接第 {attempt} 次失败：{exception.Message}");
                        if (attempt == ConnectAttempts)
                        {
                            return;
                        }

                        try
                        {
                            await Task.Delay(ConnectRetryDelay, _connectLifetime.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>Ends the presentation host only for an explicit full exit or update.</summary>
    private static void Retire(Action? shutdown, IClassicDesktopStyleApplicationLifetime lifetime)
        => (shutdown ?? (() => lifetime.Shutdown()))();
    /// <summary>
    /// Sends a Show for a render process that is already alive. The tray owns the activation
    /// channel, so the request is written over that pipe exactly as a second launch would
    /// write it, and the channel routes it to the render supervisor.
    /// </summary>
    private static void ShowLiveRenderProcess()
    {
        var identity = GuiInstanceIdentity.ForCurrentProcess(GuiInstanceRole.Render);

        _ = GuiActivationClient.TryShowAsync(identity, GuiActivationOptions.Default, CancellationToken.None);
    }
}

/// <summary>
/// The production side of the process-launch seam: the real OS process table, plus the command
/// that re-enters this product with a given argument.
/// </summary>
internal static class ProcessLauncher
{
    internal static ProcessStartInfo DaemonStartInfo() => EntryStartInfo("--daemon");

    internal static ProcessStartInfo RenderStartInfo() => EntryStartInfo("--gui");

    internal static ProcessStartInfo TrayStartInfo() => EntryStartInfo("--tray");

    internal static IProcessHandle? Start(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo);

        if (process is null)
        {
            return null;
        }

        process.EnableRaisingEvents = true;

        var handle = new SystemProcessHandle(process);
        process.Exited += handle.OnProcessExited;

        return handle;
    }

    /// <summary>
    /// The command that re-enters this product with <paramref name="argument"/>. The published
    /// executable is started directly; under "dotnet run" - where the process path is the
    /// muxer - the host is started with the entry assembly beside it, which is the same shape
    /// the current entry uses.
    /// </summary>
    private static ProcessStartInfo EntryStartInfo(string argument)
    {
        var executable = Environment.ProcessPath;
        var fileName = executable ?? "dotnet";
        var arguments = argument;

        if (string.IsNullOrEmpty(executable) || Program.IsDotNetHost(executable))
        {
            var assembly = Assembly.GetEntryAssembly()?.Location;

            if (!string.IsNullOrEmpty(assembly))
            {
                arguments = $"\"{assembly}\" {argument}";
            }
        }

        return new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
    }

    private sealed class SystemProcessHandle(Process process) : IProcessHandle
    {
        private bool _disposed;

        public bool HasExited
        {
            get
            {
                try
                {
                    return process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    // No process is associated with the handle any more: treat it as ended.
                    return true;
                }
            }
        }

        public event Action? Exited;

        public void Kill(bool entireTree)
        {
            try
            {
                process.Kill(entireTree);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // Already gone, or not ours to terminate.
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => process.WaitForExitAsync(cancellationToken);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            process.Dispose();
        }

        internal void OnProcessExited(object? sender, EventArgs e) => Exited?.Invoke();
    }
}
