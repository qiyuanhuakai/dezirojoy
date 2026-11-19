using System.Diagnostics;
using Avalonia.Headless.XUnit;
using DeziroJoy.App.Hosting;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The update's stop: the one an installer may be started behind. The exit path's stop is not
/// good enough for it, because the exit path only needs the mapping pipe gone while the process
/// is on its way out anyway - an update needs the daemon <em>process</em> gone, since a daemon
/// that is still resident when the setup begins holds the install directory's files open and
/// the setup's DeleteFile fails with access denied (error 5) and rolls back. That is what a user
/// hit: the app closed for the update, nothing was installed, and nothing said why.
/// <para>
/// So the chain under test is a proof, not a request: the graceful stop, then the platform or
/// identity-confirmed end, a bounded wait for that asynchronous end, and finally the elevated
/// stop-only copy. No recovery start is allowed during an update. A daemon nobody could prove
/// gone is a failed stop, which is why every assertion here ends in either a proven stop or an
/// honest failure. Every process is a fake and no PID is ever ended for real.
/// </para>
/// </summary>
public sealed class DaemonSupervisorStopForUpdateTests
{
    private const int OldDaemonPid = 4242;

    private static DateTimeOffset SeenAt => DateTimeOffset.UtcNow.AddMinutes(-1);

    /// <summary>
    /// The recovery surface, faked: the live processes a test plants, the ends, the slot states
    /// and the scheduler calls. Ending a process removes its start time, which is what "the
    /// process is gone" looks like to the supervisor - and to the liveness probe beside it.
    /// </summary>
    private sealed class FakeRecovery : IDaemonRecovery
    {
        private readonly Dictionary<int, DateTimeOffset> _startTimes = [];
        private readonly HashSet<int> _executables = [];
        private readonly List<(int ProcessId, DateTimeOffset SeenAt)> _elevatedRequests = [];
        private readonly List<(int ProcessId, DateTimeOffset SeenAt)> _stopOnlyRequests = [];

        public HashSet<int> Ended { get; } = [];

        public IReadOnlyList<(int ProcessId, DateTimeOffset SeenAt)> ElevatedRequests => _elevatedRequests;

        /// <summary>Every identity handed to the stop-only elevated copy, in order.</summary>
        public IReadOnlyList<(int ProcessId, DateTimeOffset SeenAt)> StopOnlyRequests => _stopOnlyRequests;

        public bool InstanceSlotFree { get; set; } = true;

        public bool InjectionSlotFree { get; set; } = true;

        public bool TaskInstalled { get; set; }

        /// <summary>When set, every end is refused - the access-denied kill.</summary>
        public bool RefuseEnd { get; set; }

        public int EndTaskCalls { get; private set; }

        public int StartTaskCalls { get; private set; }

        public int StopUnitCalls { get; private set; }

        /// <summary>Runs after a process was ended - how a test models Restart=on-failure.</summary>
        public Action<int>? OnEndProcess { get; set; }

        /// <summary>Plants one live daemon process with its identity.</summary>
        public void Plant(int processId, bool executable = true)
        {
            _startTimes[processId] = SeenAt.AddMinutes(-5);
            if (executable)
            {
                _executables.Add(processId);
            }
        }

        public IReadOnlyList<DaemonProcessIdentity> FindDaemonProcesses() => [];

        public DateTimeOffset? TryGetStartTime(int processId)
            => _startTimes.TryGetValue(processId, out var startTime) ? startTime : null;

        public bool IsDaemonExecutable(int processId) => _executables.Contains(processId);

        public bool EndProcess(int processId)
        {
            if (RefuseEnd || !_startTimes.ContainsKey(processId))
            {
                return false;
            }

            Ended.Add(processId);
            _startTimes.Remove(processId);
            OnEndProcess?.Invoke(processId);
            return true;
        }

