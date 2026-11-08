using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// Process-level proof that the headless daemon starts, reports its state and stops
/// cleanly on SIGTERM. The daemon is the one path that must never load Avalonia, so it is
/// exercised as a real process rather than through the in-process API.
/// </summary>
/// <remarks>
/// SIGINT is not asserted here: .NET's console layer owns that signal and it is not
/// deliverable to a process whose stdout is a pipe, which is what a test harness has.
/// Ctrl+C is verified manually against a real pseudo-terminal instead.
///
/// Shares a collection with <see cref="InjectionMutexAbandonmentTests"/>: the daemon
/// resolves the production mutex name, so two daemons acquiring at once would refuse
/// each other.
/// </remarks>
[Collection("DeziroJoy.DaemonProcess")]
public class DaemonHostProcessTests
{
    [Fact]
    public async Task Daemon_Starts_Reports_No_Devices_And_Stops_Cleanly_On_Sigterm()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("SIGTERM delivery is POSIX-only.");
        }

        var executable = LocateExecutable();
        Assert.True(File.Exists(executable), $"daemon executable not found at {executable}");

        using var process = StartDaemon(executable);
        var output = new StringBuilder();
        var pump = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                lock (output)
                {
                    output.AppendLine(line);
                }
            }
        }, TestContext.Current.CancellationToken);

        var started = await WaitForOutputAsync(output, DaemonHost.StartupCompleteMarker, TimeSpan.FromSeconds(30));

        Assert.True(started, $"daemon never reported startup. Output:\n{Snapshot(output)}");
        Assert.Contains("devices detected: 0", Snapshot(output));

        using (var second = StartDaemon(executable))
        {
            try
            {
                var secondExited = await WaitForExitAsync(second, TimeSpan.FromSeconds(15));
                Assert.True(secondExited, "a second daemon must exit instead of competing");
                Assert.Equal(0, second.ExitCode);
            }
            finally
            {
                if (!second.HasExited)
                {
                    second.Kill(entireProcessTree: true);
                }
            }
        }

        SendSignal(process, "TERM");

        var exited = await WaitForExitAsync(process, TimeSpan.FromSeconds(15));
        Assert.True(exited, $"daemon did not exit after SIGTERM. Output:\n{Snapshot(output)}");

        await pump;

        var transcript = Snapshot(output);
        Assert.Contains("daemon exited cleanly", transcript);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Daemon_Exits_Cleanly_After_An_Ipc_Shutdown()
    {
        var executable = LocateExecutable();
        using var process = StartDaemon(executable);
        var output = new StringBuilder();
        var pump = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                lock (output)
                {
                    output.AppendLine(line);
                }
            }
        }, TestContext.Current.CancellationToken);

        try
        {
            Assert.True(
                await WaitForOutputAsync(output, DaemonHost.StartupCompleteMarker, TimeSpan.FromSeconds(30)),
                Snapshot(output));
            await using var controller = new DaemonController(new PipeEndpoint());
            await controller.ConnectAsync(TestContext.Current.CancellationToken);

            var shutdown = await controller.ShutdownDaemonAsync(TestContext.Current.CancellationToken);
            Assert.True(
                shutdown.Outcome == MutationOutcome.Accepted,
                $"shutdown={shutdown}; error={shutdown.ErrorCode}: {shutdown.ErrorMessage}; output={Snapshot(output)}");
            Assert.True(
                await WaitForExitAsync(process, TimeSpan.FromSeconds(15)),
                $"daemon did not exit after IPC shutdown. Output:\n{Snapshot(output)}");
            await pump;
            Assert.Contains("daemon exited cleanly", Snapshot(output));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static string LocateExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "DeziroJoy.exe" : "DeziroJoy";
        return Path.Combine(AppContext.BaseDirectory, name);
    }

    private static Process StartDaemon(string executable)
    {
        var startInfo = new ProcessStartInfo(executable, "--daemon")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        return Process.Start(startInfo) ?? throw new InvalidOperationException("could not start the daemon");
    }

    private static void SendSignal(Process process, string signal)
    {
        using var killer = Process.Start(new ProcessStartInfo("kill", $"-{signal} {process.Id}")
        {
            UseShellExecute = false,
        });

        killer?.WaitForExit(TimeSpan.FromSeconds(5));
    }

    private static async Task<bool> WaitForOutputAsync(StringBuilder output, string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            lock (output)
            {
                if (output.ToString().Contains(marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            await Task.Delay(50);
        }

        lock (output)
        {
            return output.ToString().Contains(marker, StringComparison.Ordinal);
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        cancellation.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string Snapshot(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }

    private sealed class PipeEndpoint : IDaemonEndpoint
    {
        public Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public async Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                IpcContract.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(TimeSpan.FromSeconds(5), cancellationToken);
                return new NamedPipeDaemonChannel(pipe);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }
    }
}
