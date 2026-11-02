using System.IO.Pipes;
using System.Runtime.InteropServices;
using DeziroJoy.App.Ipc;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Windows.Output;

namespace DeziroJoy.App.Services;

/// <summary>
/// Headless entry point. Builds the same provider -&gt; sink -&gt; <see cref="EngineHost"/>
/// wiring as the GUI through <see cref="EngineComposition"/> and runs the mapping loop
/// until the process is asked to terminate.
///
/// No Avalonia window, no <c>App</c> object and no UI stack are ever constructed, which is
/// what keeps the resident process near the bare .NET memory floor instead of the ~145 MB
/// Avalonia baseline (see docs/ELEVATION-AND-DAEMON.md).
/// </summary>
public static class DaemonHost
{
    /// <summary>Marker the startup summary ends with; the process test waits for it.</summary>
    public const string StartupCompleteMarker = "engine started";

    /// <summary>Poll interval of the mapping loop; matches the GUI's engine cadence.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// Runs the daemon until SIGINT, SIGTERM or SIGHUP arrives, then stops the engine and
    /// exits. Every supported termination path exits from its own handler, so this method
    /// only returns if a future runtime lets a handler return normally.
    ///
    /// The daemon is the authority: it owns the provider, sink, <see cref="EngineHost"/> and
    /// the injection mutex, and it serves the version-1 mapping control channel on
    /// <see cref="IpcContract.PipeName"/> for the whole of its life. A second daemon that
    /// finds this one already listening exits cleanly instead of competing for the slot
    /// (section 4.7).
    /// </summary>
    public static int Run()
    {
        if (IsAnotherDaemonListening())
        {
            // Section 4.7: clean exit, no pipe theft, no mutex theft, no double injection.
            Console.WriteLine("[DeziroJoy] another daemon is already listening on the mapping pipe; exiting without competing.");
            return 0;
        }

        using var composition = new EngineComposition();

        if (composition.Sink is SendInputOutputSink sendInputSink)
        {
            // UIPI blocking is silent by design; without this the daemon looks perfectly
            // healthy while every injection is being dropped.
            sendInputSink.InjectionFailed += OnInjectionFailed;
        }

        var stateStore = new DaemonStateStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeziroJoy",
            "daemon-state.json"));

        var daemon = new DaemonServer(
            new EngineHostAdapter(composition),
            composition.ProfileStore,
            stateStore);

        // Resume the mapping the user last asked for before the pipe opens, so a GUI that
        // never connects still gets the persisted configuration.
        daemon.ResumeDesiredState();

        using var pipeCts = new CancellationTokenSource();
        var pipeTask = ServePipeAsync(daemon, pipeCts.Token);

        composition.Host.RefreshPresence();

        // .NET 10 no longer installs SIGTERM/SIGHUP handlers, so without one the default
        // action (terminate) applies and the daemon would die without releasing held keys.
        // The handler must not return: the default disposition is applied once it does, so
        // it shuts down inline and exits explicitly.
        void ShutdownAndExit()
        {
            pipeCts.Cancel();
            Shutdown(composition);
            Console.Out.Flush();
            Environment.Exit(0);
        }
        // Ctrl+C in a terminal. SIGINT is deliberately left to the console layer: .NET owns
        // that signal, and a PosixSignalRegistration for it never fires without a TTY.
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            ShutdownAndExit();
        };

        PosixSignalRegistration? terminate = null;
        PosixSignalRegistration? hangup = null;

        if (!OperatingSystem.IsWindows())
        {
            terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => ShutdownAndExit());
            hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => ShutdownAndExit());
        }

        // StartupCompleteMarker also means termination handlers are installed: clients and
        // process tests may signal us as soon as they observe it without racing registration.
        Console.WriteLine(BuildStartupSummary(composition, DefaultInterval));

        // Park the main thread; a signal handler does the real shutdown.
        Thread.Sleep(Timeout.Infinite);

        terminate?.Dispose();
        hangup?.Dispose();

        return 0;
    }
    /// <summary>
    /// Reconciles device presence and renders the startup summary. Returns the text so the
    /// daemon and the tests see exactly the same lines.
    /// </summary>
    public static string BuildStartupSummary(EngineComposition composition, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(composition);

        composition.Host.RefreshPresence();

        var devices = composition.Host.Controllers.Count;
        var sink = composition.Sink.GetType().Name;
        var sinkNote = composition.SinkError is null ? string.Empty : $"（{composition.SinkError}）";
        var elevation = DescribeElevation(composition);

        return string.Join(
            Environment.NewLine,
            "[DeziroJoy] daemon starting · no UI stack loaded",
            $"[DeziroJoy] platform: {EngineComposition.PlatformName}",
            $"[DeziroJoy] output sink: {sink}{sinkNote}",
            $"[DeziroJoy] devices detected: {devices}",
            $"[DeziroJoy] engine started · interval {interval.TotalMilliseconds:0.#} ms · elevation: {elevation}");
    }

    /// <summary>Renders the shutdown line printed once the engine has been stopped.</summary>
    /// <param name="injectionLog">
    /// The diagnostic buffer, when the composition has one. UIPI drops blocked input without an
    /// error, so this is the only evidence a failed mapping leaves behind.
    /// </param>
    public static string BuildShutdownSummary(EngineComposition composition, InjectionLog? injectionLog = null)
    {
        ArgumentNullException.ThrowIfNull(composition);

        var injectionNote = injectionLog is null
            ? "injected: unavailable"
            : BuildInjectionNote(injectionLog);

        return $"[DeziroJoy] engine stopped · devices at shutdown: {composition.Host.Controllers.Count} · " +
            $"daemon exited cleanly · {injectionNote}";
    }

    /// <summary>
    /// Counts what the log recorded. A blocked event is one SendInput refused, which on Windows
    /// means UIPI dropped input aimed at an elevated target - reported separately because it
    /// arrives with no error anywhere else.
    /// </summary>
    private static string BuildInjectionNote(InjectionLog log)
    {
        var total = log.Count;
        if (total == 0)
        {
            return "injected: 0 events";
        }

        var blocked = log.Snapshot(total).Count(record => !record.Success);

        return blocked == 0
            ? $"injected: {total} events"
            : $"injected: {total} events · {blocked} blocked";
    }

    /// <summary>Stops the mapping loop, which also releases every key the dispatcher holds.</summary>
    private static void Shutdown(EngineComposition composition)
    {
        composition.Host.Stop();
        Console.WriteLine(BuildShutdownSummary(composition, composition.InjectionLog));
    }

    /// <summary>
    /// Serves the mapping control channel for the daemon's whole life, one task per connected
    /// GUI. A client disconnect ends only that client's loop; the engine and the injection
    /// mutex are untouched, which is what makes the daemon resident.
    /// </summary>
    private static async Task ServePipeAsync(DaemonServer daemon, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(
                    IpcContract.PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    MappingPipeOptions(OperatingSystem.IsWindows()));

                await using (server)
                {
                    await server.WaitForConnectionAsync(cancellationToken);
                    await daemon.ServeConnectionAsync(server, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                // A pipe failure must never take the mapping down with it: the engine is the
                // daemon's reason to exist, the pipe is only how a GUI talks to it.
                Console.WriteLine($"[DeziroJoy] mapping pipe error: {exception.Message}");
            }
        }
    }

    internal static PipeOptions MappingPipeOptions(bool isWindows)
        => PipeOptions.Asynchronous | (isWindows ? PipeOptions.CurrentUserOnly : PipeOptions.None);

    /// <summary>
    /// True when another daemon already answers on the mapping pipe. The pipe, not the mutex,
    /// is the liveness signal: a daemon holds the mutex only while it is injecting, but it
    /// listens for its whole life (section 4.3).
    /// </summary>
    private static bool IsAnotherDaemonListening()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", IpcContract.PipeName, PipeDirection.InOut, PipeOptions.None);
            client.Connect(TimeSpan.FromMilliseconds(250));
            return client.IsConnected;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Adapts the real composition to <see cref="IDaemonHost"/>. The counters are totalled from
    /// the injection log, which is the only record of what the sink actually injected - and of
    /// what UIPI silently dropped.
    /// </summary>
    private sealed class EngineHostAdapter : IDaemonHost
    {
        private readonly EngineComposition _composition;

        public EngineHostAdapter(EngineComposition composition) => _composition = composition;

        public bool IsRunning => _composition.Host.IsRunning;

        public bool IsMappingEnabled => _composition.Host.IsMappingEnabled;

        public bool HasController => _composition.Host.Controllers.Count > 0;

        public int InjectorPid => Environment.ProcessId;

        public string? InjectorIntegrity => _composition.IsElevationSupported
            ? _composition.IsElevated ? "high" : "medium"
            : null;

        public long InjectedEvents => _composition.InjectionLog.Count;

        public long BlockedEvents => _composition.InjectionLog.Snapshot(_composition.InjectionLog.Count)
            .Count(record => !record.Success);

        public IReadOnlyList<string> ConnectedDeviceIds
            => _composition.Host.Controllers.Select(runtime => runtime.Descriptor.DeviceId).ToList();

        public void Start() => _composition.Host.Start(DaemonHost.DefaultInterval);

        public void StopMapping() => _composition.Host.Stop();

        public void SetMappingEnabled(bool enabled) => _composition.Host.SetMappingEnabled(enabled);

        public void SetInjectionSuspended(bool suspended) => _composition.Host.SetInjectionSuspended(suspended);

        public void Reload(string deviceId, GamepadProfile profile) => _composition.Host.Reload(deviceId, profile);
    }

    private static string DescribeElevation(EngineComposition composition)
    {
        if (!composition.IsElevationSupported)
        {
            return "not applicable on this platform";
        }

        return composition.IsElevated
            ? "elevated (high integrity)"
            : "not elevated (medium integrity) - UIPI will block injection into elevated targets";
    }

    private static void OnInjectionFailed(object? sender, SendInputFailureEventArgs args)
    {
        Console.WriteLine(
            $"[DeziroJoy] input injection blocked: {args.InjectedCount}/{args.RequestedCount} events injected, " +
            $"Win32 error {args.ErrorCode}. A medium-integrity process cannot inject into an elevated target " +
            "(UIPI); relaunch elevated.");
    }
}
