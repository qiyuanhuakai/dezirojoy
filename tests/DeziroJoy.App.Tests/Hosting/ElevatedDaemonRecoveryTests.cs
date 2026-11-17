using System.Globalization;
using DeziroJoy.App;
using DeziroJoy.App.Hosting;
using Xunit;

namespace DeziroJoy.App.Tests.Hosting;

/// <summary>
/// The elevated recovery entry: the elevated copy of this product that ends the daemon
/// the GUI could not and then becomes its replacement. Every test drives it through the
/// fake recovery surface and a recording daemon start, so no real process is ever ended
/// and no real daemon is ever started.
/// </summary>
public sealed class ElevatedDaemonRecoveryTests
{
    [Fact]
    public void Run_Ends_The_Identified_Daemon_And_Then_Becomes_The_Replacement()
    {
        // The whole point of the entry, in order: verify the identity, end the old
        // process, wait for the instance and injection slots, and only then start - a
        // replacement started before the release is the second injector the slot exists
        // to prevent.
        var recovery = new FakeEntryRecovery();
        recovery.Plant(9001, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var order = new List<string>();
        recovery.OnEnd = () => order.Add("end");
        var daemon = new RecordingDaemon(order);

        var exit = ElevatedDaemonRecovery.Run(
            ["--recover-daemon", "--pid", "9001", "--seen-at", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc).Ticks.ToString(CultureInfo.InvariantCulture)],
            recovery,
            daemon.Run);

        Assert.Equal(0, exit);
        Assert.Equal(["end", "run"], order);
        Assert.Contains(9001, recovery.Ended);
    }

    [Fact]
    public void Run_Refuses_A_Reused_Pid_And_Ends_Nothing()
    {
        // The process started after the snapshot that named this PID: the daemon died
        // and the number was handed to something else. Ending it would kill a process
        // this product has no authority over, so nothing is ended and nothing starts.
        var recovery = new FakeEntryRecovery();
        recovery.Plant(9001, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));
        var order = new List<string>();
        recovery.OnEnd = () => order.Add("end");

