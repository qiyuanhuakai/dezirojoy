using System.Globalization;
using DeziroJoy.App;
using DeziroJoy.App.Hosting;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The elevated stop-only entry the update's stop chain asks for. It is the recovery's copy
/// with one difference that matters to an update: it ends the daemon and exits instead of
/// becoming the replacement. An update replaces the install directory, so a replacement would
/// be a daemon the update has to stop again - and one that could re-lock the files mid-install.
/// <para>
/// What is under test is therefore the negative: nothing is started. The identity checks are
/// the recovery's own and are asserted here too, because a stop that could end the wrong
/// process is worse than no stop at all.
/// </para>
/// </summary>
public sealed class ElevatedDaemonStopOnlyTests
{
    private const int DaemonPid = 4242;

    private static readonly DateTimeOffset SeenAt = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static string[] Args() =>
    [
        ElevatedDaemonRecovery.StopOnlyFlag,
        "--pid",
        DaemonPid.ToString(CultureInfo.InvariantCulture),
        "--seen-at",
        SeenAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
    ];

    /// <summary>The recovery surface, faked: one live daemon process, endable or not.</summary>
    private sealed class FakeRecovery : IDaemonRecovery
    {
        private readonly Dictionary<int, DateTimeOffset> _startTimes = [];
        private readonly HashSet<int> _executables = [];

        public HashSet<int> Ended { get; } = [];

        public bool RefuseEnd { get; set; }

        public bool InstanceSlotFree { get; set; } = true;

        public bool InjectionSlotFree { get; set; } = true;

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

            Ended.Add(processId);
            _startTimes.Remove(processId);
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

    /// <summary>Records whether a replacement daemon was ever asked for.</summary>
    private sealed class RecordingDaemon
    {
        private readonly List<string> _order;

        public RecordingDaemon(List<string> order) => _order = order;

        public int Run()
        {
            _order.Add("run");
            return 0;
        }
    }

    [Fact]
    public void The_Stop_Only_Copy_Ends_The_Daemon_And_Starts_No_Replacement()
    {
        // The whole point of the entry: the daemon is gone, the slots are free, and nothing
        // was started. A replacement here would be the second injector the instance slot
        // exists to prevent, and a daemon the update would have to stop again.
        var recovery = new FakeRecovery();
        recovery.Plant(DaemonPid, SeenAt.AddMinutes(-5));
        var order = new List<string>();

        var exit = ElevatedDaemonRecovery.Run(
            Args(),
            recovery,
            new RecordingDaemon(order).Run,
            TimeSpan.FromMilliseconds(200),
            stopOnly: true);

        Assert.Equal(0, exit);
        Assert.Contains(DaemonPid, recovery.Ended);
        Assert.False(order.Contains("run"), "a stop-only run never becomes the replacement daemon");
    }

    [Fact]
    public void A_Reused_Pid_Is_Refused_And_Nothing_Is_Started()
    {
        // The identity check is the recovery's own: a process that started after the snapshot
        // that named this PID is not the daemon, and ending it would kill a process this
        // product has no authority over.
        var recovery = new FakeRecovery();
        recovery.Plant(DaemonPid, SeenAt.AddMinutes(5));
        var order = new List<string>();

        var exit = ElevatedDaemonRecovery.Run(
            Args(),
            recovery,
            new RecordingDaemon(order).Run,
            TimeSpan.FromMilliseconds(200),
            stopOnly: true);

        Assert.Equal(ElevatedDaemonRecovery.IdentityRejected, exit);
        Assert.Empty(recovery.Ended);
        Assert.Empty(order);
    }

    [Fact]
    public void An_End_The_OS_Refuses_Is_A_Failure_And_Nothing_Is_Started()
    {
        var recovery = new FakeRecovery();
        recovery.Plant(DaemonPid, SeenAt.AddMinutes(-5));
        recovery.RefuseEnd = true;
        var order = new List<string>();

        var exit = ElevatedDaemonRecovery.Run(
            Args(),
            recovery,
            new RecordingDaemon(order).Run,
            TimeSpan.FromMilliseconds(200),
            stopOnly: true);

        Assert.Equal(ElevatedDaemonRecovery.EndFailed, exit);
        Assert.Empty(recovery.Ended);
        Assert.Empty(order);
    }

    [Fact]
    public void The_Recovery_Entry_Still_Becomes_The_Replacement()
    {
        // The other entry is unchanged: same identity checks, same end, and it does start the
        // replacement - which is why the update asks for the stop-only one instead.
        var recovery = new FakeRecovery();
        recovery.Plant(DaemonPid, SeenAt.AddMinutes(-5));
        var order = new List<string>();

        var exit = ElevatedDaemonRecovery.Run(
            [
                "--recover-daemon",
                "--pid",
                DaemonPid.ToString(CultureInfo.InvariantCulture),
                "--seen-at",
                SeenAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
            ],
            recovery,
            new RecordingDaemon(order).Run,
            TimeSpan.FromMilliseconds(200));

        Assert.Equal(0, exit);
        Assert.Contains(DaemonPid, recovery.Ended);
        Assert.Equal(["run"], order);
    }
}
