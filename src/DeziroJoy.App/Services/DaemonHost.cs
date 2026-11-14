using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DeziroJoy.App.Ipc;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Linux.Evdev;
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
/// <para>
/// Being the mapping authority, the daemon also runs the foreground rules itself: the same
/// <see cref="ForegroundWatcher"/> the GUI drives over its own host in local mode, here over
/// the daemon's own <see cref="EngineHost"/> and for the daemon's whole life. A window that
/// connects only edits and displays the table, so closing to the tray - the primary resident
/// mode - never stops the rules from running.
/// </para>
/// </summary>
public static class DaemonHost
{
    /// <summary>Marker the startup summary ends with; the process test waits for it.</summary>
    public const string StartupCompleteMarker = "engine started";

    /// <summary>Poll interval of the mapping loop; matches the GUI's engine cadence.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(2);

    internal const string DaemonInstanceMutexName = @"Global\DeziroJoy.Daemon";

    /// <summary>
    /// Runs the daemon until SIGINT, SIGTERM or SIGHUP arrives, then stops the engine and
    /// exits. Every supported termination path exits from its own handler, so this method
    /// only returns if a future runtime lets a handler return normally.
    ///
    /// The daemon is the authority: it owns the provider, sink, <see cref="EngineHost"/>, the
    /// foreground rules and the injection mutex, and it serves the version-1 mapping control
    /// channel on <see cref="IpcContract.PipeName"/> for the whole of its life. A second daemon
    /// that finds this one already listening exits cleanly instead of competing for the slot
    /// (section 4.7).
    /// </summary>
    public static int Run()
    {
        using var instance = new Mutex(initiallyOwned: false, DaemonInstanceMutexName);
        bool ownsInstance;
        try
        {
            ownsInstance = instance.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            ownsInstance = true;
        }

        if (!ownsInstance)
        {
            Console.WriteLine("[DeziroJoy] another daemon owns the instance slot; exiting without competing.");
            return 0;
        }

        try
        {
            return RunOwned();
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    private static int RunOwned()
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

        // Surface each device's active temporary layer in the snapshot so the GUI reads what
        // the engine is really running instead of deriving it from the profile body.
        daemon.LayerStateSource = () => composition.Host.ActiveLayers;

        composition.Host.PresetSwitchRequested += (_, slot) =>
            daemon.RequestPresetSlotSwitch(slot);

        composition.Host.ScreenKeyboardToggleRequested += () =>
            daemon.RequestScreenKeyboardToggle();

        // Materialize the four slots before restoring the one global active slot.
        composition.Host.RefreshPresence();

        // Resume the mapping the user last asked for before the pipe opens, so a GUI that
        // never connects still gets the persisted configuration.
        daemon.ResumeDesiredState();

        using var pipeCts = new CancellationTokenSource();
        using var shutdown = new ManualResetEventSlim();
        var pipeTask = ServePipeAsync(daemon, pipeCts.Token);
        var presenceTask = MonitorPresenceAsync(composition.Host, pipeCts.Token);

        // The mapping authority runs the foreground rules for the daemon's whole life, with no
        // GUI in the picture: the engine host is the target - the same one the GUI drives over
        // its own host in local mode - and the shared rule table on disk is where the rules
        // come from, re-read on every poll because a window's 保存 writes that file and the
        // control channel carries no verb for a rule edit. Closing to the tray, the primary
        // resident mode, must not stop the rules from running.
        using var foreground = new DaemonForeground(
            composition.Host,
            composition.ProfileStore,
            daemon,
            ForegroundSourceFactory.Create(),
            new ForegroundRuleStore(),
            pipeCts.Token);

        daemon.ShutdownRequested += () => shutdown.Set();

        composition.Host.RefreshPresence();

        // .NET 10 no longer installs SIGTERM/SIGHUP handlers, so without one the default
        // action (terminate) applies and the daemon would die without releasing held keys.
        // The handler must not return: the default disposition is applied once it does, so
        // it shuts down inline and exits explicitly.
        void ShutdownAndExit()
        {
            pipeCts.Cancel();
            Shutdown(composition);
            foreground.Stop();
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
        Console.WriteLine(BuildForegroundNote(foreground.Watcher));

        // The IPC shutdown path wakes the main thread only after its acknowledgement was
        // written. Signal handlers retain their explicit-exit behaviour on Unix.
        shutdown.Wait();
        pipeCts.Cancel();
        Shutdown(composition);
        Task.WaitAll([pipeTask, presenceTask, foreground.Loop], TimeSpan.FromSeconds(5));
        foreground.Stop();
        Console.Out.Flush();

        terminate?.Dispose();
        hangup?.Dispose();

        return 0;
    }

    /// <summary>
    /// Builds the resident daemon's own foreground watcher: the platform source, the daemon's
    /// engine host as the target, and the shared rule table as the starting point. The target
    /// is <see cref="EngineHostForegroundTarget"/> - the same one the GUI drives over its own
    /// host in local mode - because in daemon mode this process is the one that owns mapping,
    /// so a rule reaches the engine directly rather than over the control channel. Neither path
    /// touches the injection mutex, so the resident model survives both.
    /// </summary>
    internal static ForegroundWatcher CreateForegroundWatcher(
        EngineHost host,
        IProfileStore profileStore,
        IForegroundSource source,
        ForegroundRuleStore rules)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(profileStore);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);

        return new ForegroundWatcher(
            source,
            new EngineHostForegroundTarget(host, new PresetStore(profileStore)),
            rules.Load());
    }