        var exit = ElevatedDaemonRecovery.Run(
            ["--recover-daemon", "--pid", "9001", "--seen-at", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc).Ticks.ToString(CultureInfo.InvariantCulture)],
            recovery,
            new RecordingDaemon(order).Run);

        Assert.Equal(ElevatedDaemonRecovery.IdentityRejected, exit);
        Assert.Empty(recovery.Ended);
        Assert.Empty(order);
    }

    [Fact]
    public void Run_Refuses_A_Pid_That_No_Longer_Runs_The_Daemon()
    {
        // The PID is old enough, but it no longer runs this product's daemon entry: the
        // identity is the path, not just the number.
        var recovery = new FakeEntryRecovery();
        recovery.Plant(9001, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), executable: false);
        var order = new List<string>();
        recovery.OnEnd = () => order.Add("end");

        var exit = ElevatedDaemonRecovery.Run(
            ["--recover-daemon", "--pid", "9001", "--seen-at", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc).Ticks.ToString(CultureInfo.InvariantCulture)],
            recovery,
            new RecordingDaemon(order).Run);

        Assert.Equal(ElevatedDaemonRecovery.IdentityRejected, exit);
        Assert.Empty(recovery.Ended);
        Assert.Empty(order);
    }

    [Fact]
    public void Run_Starts_The_Replacement_When_The_Daemon_Is_Already_Gone()
    {
        // The daemon died between the request and the elevated copy: there is nothing to
        // end and nothing to verify, so the copy goes straight to being the replacement.
        var recovery = new FakeEntryRecovery();
        var order = new List<string>();

        var exit = ElevatedDaemonRecovery.Run(
            ["--recover-daemon", "--pid", "9001", "--seen-at", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc).Ticks.ToString(CultureInfo.InvariantCulture)],
            recovery,
            new RecordingDaemon(order).Run);

        Assert.Equal(0, exit);
        Assert.Empty(recovery.Ended);
        Assert.Equal(["run"], order);
    }

    [Fact]
    public void Run_Refuses_To_Start_When_The_End_Is_Refused()
    {
        // The end was refused - a token that still cannot signal the daemon. Starting
        // anyway would be the second injector, so the copy reports the failure and
        // leaves the old daemon alone.
        var recovery = new FakeEntryRecovery { RefuseEnd = true };
        recovery.Plant(9001, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var order = new List<string>();

        var exit = ElevatedDaemonRecovery.Run(
            ["--recover-daemon", "--pid", "9001", "--seen-at", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc).Ticks.ToString(CultureInfo.InvariantCulture)],
            recovery,
            new RecordingDaemon(order).Run);

        Assert.Equal(ElevatedDaemonRecovery.EndFailed, exit);
        Assert.Empty(order);
    }

    [Fact]
    public void Run_Refuses_To_Start_When_The_Slots_Never_Release()
    {
        // The process is gone but a holder still owns the instance slot: the release is
        // the safety gate, and a copy that starts beside a holder doubles every input.
        var recovery = new FakeEntryRecovery { InstanceSlotFree = false };
        recovery.Plant(9001, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var order = new List<string>();
        recovery.OnEnd = () => order.Add("end");

        var exit = ElevatedDaemonRecovery.Run(
            ["--recover-daemon", "--pid", "9001", "--seen-at", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc).Ticks.ToString(CultureInfo.InvariantCulture)],
            recovery,
            new RecordingDaemon(order).Run,
            releaseWait: TimeSpan.FromMilliseconds(300));

        Assert.Equal(ElevatedDaemonRecovery.EndFailed, exit);
        Assert.Equal(["end"], order);
    }

    [Fact]
    public void Run_Refuses_Without_A_Daemon_Identity()
    {
        // No PID or no moment: the copy has nothing it could prove, and a kill without a
        // provable identity is exactly what this entry exists to refuse.
        var recovery = new FakeEntryRecovery();
        var order = new List<string>();

        var exit = ElevatedDaemonRecovery.Run(["--recover-daemon"], recovery, new RecordingDaemon(order).Run);

        Assert.Equal(ElevatedDaemonRecovery.IdentityRejected, exit);
        Assert.Empty(recovery.Ended);
        Assert.Empty(order);
    }

    /// <summary>The daemon start, recorded in order with the end and the release.</summary>
    private sealed class RecordingDaemon(List<string> order)
    {
        public int Run()
        {
            order.Add("run");
            return 0;
        }
    }

    /// <summary>
    /// The recovery surface, faked for the entry: the identity a test plants, the end it
    /// permits or refuses, and the slot states the release wait watches. Ending a process
    /// removes its start time - what "the process is gone" looks like to the entry.
    /// </summary>
    private sealed class FakeEntryRecovery : IDaemonRecovery
    {
        private readonly Dictionary<int, DateTimeOffset> _startTimes = [];
        private readonly HashSet<int> _executables = [];
        private readonly HashSet<int> _ended = [];

        public HashSet<int> Ended => _ended;

        public bool InstanceSlotFree { get; set; } = true;

        public bool InjectionSlotFree { get; set; } = true;

        public bool RefuseEnd { get; set; }

        public Action? OnEnd { get; set; }

        public void Plant(int processId, DateTimeOffset startTime, bool executable = true)
        {
            _startTimes[processId] = startTime;
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

            _ended.Add(processId);
            _startTimes.Remove(processId);
            OnEnd?.Invoke();
            return true;
        }

        public bool IsDaemonInstanceSlotFree() => InstanceSlotFree;

        public bool IsInjectionSlotFree() => InjectionSlotFree;

        public bool IsDaemonTaskInstalled() => false;

        public bool EndDaemonTask() => false;

        public bool StartDaemonTask() => false;

        public bool StopDaemonUnit() => false;

        public bool StartElevatedRecovery(int processId, DateTimeOffset seenAt) => false;
    }
}
