using System.Diagnostics;
using System.Text;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// Groups every test that runs the real daemon binary. The daemon resolves the production
/// mutex name, so two daemons acquiring at once would refuse each other; serialising the
/// classes keeps one daemon per slot.
/// </summary>
[CollectionDefinition("DeziroJoy.DaemonProcess")]
public sealed class DaemonProcessGroup;

/// <summary>
/// A holder that dies without releasing must not block the next start - a leaked mutex
/// would block mapping until reboot, which is far worse than the bug being fixed.
///
/// The daemon is the real second process: it builds the same <see cref="EngineHost"/>
/// wiring and takes the production mutex on start. Killing it hard (SIGKILL, no graceful
/// shutdown, no release) leaves the slot held by a dead process, and the next acquire
/// must still succeed.
///
/// What that relies on, per platform:
/// - Linux: .NET named mutexes are file locks, and the kernel drops them with the
///   process, so the next waiter simply acquires.
/// - Windows: the mutex is abandoned and the next waiter is granted it, which .NET
///   surfaces as <see cref="AbandonedMutexException"/> - treated as a successful acquire.
/// </summary>
[Collection("DeziroJoy.DaemonProcess")]
public class InjectionMutexAbandonmentTests
{
    [Fact]
    public async Task A_Process_That_Dies_Holding_The_Mutex_Does_Not_Block_The_Next_Start()
    {
        var executable = LocateExecutable();
        Assert.True(File.Exists(executable), $"daemon executable not found at {executable}");

        using var daemon = StartDaemon(executable);
        var output = new StringBuilder();
        var pump = Task.Run(() =>
        {
            string? line;
            while ((line = daemon.StandardOutput.ReadLine()) is not null)
            {
                lock (output)
                {
                    output.AppendLine(line);
                }
            }
        }, TestContext.Current.CancellationToken);

        var started = await WaitForOutputAsync(output, DaemonHost.StartupCompleteMarker, TimeSpan.FromSeconds(30));
        Assert.True(started, $"daemon never reported startup. Output:\n{Snapshot(output)}");

        // Kill hard: no signal handler runs, so nothing releases the slot gracefully.
        daemon.Kill(entireProcessTree: true);
        await daemon.WaitForExitAsync(TestContext.Current.CancellationToken);
        await pump;

        using var reacquired = InjectionMutex.TryAcquire(InjectionMutex.DefaultName);
        Assert.NotNull(reacquired);
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

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        lock (output)
        {
            return output.ToString().Contains(marker, StringComparison.Ordinal);
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
