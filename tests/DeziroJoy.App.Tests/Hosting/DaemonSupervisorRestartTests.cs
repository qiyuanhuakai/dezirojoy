using System.Diagnostics;
using Avalonia.Headless.XUnit;
using DeziroJoy.App.Hosting;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Hosting;

/// <summary>
/// The safe restart the input-backend switch asks for. The provider is built into the
/// daemon's composition at startup and cannot be hot-swapped, so a new selection takes
/// effect only on a new process - and a restart that cannot confirm the old daemon's exit
/// fails rather than starting a second injector beside it.
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
            isDaemonUnitInstalled: () => false);

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
            });

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
            isDaemonUnitInstalled: () => false);

        supervisor.EnsureRunning();

        var restarted = supervisor.Restart();

        // The old daemon is still alive: a replacement would compete for the injection
        // mutex and double every input, so nothing was started and the caller is told.
        Assert.False(restarted);
        Assert.Empty(launcher.Started);
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

    private sealed class FakeElevation : IElevationService
    {
        public bool IsSupported => true;

        public bool IsElevated => false;

        public bool RelaunchElevated(string? arguments) => false;
    }
}
