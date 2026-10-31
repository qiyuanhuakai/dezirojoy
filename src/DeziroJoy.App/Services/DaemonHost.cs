using System.Runtime.InteropServices;
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
    /// </summary>
    public static int Run()
    {
        using var composition = new EngineComposition();

        if (composition.Sink is SendInputOutputSink sendInputSink)
        {
            // UIPI blocking is silent by design; without this the daemon looks perfectly
            // healthy while every injection is being dropped.
            sendInputSink.InjectionFailed += OnInjectionFailed;
        }

        Console.WriteLine(BuildStartupSummary(composition, DefaultInterval));

        composition.Host.Start(DefaultInterval);

        // .NET 10 no longer installs SIGTERM/SIGHUP handlers, so without one the default
        // action (terminate) applies and the daemon would die without releasing held keys.
        // The handler must not return: the default disposition is applied once it does, so
        // it shuts down inline and exits explicitly.
        void ShutdownAndExit()
        {
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
    public static string BuildShutdownSummary(EngineComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);

        return $"[DeziroJoy] engine stopped · devices at shutdown: {composition.Host.Controllers.Count} · " +
            "daemon exited cleanly";
    }

    /// <summary>Stops the mapping loop, which also releases every key the dispatcher holds.</summary>
    private static void Shutdown(EngineComposition composition)
    {
        composition.Host.Stop();
        Console.WriteLine(BuildShutdownSummary(composition));
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