    /// <summary>
    /// Drives the daemon's foreground watcher for its whole life: one poll per
    /// <see cref="ForegroundWatcher.DefaultInterval"/>, and a re-read of the shared rule table
    /// whenever the file on disk changed. The file is the channel - the control protocol has
    /// no verb for a rule edit - so a daemon that never re-read it would run whatever table
    /// the last window left for the rest of its life. Independent of any GUI by construction:
    /// the loop runs whether or not a window is connected, which is what makes closing to the
    /// tray stop mattering.
    /// </summary>
    internal static async Task MonitorForegroundAsync(
        ForegroundWatcher watcher,
        ForegroundRuleStore rules,
        CancellationToken cancellationToken,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(rules);

        var poll = interval ?? ForegroundWatcher.DefaultInterval;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // An edit lands at once - the same contract the panel's 保存 has - and a table
                // that has not changed costs one file read and no parse.
                if (rules.LoadIfChanged() is { } edited)
                {
                    watcher.Update(edited);
                }

                watcher.Refresh();

                await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A failing poll must never take the mapping down with it: the rules are a
                // convenience over the engine, and the engine is the daemon's reason to exist.
                Console.WriteLine($"[DeziroJoy] foreground watcher error: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// The journal's one line about the foreground rules: how many are in charge and at what
    /// cadence. The daemon has no panel to explain itself with - stdout under systemd is the
    /// only surface a headless install has - so a resident install that never opens a window
    /// still gets told the rules are running.
    /// </summary>
    internal static string BuildForegroundNote(ForegroundWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);

        var settings = watcher.Settings;
        var table = settings.Enabled
            ? $"{settings.Rules.Count} 条规则"
            : "已停用";

        return $"[DeziroJoy] 前台规则：{table} · 轮询 {ForegroundWatcher.DefaultInterval.TotalMilliseconds:0.#} ms";
    }

    private static async Task MonitorPresenceAsync(EngineHost host, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (host.IsRunning)
                {
                    host.RefreshPresence();
                }
                else
                {
                    host.SamplePresence();
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[DeziroJoy] presence monitor error: {exception.Message}");
            }
        }
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
        var deviceNote = BuildDeviceNote(composition);

        var lines = new List<string>
        {
            "[DeziroJoy] daemon starting · no UI stack loaded",
            $"[DeziroJoy] platform: {EngineComposition.PlatformName}",
            $"[DeziroJoy] output sink: {sink}{sinkNote}",
            $"[DeziroJoy] devices detected: {devices}",
        };

        if (deviceNote is not null)
        {
            lines.Add($"[DeziroJoy] {deviceNote}");
        }

        lines.Add($"[DeziroJoy] engine started · interval {interval.TotalMilliseconds:0.#} ms · elevation: {elevation}");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Why the device count is what it is, on the platform that can tell. A zero count with no
    /// explanation is the failure this exists for: "nothing plugged in", "no permission" and
    /// "no input subsystem" need three different fixes, and the daemon's stdout - the journal
    /// under systemd - is the only place any of them is visible. A non-zero count is explained
    /// too when the pads it found report axes no mapping can reach: those are quoted for an
    /// <c>evtest</c> comparison instead of passing for a broken stick. See
    /// <see cref="BuildDeviceNote(EvdevScanDiagnostic?, int)"/> for the per-diagnostic rule.
    /// </summary>
    private static string? BuildDeviceNote(EngineComposition composition)
    {
        if (composition.Provider is not EvdevGamepadProvider evdev)
        {
            return null;
        }

        return BuildDeviceNote(evdev.LastScanDiagnostic, composition.Host.Controllers.Count);
    }

    /// <summary>
    /// The device note for one scan diagnostic and the pad count the host is actually serving,
    /// or null when there is nothing to say. A connected pad used to suppress the note
    /// entirely, which hid the unhandled-axis diagnostic a user needs to compare a dead stick
    /// or trigger against <c>evtest</c>. With a pad only the axis segment is quoted:
    /// <see cref="EvdevScanDiagnostic.Guidance"/> already drops the outcome wording once a
    /// gamepad was found, so it is exactly that segment - and empty when every reported axis
    /// is handled, leaving a healthy pad silent. A diagnostic that disagrees with the
    /// connected pad (a stale scan still reading "nothing plugged in") says nothing rather
    /// than contradicting the device in view. Without a pad the full guidance - outcome plus
    /// any unhandled axes - stays as it was.
    /// </summary>
    internal static string? BuildDeviceNote(EvdevScanDiagnostic? diagnostic, int controllerCount)
    {
        if (diagnostic is null)
        {
            return null;
        }

        var quoteable = controllerCount == 0 || diagnostic.Outcome == EvdevScanOutcome.GamepadsFound;

        return quoteable && !string.IsNullOrEmpty(diagnostic.Guidance)
            ? diagnostic.Guidance
            : null;
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
    internal static async Task ServePipeAsync(
        DaemonServer daemon,
        CancellationToken cancellationToken,
        string? pipeName = null)
    {
        pipeName ??= IpcContract.PipeName;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var server = CreateMappingPipe(pipeName);
                try
                {
                    await server.WaitForConnectionAsync(cancellationToken);
                }
                catch
                {
                    await server.DisposeAsync();
                    throw;
                }

                _ = ServeConnectedClientAsync(daemon, server, cancellationToken);
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

    private static async Task ServeConnectedClientAsync(
        DaemonServer daemon,
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        await using (server)
        {
            try
            {
                await daemon.ServeConnectionAsync(server, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[DeziroJoy] mapping client error: {exception.Message}");
            }
        }
    }

    private static NamedPipeServerStream CreateMappingPipe(string pipeName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }

        var userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("the current Windows token has no user SID");
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            CreateMappingPipeSecurity(userSid));
    }

    [SupportedOSPlatform("windows")]
    internal static PipeSecurity CreateMappingPipeSecurity(string userSid)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(userSid),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        return security;
    }

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

        public EngineHostAdapter(EngineComposition composition)
        {
            _composition = composition;
            _composition.Host.Sampled += ForwardSample;
            _composition.Host.PresenceSampled += ForwardSample;
        }

        public event Action<string, DeziroJoy.Core.Input.IGamepadState>? InputSampled;

        private void ForwardSample(object? sender, ControllerSampledEventArgs args)
            => InputSampled?.Invoke(args.Runtime.Descriptor.DeviceId, args.State);

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

        public IReadOnlyList<ConnectedDeviceSnapshot> ConnectedDevices
            => _composition.Host.Controllers.Select(runtime => new ConnectedDeviceSnapshot(
                runtime.Descriptor.DeviceId,
                runtime.Descriptor.Name,
                runtime.Descriptor.Layout)).ToList();

        public event Action? DevicesChanged
        {
            add => _composition.Host.ControllersChanged += value;
            remove => _composition.Host.ControllersChanged -= value;
        }

        public GamepadProfile? GetProfile(string deviceId)
            => _composition.Host.Controllers.SingleOrDefault(
                runtime => string.Equals(runtime.Descriptor.DeviceId, deviceId, StringComparison.Ordinal))?.Profile;

        public void SamplePresence() => _composition.Host.SamplePresence();

        public void Start() => _composition.Host.Start(DaemonHost.DefaultInterval);

        public void StopMapping() => _composition.Host.Stop();

        public void SetMappingEnabled(bool enabled) => _composition.Host.SetMappingEnabled(enabled);

        public void SetInjectionSuspended(bool suspended) => _composition.Host.SetInjectionSuspended(suspended);

        public bool IsNativeGamepadPaused => _composition.Host.IsNativeGamepadPaused;

        public void SetNativeGamepadPaused(bool paused) => _composition.Host.SetNativeGamepadPaused(paused);

        public void Reload(string deviceId, GamepadProfile profile) => _composition.Host.Reload(deviceId, profile);

        public void SwitchPresetSlot(int slot, IReadOnlyDictionary<string, GamepadProfile> profiles)
            => _composition.Host.SwitchPresetSlot(slot, profiles);

        public void InjectText(string text) => _composition.Host.InjectText(text);

        public void TapKey(GamepadKey key, KeyModifiers modifiers)
            => _composition.Host.TapKey(key, modifiers);
    }