        /// <summary>
        /// Ends a process regardless of this process's own denial - the elevated copy's end,
        /// which is the whole point of the hand-off: the token that could not end the daemon
        /// asks a copy that can.
        /// </summary>
        public bool ForceEnd(int processId)
        {
            if (!_startTimes.ContainsKey(processId))
            {
                return false;
            }

            Ended.Add(processId);
            _startTimes.Remove(processId);
            OnEndProcess?.Invoke(processId);
            return true;
        }

        public bool IsDaemonInstanceSlotFree() => InstanceSlotFree;

        public bool IsInjectionSlotFree() => InjectionSlotFree;

        public bool IsDaemonTaskInstalled() => TaskInstalled;

        public bool EndDaemonTask()
        {
            EndTaskCalls++;
            return true;
        }

        public bool StartDaemonTask()
        {
            StartTaskCalls++;
            return true;
        }

        public bool StopDaemonUnit()
        {
            StopUnitCalls++;
            return true;
        }

        public bool StartElevatedRecovery(int processId, DateTimeOffset seenAt)
        {
            _elevatedRequests.Add((processId, seenAt));
            return true;
        }

        /// <summary>True when the process is still in the table - the liveness the probe reports.</summary>
        public bool IsAlive(int processId) => _startTimes.ContainsKey(processId);
    }

    /// <summary>Records the launches, and the arguments the elevated copies were handed.</summary>
    private sealed class FakeLauncher
    {
        public List<ProcessStartInfo> Started { get; } = [];

        public IProcessHandle? Start(ProcessStartInfo startInfo)
        {
            Started.Add(startInfo);
            return null;
        }
    }

    /// <summary>
    /// Builds a supervisor over fakes, with the update's own two seams exposed: the liveness
    /// probe and the stop-only elevated request. Both default to "the process is gone", so a test
    /// that cares about either overrides it.
    /// </summary>
    private static (DaemonSupervisor Supervisor, FakeLauncher Launcher, List<int> StopOnlyRequests) Build(
        FakeRecovery recovery,
        Func<bool> isWindows,
        Func<bool> isReachable,
        Func<CancellationToken, Task<bool>> requestShutdown,
        Func<int, DaemonProcessLiveness>? probe = null,
        Func<int, DateTimeOffset, bool>? stopOnly = null,
        bool unitInstalled = false,
        Func<bool>? startDaemonUnit = null)
    {
        var launcher = new FakeLauncher();
        var stopOnlyRequests = new List<int>();
        var supervisor = new DaemonSupervisor(
            new NoElevation(),
            launcher.Start,
            isWindows: isWindows,
            isDaemonReachable: isReachable,
            requestShutdown: requestShutdown,
            isDaemonUnitInstalled: () => unitInstalled,
            startDaemonUnit: startDaemonUnit ?? (() => true),
            recovery: recovery,
            releaseWait: TimeSpan.FromMilliseconds(200),
            verifyWait: TimeSpan.FromMilliseconds(200),
            elevatedReplacementWait: TimeSpan.FromMilliseconds(200),
            probeProcessLiveness: probe ?? (_ => DaemonProcessLiveness.Gone),
            stopOnlyElevatedRecovery: stopOnly ?? ((pid, _) =>
            {
                stopOnlyRequests.Add(pid);
                return true;
            }));

        return (supervisor, launcher, stopOnlyRequests);
    }

    private sealed class NoElevation : IElevationService
    {
        public bool IsSupported => false;

        public bool IsElevated => false;

        public bool RelaunchElevated(string? arguments) => false;
    }

    [AvaloniaFact]
    public void A_Proven_Stop_Needs_No_Escalation_And_No_Replacement()
    {
        // The healthy case: the shutdown is accepted, the pipe goes, the process is gone and
        // the slots are free. Nothing is started and nothing is asked of an elevated copy.
        var reachable = true;
        var recovery = new FakeRecovery();
        var (supervisor, launcher, stopOnlyRequests) = Build(
            recovery,
            () => true,
            () => reachable,
            _ =>
            {
                reachable = false;
                return Task.FromResult(true);
            });

        supervisor.EnsureRunning();
        supervisor.RecordDaemonPid(OldDaemonPid);
        recovery.Plant(OldDaemonPid);

        Assert.True(supervisor.StopForUpdate());

        Assert.Empty(launcher.Started);
        Assert.Empty(recovery.Ended);
        Assert.Empty(stopOnlyRequests);
        Assert.Null(supervisor.LastError);
    }

