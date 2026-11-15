using System.Text.Json;
using DeziroJoy.Core.Diagnostics;

namespace DeziroJoy.App.Services;

/// <summary>
/// Writes one atomic JSON snapshot per second for an external process sampler. Enabled only
/// when DEZIROJOY_PERF_METRICS=1 and DEZIROJOY_PERF_DIR names a baseline directory.
/// A reset request lets the runner exclude process startup and warm-up from steady-state data.
/// </summary>
public sealed class PerformanceReporter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _directory;
    private readonly string _role;
    private readonly string _scenario;
    private readonly string _snapshotPath;
    private readonly string _resetPath;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;

    private PerformanceReporter(string directory, string role, string scenario)
    {
        _directory = directory;
        _role = role;
        _scenario = scenario;
        Directory.CreateDirectory(directory);
        _snapshotPath = Path.Combine(directory, $"metrics-{role}-{Environment.ProcessId}.json");
        _resetPath = Path.Combine(directory, $"reset-{Environment.ProcessId}.request");
        WriteSnapshot();
        _loop = Task.Run(RunAsync);
    }

    public string SnapshotPath => _snapshotPath;

    public static PerformanceReporter? StartIfEnabled(IReadOnlyList<string> args)
    {
        if (!RuntimePerformance.Enabled)
        {
            return null;
        }

        var directory = Environment.GetEnvironmentVariable("DEZIROJOY_PERF_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var role = args.Any(arg => arg.Equals("--daemon", StringComparison.OrdinalIgnoreCase)) ? "daemon"
            : args.Any(arg => arg.Equals("--tray", StringComparison.OrdinalIgnoreCase)) ? "presentation"
            : args.Any(arg => arg.Equals("--perf-control", StringComparison.OrdinalIgnoreCase)) ? "control"
            : "gui";
        var scenario = Environment.GetEnvironmentVariable("DEZIROJOY_VIRTUAL_PAD") ?? "physical";
        if (Environment.GetEnvironmentVariable("DEZIROJOY_PERF_OVERLAY") == "1")
        {
            scenario += "+overlay";
        }

        return new PerformanceReporter(directory, role, scenario);
    }

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _shutdown.Token).ConfigureAwait(false);
                if (File.Exists(_resetPath))
                {
                    RuntimePerformance.Reset();
                    File.Delete(_resetPath);
                }

                WriteSnapshot();
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Diagnostics must never take down mapping or presentation. Surface the failure
                // in the scenario directory so an incomplete baseline cannot look successful.
                try
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(_directory, $"reporter-error-{_role}-{Environment.ProcessId}.log"),
                        exception.ToString()).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private void WriteSnapshot()
    {
        var snapshot = RuntimePerformance.Snapshot(_role, _scenario);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var temporary = _snapshotPath + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _snapshotPath, overwrite: true);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        WriteSnapshot();
        _shutdown.Dispose();
    }
}