    /// <summary>
    /// The resident daemon's foreground authority: the watcher over this process's own engine
    /// host, the mirror that keeps the server's published slot in step with it, and the poll
    /// loop that drives both. Owned as one object so shutdown lifts all three together - the
    /// loop stops, the mirror detaches, and a pause a rule left behind is released - and so a
    /// test can run the whole daemon-side path against a fake foreground source with no window,
    /// no pipe and no GUI anywhere in the picture.
    /// </summary>
    internal sealed class DaemonForeground : IDisposable
    {
        private readonly ForegroundSlotMirror _mirror;

        private bool _stopped;

        public DaemonForeground(
            EngineHost host,
            IProfileStore profileStore,
            DaemonServer server,
            IForegroundSource source,
            ForegroundRuleStore rules,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(server);
            ArgumentNullException.ThrowIfNull(rules);

            Watcher = CreateForegroundWatcher(host, profileStore, source, rules);
            _mirror = new ForegroundSlotMirror(host, server, Watcher, host.ActivePresetSlot);
            _mirror.Start();
            Loop = MonitorForegroundAsync(Watcher, rules, cancellationToken);
        }

        /// <summary>The watcher itself, for the journal note and for tests.</summary>
        public ForegroundWatcher Watcher { get; }

        /// <summary>The poll loop, so the caller can wait for it before lifting the watcher.</summary>
        public Task Loop { get; }