    [AvaloniaFact]
    public void A_Daemon_Process_That_Is_Still_Running_Is_Not_A_Success()
    {
        // The pipe is gone - which is all the exit path's stop proves - but the process is
        // there. Installing now would race it for the files, so the update's stop escalates
        // and, finding nothing that can end it, reports an honest failure.
        var reachable = true;
        var recovery = new FakeRecovery();
        var (supervisor, _, _) = Build(
            recovery,
            () => true,
            () => reachable,
            _ =>
            {
                reachable = false;
                return Task.FromResult(true);
            },
            probe: _ => DaemonProcessLiveness.Running);

        supervisor.EnsureRunning();
        supervisor.RecordDaemonPid(OldDaemonPid);
        recovery.Plant(OldDaemonPid);

        Assert.False(supervisor.StopForUpdate());
        Assert.Contains("守护进程", supervisor.LastError);
    }

    [AvaloniaFact]
    public void The_Asynchronous_Platform_End_Is_Waited_For_Without_Starting_A_Replacement()
    {
        // Ending a scheduled task acknowledges the request before the elevated process exits.
        // The update must wait for that process, never call the recovery start path and then
        // race the replacement it just created.
        var reachable = true;
        var probes = 0;
        var recovery = new FakeRecovery { TaskInstalled = true, RefuseEnd = true };
        var (supervisor, _, stopOnlyRequests) = Build(
            recovery,
            () => true,
            () => reachable,
            _ =>
            {
                reachable = false;
                return Task.FromResult(true);
            },
            probe: pid =>
            {
                if (!recovery.IsAlive(pid))
                {
                    return DaemonProcessLiveness.Gone;
                }

                if (++probes >= 3)
                {
                    recovery.ForceEnd(pid);
                    return DaemonProcessLiveness.Gone;
                }

                return DaemonProcessLiveness.Unreadable;
            });

        supervisor.EnsureRunning();
        supervisor.RecordDaemonPid(OldDaemonPid);
        recovery.Plant(OldDaemonPid, executable: false);

        Assert.True(supervisor.StopForUpdate());

        Assert.Equal(1, recovery.EndTaskCalls);
        Assert.Equal(0, recovery.StartTaskCalls);
        Assert.Empty(stopOnlyRequests);
        Assert.False(recovery.IsAlive(OldDaemonPid));
    }

    [AvaloniaFact]
    public void A_Daemon_This_Process_Cannot_Open_Is_Handed_To_The_Stop_Only_Elevated_Copy()
    {
        // The elevated daemon no planned task owns: this token cannot open it, so it cannot
        // prove it gone and cannot end it. The stop-only elevated copy verifies the same
        // identity with its own token, ends it and exits - and because it starts nothing,
        // the update is not left with a replacement to stop again.
        var reachable = true;
        var recovery = new FakeRecovery();

        // Unreadable and unendable from this token: the executable cannot be read and the kill
        // is refused, which is exactly the elevated daemon on Windows.
        recovery.Plant(OldDaemonPid, executable: false);
        recovery.RefuseEnd = true;
        var stopOnlyRequests = new List<int>();
        var (supervisor, _, _) = Build(
            recovery,
            () => true,
            () => reachable,
            _ =>
            {
                reachable = false;
                return Task.FromResult(true);
            },
            probe: pid => recovery.IsAlive(pid)
                ? DaemonProcessLiveness.Unreadable
                : DaemonProcessLiveness.Gone,
            stopOnly: (pid, _) =>
            {
                stopOnlyRequests.Add(pid);

                // The elevated copy's end, which is the whole point of the hand-off: the token
                // that could not open the daemon asks a copy that can.
                recovery.ForceEnd(pid);
                return true;
            });

        supervisor.EnsureRunning();
        supervisor.RecordDaemonPid(OldDaemonPid);

        Assert.True(supervisor.StopForUpdate());

        // The identity that travelled is the one the snapshot named - nothing is handed over
        // without it, which is what keeps this from being a blind kill.
        Assert.Equal(OldDaemonPid, Assert.Single(stopOnlyRequests));
        Assert.Empty(recovery.ElevatedRequests);
    }

