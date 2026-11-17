using System.Diagnostics;
using Avalonia.Headless.XUnit;
using DeziroJoy.App.Hosting;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Hosting;

/// <summary>
/// The safe restart and the supervised recovery. The provider is built into the
/// daemon's composition at startup and cannot be hot-swapped, so a new selection takes
/// effect only on a new process - and a restart that cannot confirm the old daemon's
/// exit fails rather than starting a second injector beside it. The recovery is what
/// brings back a daemon that stopped answering: a client reconnect cannot, because the
/// process holding the mapping pipe, the injection mutex and the instance slot is still
/// resident - which is exactly why the user ends up in Task Manager. Every test drives
/// the supervisor through the fake launch and recovery seams, so no daemon process is
/// ever spawned and no real PID is ever ended.
/// </summary>
public sealed class DaemonSupervisorRestartTests
{
    [AvaloniaFact]
    public void Restart_Stops_The_Running_Daemon_And_Brings_A_Fresh_Child_Up()
    {
        var launcher = new FakeLauncher();
        var reachable = true;
        var shutdownRequests = 0;
        var supervisor = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            isWindows: () => false,
            isDaemonReachable: () => reachable,
            requestShutdown: _ =>
            {
                shutdownRequests++;
                reachable = false;
                return Task.FromResult(true);
            },
            isDaemonUnitInstalled: () => false,
            recovery: new FakeRecovery());

        supervisor.EnsureRunning();

        // The daemon was already listening, so the supervisor owns no child yet.
        Assert.Empty(launcher.Started);

        var restarted = supervisor.Restart();

