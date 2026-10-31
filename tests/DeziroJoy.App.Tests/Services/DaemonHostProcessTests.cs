using System.Diagnostics;
using System.Text;
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
/// </remarks>
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

        SendSignal(process, "TERM");

        var exited = await WaitForExitAsync(process, TimeSpan.FromSeconds(15));
        Assert.True(exited, $"daemon did not exit after SIGTERM. Output:\n{Snapshot(output)}");

        await pump;

        var transcript = Snapshot(output);
        Assert.Contains("daemon exited cleanly", transcript);
        Assert.Equal(0, process.ExitCode);
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
}