    [AvaloniaFact]
    public void A_Declined_Elevated_Stop_Is_An_Honest_Failure_And_Not_A_Silent_Success()
    {
        // Nobody could prove the daemon gone - the token cannot open it and the user dismissed
        // the prompt - so the update is cancelled with mapping still working, rather than
        // started against a daemon that may still be holding the install directory's files.
        var reachable = true;
        var recovery = new FakeRecovery();
        recovery.Plant(OldDaemonPid, executable: false);
        recovery.RefuseEnd = true;
        var stopOnlyRequests = new List<int>();
        var (supervisor, _, _) = Build(
            recovery,
            () => true,
            () => reachable,
            _ =>
            {
                reachable = false;
                return Task.FromResult(true);
            },
            probe: _ => DaemonProcessLiveness.Unreadable,
            stopOnly: (pid, _) =>
            {
                stopOnlyRequests.Add(pid);
                return false;
            });

        supervisor.EnsureRunning();
        supervisor.RecordDaemonPid(OldDaemonPid);

        Assert.False(supervisor.StopForUpdate());

        // The conclusive wording, not the escalation's intermediate one: nobody could prove
        // the daemon gone, so the update is cancelled and mapping keeps running.
        Assert.Contains("无法确认守护进程已退出", supervisor.LastError);
        Assert.Contains("映射功能不受影响", supervisor.LastError);
        Assert.Equal(OldDaemonPid, Assert.Single(stopOnlyRequests));
    }

    [AvaloniaFact]
    public void A_Daemon_That_Answers_The_Pipe_Again_Is_Not_Gone()
    {
        // A resident service (the unit's Restart=on-failure) put a daemon back: the pipe
        // answers, so nothing is proven and the update must not start.
        var recovery = new FakeRecovery();
        var (supervisor, _, _) = Build(
            recovery,
            () => false,
            () => true,
            _ => Task.FromResult(true));

        supervisor.EnsureRunning();
        supervisor.RecordDaemonPid(OldDaemonPid);
        recovery.Plant(OldDaemonPid);

        Assert.False(supervisor.StopForUpdate());
    }

    [AvaloniaFact]
    public void The_Linux_Unit_Is_The_End_That_Reaches_A_Daemon_This_Process_Cannot()
    {
        // The same chain on Linux, where the unit owns the daemon: the graceful stop cannot
        // prove it gone, so the unit's own stop is the end, and the proof that follows is the
        // process's rather than the pipe's.
        var reachable = true;
        var recovery = new FakeRecovery();
        var (supervisor, _, _) = Build(
            recovery,
            () => false,
            () => reachable,
            _ =>
            {
                reachable = false;
                return Task.FromResult(true);
            },
            probe: pid => recovery.IsAlive(pid)
                ? DaemonProcessLiveness.Running
                : DaemonProcessLiveness.Gone,
            unitInstalled: true);

        supervisor.EnsureRunning();
        supervisor.RecordDaemonPid(OldDaemonPid);
        recovery.Plant(OldDaemonPid);

        Assert.True(supervisor.StopForUpdate());

        // The unit's own stop is the end that reaches a daemon this process cannot signal, and
        // the identity-confirmed end is what finishes the rest.
        Assert.Contains(OldDaemonPid, recovery.Ended);
        Assert.False(recovery.IsAlive(OldDaemonPid));
    }
}