        /// <summary>
        /// Lifts the authority. The caller waits for <see cref="Loop"/> first: disposing the
        /// watcher mid-poll would answer a refresh the loop is still inside.
        /// </summary>
        public void Stop()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _mirror.Stop();
            Watcher.Dispose();
        }

        public void Dispose() => Stop();
    }

    /// <summary>
    /// Keeps the server's published app-wide slot equal to the engine host's. The foreground
    /// watcher drives the host directly - the same target the GUI uses over its own host in
    /// local mode - while the server keeps a second record of the slot that every connected
    /// GUI reads: its selector, its snapshot body, and the slot an edit is saved into. A
    /// rule-driven switch that reached only the host would leave that record quoting a slot the
    /// engine had left, so each poll's outcome is mirrored through the same request path the
    /// leader key uses - one revision bump, and every window follows the rule.
    /// <para>
    /// A GUI's own switch already updates the record through the server; watching the
    /// publication is what stops this mirror from sending it a second time. The two counters
    /// are written from the poll loop and from the pipe threads alike, hence volatile: a lost
    /// update costs one redundant request, never a wrong slot, because the request itself is
    /// resolved by the server from the store.
    /// </para>
    /// </summary>
    private sealed class ForegroundSlotMirror : IDisposable
    {
        private readonly EngineHost _host;

        private readonly DaemonServer _server;

        private readonly ForegroundWatcher _watcher;

        /// <summary>The slot the server last published, whether by this mirror or by a GUI.</summary>
        private volatile int _published;

        /// <summary>The slot this mirror last asked for, so a refusal is not re-sent per poll.</summary>
        private volatile int _requested;

        private bool _stopped;

        public ForegroundSlotMirror(
            EngineHost host,
            DaemonServer server,
            ForegroundWatcher watcher,
            int publishedSlot)
        {
            _host = host;
            _server = server;
            _watcher = watcher;
            _published = publishedSlot;
            _requested = publishedSlot;
        }

        public void Start()
        {
            _server.StateChanged += OnPublished;
            _watcher.StateChanged += OnPolled;
        }

        public void Stop()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _server.StateChanged -= OnPublished;
            _watcher.StateChanged -= OnPolled;
        }

        public void Dispose() => Stop();

        private void OnPublished(StateChangedMessage message)
        {
            _published = message.Snapshot.ActivePresetSlot;
            _requested = _published;
        }

        private void OnPolled()
        {
            var slot = _host.ActivePresetSlot;

            if (slot == _published || slot == _requested)
            {
                return;
            }

            _requested = slot;
            _server.RequestPresetSlotSwitch(slot);
        }
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