        Assert.True(restarted);
        Assert.Equal(1, shutdownRequests);
        Assert.Single(launcher.Started);
        Assert.Contains("--daemon", launcher.Started[0].Arguments);
    }
    [AvaloniaFact]
    public void Restart_Asks_The_Unit_For_The_Replacement_Daemon_On_Linux()
    {
        var launcher = new FakeLauncher();
        var reachable = true;
        var unitStarts = 0;
        var supervisor = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            isWindows: () => false,
            isDaemonReachable: () => reachable,
            requestShutdown: _ =>
            {
                reachable = false;
                return Task.FromResult(true);
            },
            isDaemonUnitInstalled: () => true,
            startDaemonUnit: () =>
            {
                unitStarts++;
                reachable = true;
                return true;
            },
            recovery: new FakeRecovery());

        supervisor.EnsureRunning();
        Assert.Empty(launcher.Started);

        var restarted = supervisor.Restart();

        Assert.True(restarted);
        Assert.Equal(1, unitStarts);
        Assert.Empty(launcher.Started);
    }

    [AvaloniaFact]
    public void A_Restart_Whose_Stop_Never_Confirmed_The_Exit_Fails_Instead_Of_Doubling_The_Injector()
    {
        var launcher = new FakeLauncher();
        var supervisor = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            isWindows: () => false,
            isDaemonReachable: () => true,
            requestShutdown: _ => Task.FromResult(false),
            isDaemonUnitInstalled: () => false,
            recovery: new FakeRecovery());

        supervisor.EnsureRunning();

        var restarted = supervisor.Restart();

        // The pipe still answers, but no daemon process this supervisor can identify is
        // running: a replacement started now would be the second injector, and the old
        // daemon's exit cannot be proven - so nothing was started and the caller is told.
        Assert.False(restarted);
        Assert.Empty(launcher.Started);
    }

    [AvaloniaFact]
    public void Recover_Ends_The_Unresponsive_Daemon_By_Identity_And_Replaces_It()
    {
        // The reported failure: the daemon process is alive and its pipe still accepts a
        // connect, but no request ever completes - the graceful shutdown times out and
        // the client reconnect lands on the same hung process. The recovery ends the
        // exact process a snapshot named and starts its replacement.
        var launcher = new FakeLauncher();
        var reachable = true;
        var recovery = new FakeRecovery();
        var supervisor = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            isWindows: () => false,
            isDaemonReachable: () => reachable,
            requestShutdown: _ => Task.FromResult(false),
            isDaemonUnitInstalled: () => false,
            recovery: recovery,
            releaseWait: TimeSpan.FromSeconds(5),
            verifyWait: TimeSpan.FromSeconds(5));

        supervisor.EnsureRunning();
        Assert.Equal(DaemonSupervisionState.Running, supervisor.State);

        // The identity a served snapshot named, seen alive before now.
        supervisor.RecordDaemonPid(9001);
        recovery.Plant(9001, DateTimeOffset.UtcNow.AddMinutes(-1));
        recovery.OnEndProcess = () => reachable = false;

        var report = supervisor.Recover();

        Assert.True(report.Recovered, report.Failure);
        Assert.Contains(9001, recovery.Ended);
        Assert.Single(launcher.Started);
        Assert.Contains("--daemon", launcher.Started[0].Arguments);
        Assert.Contains("已按身份确认结束旧守护进程", report.Phases);
        Assert.Contains("新守护进程已就绪", report.Phases);
        Assert.Equal(DaemonSupervisionState.Running, supervisor.State);
    }

    [AvaloniaFact]
    public void Recover_Refuses_A_Reused_Pid_And_Starts_Nothing()
    {
        // PID reuse: the daemon died, the number was handed to a newer process, and a
        // snapshot from before the handover still names it. Ending that process would
        // kill something this supervisor has no authority over.
        var launcher = new FakeLauncher();
        var recovery = new FakeRecovery();
        var supervisor = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            isWindows: () => false,
            isDaemonReachable: () => true,
            requestShutdown: _ => Task.FromResult(false),
            isDaemonUnitInstalled: () => false,
            recovery: recovery);

        supervisor.EnsureRunning();

        supervisor.RecordDaemonPid(9001);
        recovery.Plant(9001, DateTimeOffset.UtcNow.AddMinutes(1));

        var report = supervisor.Recover();

        Assert.False(report.Recovered);
        Assert.DoesNotContain(9001, recovery.Ended);
        Assert.Empty(launcher.Started);
        Assert.Contains("PID", report.Failure);
    }

    [AvaloniaFact]
    public void Recover_Refuses_A_Pid_That_No_Longer_Runs_The_Daemon()
    {
        // The recorded PID is alive and old enough, but it no longer runs the daemon
        // entry: the identity check is the path, not just the number.
        var launcher = new FakeLauncher();
        var recovery = new FakeRecovery();
        var supervisor = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            isWindows: () => false,
            isDaemonReachable: () => true,
            requestShutdown: _ => Task.FromResult(false),
            isDaemonUnitInstalled: () => false,
            recovery: recovery);

        supervisor.EnsureRunning();

        supervisor.RecordDaemonPid(9001);
        recovery.Plant(9001, DateTimeOffset.UtcNow.AddMinutes(-1), executable: false);

        var report = supervisor.Recover();

        Assert.False(report.Recovered);
        Assert.DoesNotContain(9001, recovery.Ended);
        Assert.Empty(launcher.Started);
    }

    [AvaloniaFact]
    public void Recover_Ends_A_Daemon_The_Process_Scan_Finds_When_No_Snapshot_Named_It()
    {
        // No snapshot ever arrived - the tray's supervisor, or a window that never
        // connected - so there is no recorded identity. The scan's daemon processes,
        // matched by entry path and argument, are the daemon by identity.
        var launcher = new FakeLauncher();
        var reachable = true;
        var recovery = new FakeRecovery();
        var supervisor = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            isWindows: () => false,
            isDaemonReachable: () => reachable,
            requestShutdown: _ => Task.FromResult(false),
            isDaemonUnitInstalled: () => false,
            recovery: recovery);

        supervisor.EnsureRunning();

        recovery.Processes.Add(new DaemonProcessIdentity(9001, DateTimeOffset.UtcNow.AddMinutes(-5)));
        recovery.Plant(9001, DateTimeOffset.UtcNow.AddMinutes(-5));
        recovery.OnEndProcess = () => reachable = false;

        var report = supervisor.Recover();

        Assert.True(report.Recovered, report.Failure);
        Assert.Contains(9001, recovery.Ended);
        Assert.Single(launcher.Started);
    }

    [AvaloniaFact]
    public void Recover_Stops_The_Daemon_Through_The_Systemd_Unit_When_The_Graceful_Stop_Fails()
    {
        // Unit ownership: the unit's daemon does not answer, so the graceful stop cannot
        // confirm its exit. The systemd user unit's stop is the end that reaches it -
        // and killing the PID directly instead would race the unit's own
        // Restart=on-failure. The replacement comes from the unit too, so no private
        // child competes with it.
        var launcher = new FakeLauncher();
        var reachable = true;
        var unitStarts = 0;
        var recovery = new FakeRecovery();
        var supervisor = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            isWindows: () => false,
            isDaemonReachable: () => reachable,
            requestShutdown: _ => Task.FromResult(false),
            isDaemonUnitInstalled: () => true,
            startDaemonUnit: () =>
            {
                unitStarts++;
                reachable = true;
                return true;
            },
            recovery: recovery);

        supervisor.EnsureRunning();
        Assert.Empty(launcher.Started);

        // The unit daemon's identity, as a snapshot named it; the unit stop ends it.
        supervisor.RecordDaemonPid(9001);
        recovery.Plant(9001, DateTimeOffset.UtcNow.AddMinutes(-1));
        recovery.OnStopUnit = () =>
        {
            reachable = false;
            recovery.EndProcess(9001);
        };

        var report = supervisor.Recover();

        Assert.True(report.Recovered, report.Failure);
        Assert.Equal(1, recovery.StopUnitCalls);
        Assert.Equal(1, unitStarts);
        Assert.Empty(launcher.Started);
        Assert.Contains("已通过 systemd 用户单元停止常驻服务", report.Phases);
        Assert.Contains("新守护进程已就绪", report.Phases);
    }

    [AvaloniaFact]
    public void Recover_Ends_The_Elevated_Daemon_Through_The_Planned_Task_On_Windows()
    {
        // Windows elevation: the daemon runs with a token this process does not have, so
        // neither the graceful stop nor a direct kill can end it. The logon planned task
        // is the end and the start that reaches an elevated daemon from an unelevated
        // caller - and its start needs no UAC consent, unlike a second elevated relaunch.
        var launcher = new FakeLauncher();
        var reachable = false;
        var elevation = new FakeElevation();
        var recovery = new FakeRecovery { TaskInstalled = true };
        var supervisor = new DaemonSupervisor(
            elevation,
            launcher.Start,
            isWindows: () => true,
            isDaemonReachable: () => reachable,
            requestShutdown: _ => Task.FromResult(false),
            isDaemonUnitInstalled: () => throw new InvalidOperationException("Windows has no unit"),
            recovery: recovery);

        supervisor.EnsureRunning();
        Assert.Equal(1, elevation.RelaunchCalls);
        // The elevated daemon the relaunch asked for is now listening.
        reachable = true;

        // Its identity, as a snapshot named it; the executable is not readable from this
        // token, which is why the task path - not a kill - is the end here. The task's
        // end is what ends the process: the elevated daemon dies with it.
        supervisor.RecordDaemonPid(9001);
        recovery.Plant(9001, DateTimeOffset.UtcNow.AddMinutes(-1), executable: false);
        recovery.OnEndTask = () =>
        {
            reachable = false;
            recovery.EndProcess(9001);
        };
        recovery.OnStartTask = () => reachable = true;

        var report = supervisor.Recover();

        Assert.True(report.Recovered, report.Failure);
        Assert.Equal(1, recovery.EndTaskCalls);
        Assert.Equal(1, recovery.StartTaskCalls);
        Assert.Empty(launcher.Started);
        Assert.Equal(1, elevation.RelaunchCalls);
        Assert.Contains("已通过计划任务结束提权实例", report.Phases);
        Assert.Contains("已通过计划任务启动提权守护进程", report.Phases);
    }

    [AvaloniaFact]
    public void Recover_Starts_Nothing_When_The_Daemon_Process_Cannot_Be_Ended()
    {
        // The identity is confirmed but the OS refuses the end - the access-denied kill
        // of an elevated daemon from an unelevated caller. A replacement started now
        // would be the second injector, so the recovery reports the failure instead.
        var launcher = new FakeLauncher();
        var recovery = new FakeRecovery { RefuseEnd = true };
        var supervisor = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            isWindows: () => false,
            isDaemonReachable: () => true,
            requestShutdown: _ => Task.FromResult(false),
            isDaemonUnitInstalled: () => false,
            recovery: recovery);

        supervisor.EnsureRunning();

        supervisor.RecordDaemonPid(9001);
        recovery.Plant(9001, DateTimeOffset.UtcNow.AddMinutes(-1));

        var report = supervisor.Recover();

        Assert.False(report.Recovered);
        Assert.DoesNotContain(9001, recovery.Ended);
        Assert.Empty(launcher.Started);
        Assert.Contains("权限", report.Failure);
    }

    [AvaloniaFact]
    public void Recover_Starts_Nothing_When_The_Release_Never_Completes()
    {
        // The end was issued but the daemon never lets go of the instance slot: the
        // release wait is the recovery's safety gate, and a replacement started beside
        // a holder is the double injection it exists to prevent.
        var launcher = new FakeLauncher();
        var reachable = true;
        var recovery = new FakeRecovery { InstanceSlotFree = false };
        var supervisor = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            isWindows: () => false,
            isDaemonReachable: () => reachable,
            requestShutdown: _ => Task.FromResult(false),
            isDaemonUnitInstalled: () => false,
            recovery: recovery,
            releaseWait: TimeSpan.FromMilliseconds(300));

        supervisor.EnsureRunning();

        supervisor.RecordDaemonPid(9001);
        recovery.Plant(9001, DateTimeOffset.UtcNow.AddMinutes(-1));
        recovery.OnEndProcess = () => reachable = false;

        var report = supervisor.Recover();

        Assert.False(report.Recovered);
        Assert.Contains(9001, recovery.Ended);
        Assert.Empty(launcher.Started);
        Assert.Contains("槽位", report.Failure);
    }

    [AvaloniaFact]
    public void Recover_Reports_The_Resident_Service_Restoring_The_Daemon_Itself()
    {
        // The unit's own Restart=on-failure put the daemon back after the recovery ended
        // it: the pipe answers again while the ended process stays dead. That is the
        // recovery - reported as one, with nothing started beside it. The instance slot
        // the replacement holds for its whole life is what proves the answering pipe
        // belongs to a live daemon, so the fallback checks the slot and not just the pipe.
        var launcher = new FakeLauncher();
        var reachable = true;
        var recovery = new FakeRecovery { InstanceSlotFree = false };
        var supervisor = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            isWindows: () => false,
            isDaemonReachable: () => reachable,
            requestShutdown: _ => Task.FromResult(false),
            isDaemonUnitInstalled: () => false,
            recovery: recovery,
            releaseWait: TimeSpan.FromMilliseconds(300));

        supervisor.EnsureRunning();

        supervisor.RecordDaemonPid(9001);
        recovery.Plant(9001, DateTimeOffset.UtcNow.AddMinutes(-1));

        var report = supervisor.Recover();

        Assert.True(report.Recovered, report.Failure);
        Assert.Contains(9001, recovery.Ended);
        Assert.Empty(launcher.Started);
        Assert.Contains("常驻服务已自行恢复守护进程", report.Phases);
    }

    [AvaloniaFact]
    public void Recover_Does_Not_Claim_A_Slotless_Pipe_Answer_As_The_Resident_Services_Recovery()
    {
        // The pipe answers and the ended process is gone, but no daemon owns the instance
        // slot: whatever answers is not a daemon this product can prove, so the recovery
        // reports failure rather than claiming a recovery it cannot show.
        var launcher = new FakeLauncher();
        var reachable = true;
        var recovery = new FakeRecovery { InstanceSlotFree = true };
        var supervisor = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            isWindows: () => false,
            isDaemonReachable: () => reachable,
            requestShutdown: _ => Task.FromResult(false),
            isDaemonUnitInstalled: () => false,
            recovery: recovery,
            releaseWait: TimeSpan.FromMilliseconds(300));

        supervisor.EnsureRunning();

        supervisor.RecordDaemonPid(9001);
        recovery.Plant(9001, DateTimeOffset.UtcNow.AddMinutes(-1));

        var report = supervisor.Recover();

        Assert.False(report.Recovered);
        Assert.Contains(9001, recovery.Ended);
        Assert.Empty(launcher.Started);
        Assert.DoesNotContain("常驻服务已自行恢复守护进程", report.Phases);
    }

    [AvaloniaFact]
    public void Recover_Hands_The_Elevated_Daemon_To_An_Elevated_Copy_When_No_Task_Owns_It()
    {
        // Windows, the daemon runs elevated from a one-off relaunch and no planned task
        // owns it: an unelevated caller can neither read its executable nor end it, so
        // the recovery hands the confirmed identity to an elevated copy of this product -
        // which re-verifies, ends and becomes the replacement. This process starts
        // nothing beside it and prompts for nothing.
        var launcher = new FakeLauncher();
        var reachable = false;
        var elevation = new FakeElevation();
        var recovery = new FakeRecovery { TaskInstalled = false, RefuseEnd = true };
        var supervisor = new DaemonSupervisor(
            elevation,
            launcher.Start,
            isWindows: () => true,
            isDaemonReachable: () => reachable,
            requestShutdown: _ => Task.FromResult(false),
            isDaemonUnitInstalled: () => throw new InvalidOperationException("Windows has no unit"),
            recovery: recovery,
            elevatedReplacementWait: TimeSpan.FromSeconds(5));

        supervisor.EnsureRunning();
        Assert.Equal(1, elevation.RelaunchCalls);
        // The elevated daemon the relaunch asked for is now listening.
        reachable = true;

        // Its identity, as a snapshot named it. The executable is not readable from this
        // token - which is exactly why the hand-off, not a kill, is the end here - but
        // the start time is, so the PID is provably not reused.
        supervisor.RecordDaemonPid(9001);
        var seenAt = DateTimeOffset.UtcNow;
        recovery.Plant(9001, seenAt.AddMinutes(-1), executable: false);

        var handedPid = 0;
        DateTimeOffset handedSeenAt = default;
        recovery.OnStartElevatedRecovery = (pid, at) =>
        {
            handedPid = pid;
            handedSeenAt = at;
            // The elevated copy: it ends the old daemon with its own token - this
            // process's end was refused - waits for the slots itself and then serves the
            // mapping pipe as the replacement.
            reachable = false;
            recovery.ForceEnd(9001);
            _ = Task.Delay(TimeSpan.FromMilliseconds(300)).ContinueWith(_ => reachable = true);
        };

        var report = supervisor.Recover();

        Assert.True(report.Recovered, report.Failure);
        Assert.Equal(9001, handedPid);
        Assert.Equal(seenAt, handedSeenAt, TimeSpan.FromSeconds(1));
        Assert.Contains(9001, recovery.Ended);
        Assert.Empty(launcher.Started);
        Assert.Equal(1, elevation.RelaunchCalls);
        Assert.Equal(DaemonSupervisionState.Running, supervisor.State);
        Assert.Contains("提权副本已接替常驻守护进程", report.Phases);
    }

    [AvaloniaFact]
    public void Recover_Fails_When_The_Elevated_Hand_Off_Is_Declined()
    {
        // The UAC prompt was dismissed: the elevated copy never starts, so the old daemon
        // stays and the recovery says so - with the way out - instead of starting a
        // second injector beside it.
        var launcher = new FakeLauncher();
        var reachable = true;
        var recovery = new FakeRecovery { TaskInstalled = false, RefuseEnd = true };
        recovery.StartElevatedRecoveryOverride = false;
        var supervisor = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            isWindows: () => true,
            isDaemonReachable: () => reachable,
            requestShutdown: _ => Task.FromResult(false),
            isDaemonUnitInstalled: () => throw new InvalidOperationException("Windows has no unit"),
            recovery: recovery,
            elevatedReplacementWait: TimeSpan.FromSeconds(2));

        supervisor.EnsureRunning();

        supervisor.RecordDaemonPid(9001);
        recovery.Plant(9001, DateTimeOffset.UtcNow.AddMinutes(-1), executable: false);

        var report = supervisor.Recover();

        Assert.False(report.Recovered);
        Assert.Single(recovery.ElevatedRecoveryRequests);
        Assert.Empty(launcher.Started);
        Assert.Contains("UAC", report.Failure);
    }

    private sealed class FakeLauncher
    {
        public List<ProcessStartInfo> Started { get; } = [];

        public FakeProcessHandle? Start(ProcessStartInfo info)
        {
            Started.Add(info);
            return new FakeProcessHandle();
        }
    }

    private sealed class FakeProcessHandle : IProcessHandle
    {
        private bool _exited;

        public bool HasExited => _exited;

        public event Action? Exited;

        public void Kill(bool entireTree)
        {
            if (_exited)
            {
                return;
            }

            _exited = true;
            Exited?.Invoke();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => _exited ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancellationToken);

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The recovery surface, faked: the process identities a test plants, the ends and
    /// slot states the release wait watches, and the scheduler calls counted per test.
    /// Ending a process removes its start time, which is what "the process is gone"
    /// looks like to the supervisor.
    /// </summary>
    private sealed class FakeRecovery : IDaemonRecovery
    {
        private readonly Dictionary<int, DateTimeOffset> _startTimes = [];
        private readonly HashSet<int> _executables = [];
        private readonly HashSet<int> _ended = [];
        private readonly List<(int ProcessId, DateTimeOffset SeenAt)> _elevatedRecoveryRequests = [];

        /// <summary>Live daemon processes the scan finds.</summary>
        public List<DaemonProcessIdentity> Processes { get; } = [];

        public HashSet<int> Ended => _ended;

        /// <summary>Every identity handed to the elevated recovery entry, in order.</summary>
        public IReadOnlyList<(int ProcessId, DateTimeOffset SeenAt)> ElevatedRecoveryRequests => _elevatedRecoveryRequests;

        public bool InstanceSlotFree { get; set; } = true;

        public bool InjectionSlotFree { get; set; } = true;

        public bool TaskInstalled { get; set; }

        /// <summary>When set, every end is refused - the access-denied kill.</summary>
        public bool RefuseEnd { get; set; }

        public int EndTaskCalls { get; private set; }

        public int StartTaskCalls { get; private set; }

        public int StopUnitCalls { get; private set; }

        public Action? OnEndProcess { get; set; }

        public Action? OnEndTask { get; set; }

        public Action? OnStartTask { get; set; }

        public Action? OnStopUnit { get; set; }

        /// <summary>Runs when an identity is handed to the elevated recovery entry.</summary>
        public Action<int, DateTimeOffset>? OnStartElevatedRecovery { get; set; }

        /// <summary>
        /// Overrides the hand-off's answer: false models a declined UAC prompt, so the
        /// elevated copy never starts and the recovery must report the failure.
        /// </summary>
        public bool? StartElevatedRecoveryOverride { get; set; }

        /// <summary>Plants one live process with its identity.</summary>
        public void Plant(int processId, DateTimeOffset startTime, bool executable = true)
        {
            _startTimes[processId] = startTime;
            if (executable)
            {
                _executables.Add(processId);
            }
        }

        public IReadOnlyList<DaemonProcessIdentity> FindDaemonProcesses() => Processes;

        public DateTimeOffset? TryGetStartTime(int processId)
            => _startTimes.TryGetValue(processId, out var startTime) ? startTime : null;

        public bool IsDaemonExecutable(int processId) => _executables.Contains(processId);

        public bool EndProcess(int processId)
        {
            if (RefuseEnd || !_startTimes.ContainsKey(processId))
            {
                return false;
            }

            _ended.Add(processId);
            _startTimes.Remove(processId);
            OnEndProcess?.Invoke();
            return true;
        }

        /// <summary>
        /// Ends a process regardless of this process's own denial - the elevated copy's
        /// end, which is the whole point of the hand-off: the token that could not end
        /// the daemon asks a copy that can.
        /// </summary>
        public bool ForceEnd(int processId)
        {
            if (!_startTimes.ContainsKey(processId))
            {
                return false;
            }

            _ended.Add(processId);
            _startTimes.Remove(processId);
            return true;
        }

        public bool IsDaemonInstanceSlotFree() => InstanceSlotFree;

        public bool IsInjectionSlotFree() => InjectionSlotFree;

        public bool IsDaemonTaskInstalled() => TaskInstalled;

        public bool EndDaemonTask()
        {
            EndTaskCalls++;
            OnEndTask?.Invoke();
            return true;
        }

        public bool StartDaemonTask()
        {
            StartTaskCalls++;
            OnStartTask?.Invoke();
            return true;
        }

        public bool StopDaemonUnit()
        {
            StopUnitCalls++;
            OnStopUnit?.Invoke();
            return true;
        }

        public bool StartElevatedRecovery(int processId, DateTimeOffset seenAt)
        {
            _elevatedRecoveryRequests.Add((processId, seenAt));
            OnStartElevatedRecovery?.Invoke(processId, seenAt);
            return StartElevatedRecoveryOverride ?? true;
        }
    }

    private sealed class FakeElevation : IElevationService
    {
        public bool IsSupported => true;

        public bool IsElevated => false;

        public int RelaunchCalls { get; private set; }

        public string? LastArguments { get; private set; }

        public bool RelaunchElevated(string? arguments)
        {
            RelaunchCalls++;
            LastArguments = arguments;
            return false;
        }
    }
}
