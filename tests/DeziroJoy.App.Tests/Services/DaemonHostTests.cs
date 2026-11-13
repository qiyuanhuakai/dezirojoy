using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.Tests.Ipc;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Profiles;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Windows.Output;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The daemon's only user-visible contract is its log: a startup line naming the platform,
/// the devices it found and the fact that the engine is running, plus a clean shutdown
/// line. Both are rendered by pure helpers so they can be asserted without a process.
/// </summary>
public class DaemonHostTests
{
    [Fact]
    public void Startup_Summary_Names_The_Platform_The_Devices_And_The_Engine()
    {
        using var composition = new EngineComposition();

        var summary = DaemonHost.BuildStartupSummary(composition, TimeSpan.FromMilliseconds(2));

        Assert.Contains("daemon", summary);
        Assert.Contains(EngineComposition.PlatformName, summary);
        Assert.Contains($"devices detected: {composition.Host.Controllers.Count}", summary);
        Assert.Contains("engine started", summary);
    }

    [Fact]
    public void Startup_Summary_States_The_Elevation_Position()
    {
        using var composition = new EngineComposition();

        var summary = DaemonHost.BuildStartupSummary(composition, TimeSpan.FromMilliseconds(2));

        if (composition.IsElevationSupported)
        {
            Assert.Contains(composition.IsElevated ? "elevated" : "not elevated", summary);
        }
        else
        {
            Assert.Contains("not applicable", summary);
        }
    }

    [Fact]
    public void Startup_Summary_Names_The_Output_Sink_Actually_In_Use()
    {
        using var composition = new EngineComposition();

        var summary = DaemonHost.BuildStartupSummary(composition, TimeSpan.FromMilliseconds(2));

        Assert.Contains(composition.Sink.GetType().Name, summary);
    }

    [Fact]
    public void Shutdown_Summary_Reports_A_Clean_Exit()
    {
        using var composition = new EngineComposition();

        var summary = DaemonHost.BuildShutdownSummary(composition);

        Assert.Contains("engine stopped", summary);
        Assert.Contains("daemon exited cleanly", summary);
    }

    [Fact]
    public void Startup_Summary_Rejects_A_Null_Composition()
    {
        Assert.Throws<ArgumentNullException>(() => DaemonHost.BuildStartupSummary(null!, TimeSpan.FromMilliseconds(2)));
    }

    [Fact]
    public void Startup_Summary_Explains_A_Zero_Device_Count_On_Linux()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the evdev diagnostic only exists on Linux");

        using var composition = new EngineComposition();
        var summary = DaemonHost.BuildStartupSummary(composition, TimeSpan.FromMilliseconds(2));

        if (composition.Host.Controllers.Count > 0)
        {
            // A real gamepad is attached: there is nothing to guide the user towards, and the
            // summary must not invent advice.
            Assert.DoesNotContain("usermod", summary);
            return;
        }

        // The whole point of the diagnostic: a bare "devices detected: 0" used to be the only
        // trace, and "no permission" versus "nothing plugged in" need opposite fixes. Which
        // marker appears depends on the machine (denied nodes, empty directory, readable
        // non-gamepad nodes), so every non-empty outcome counts.
        Assert.Contains("devices detected: 0", summary);

        var hasGuidance = summary.Contains("usermod -aG input", StringComparison.Ordinal)
            || summary.Contains("但没有识别到手柄", StringComparison.Ordinal)
            || summary.Contains("连接", StringComparison.Ordinal)
            || summary.Contains("输入子系统", StringComparison.Ordinal);

        Assert.True(hasGuidance, $"expected actionable guidance in the summary:{Environment.NewLine}{summary}");
    }

    [Fact]
    public void Shutdown_Summary_Dumps_The_Injection_Log_So_A_Failed_Mapping_Has_Evidence()
    {
        using var composition = new EngineComposition();

        var summary = DaemonHost.BuildShutdownSummary(composition);

        Assert.Contains("engine stopped", summary);
        Assert.Contains("daemon exited cleanly", summary);
        Assert.Contains("injected:", summary);
    }

    [Fact]
    public void Shutdown_Summary_States_The_Injection_Count_Without_The_Log()
    {
        using var composition = new EngineComposition();

        var summary = DaemonHost.BuildShutdownSummary(composition, injectionLog: null);

        Assert.Contains("engine stopped", summary);
        Assert.Contains("injected: unavailable", summary);
    }

    [Fact]
    public void Shutdown_Summary_Counts_What_The_Log_Recorded()
    {
        var log = new InjectionLog();
        log.Record(new InjectionRecord(1, InjectionAction.KeyDown, (int)GamepadKey.Space, 0, true));
        log.Record(new InjectionRecord(2, InjectionAction.KeyUp, (int)GamepadKey.Space, 0, true));

        using var composition = new EngineComposition();
        var summary = DaemonHost.BuildShutdownSummary(composition, log);

        Assert.Contains("injected: 2 events", summary);
    }

    [Fact]
    public void Shutdown_Summary_Reports_Blocked_Injections_Separately()
    {
        var log = new InjectionLog();
        log.Record(new InjectionRecord(1, InjectionAction.KeyDown, (int)GamepadKey.Space, 0, true));
        log.Record(new InjectionRecord(2, InjectionAction.KeyUp, (int)GamepadKey.Space, 0, false));

        using var composition = new EngineComposition();
        var summary = DaemonHost.BuildShutdownSummary(composition, log);

        Assert.Contains("injected: 2 events", summary);
        Assert.Contains("1 blocked", summary);
    }

    // A connected pad used to suppress the device note entirely, which hid the unhandled-axis
    // diagnostic a user needs to compare a dead stick or trigger against evtest. The note is a
    // pure function of the scan diagnostic and the pad count, so the whole matrix - pad or not,
    // unmapped axes or not - is pinned here without a real kernel.
    [Fact]
    public void A_Connected_Pad_Quotes_Its_Unhandled_Axes()
    {
        var note = DaemonHost.BuildDeviceNote(
            Diagnostic(EvdevScanOutcome.GamepadsFound, gamepadCount: 1,
                unhandledAxes: [new EvdevUnhandledAxis(0x09, 0, 255, 255, "/dev/input/event5")]),
            controllerCount: 1);

        Assert.NotNull(note);
        Assert.Contains("未处理的轴", note);
        Assert.Contains("0x09", note);
        // The outcome wording would contradict the device in view, so a pad quotes only the
        // axis segment.
        Assert.DoesNotContain("usermod", note);
        Assert.DoesNotContain("但没有识别到手柄", note);
    }

    [Fact]
    public void A_Connected_Pad_With_Every_Axis_Handled_Says_Nothing()
    {
        var note = DaemonHost.BuildDeviceNote(
            Diagnostic(EvdevScanOutcome.GamepadsFound, gamepadCount: 1),
            controllerCount: 1);

        Assert.Null(note);
    }

    [Fact]
    public void A_Zero_Device_Scan_Keeps_Its_No_Gamepad_Guidance()
    {
        var note = DaemonHost.BuildDeviceNote(
            Diagnostic(EvdevScanOutcome.NoGamepad, nodeCount: 4),
            controllerCount: 0);

        Assert.NotNull(note);
        Assert.Contains("但没有识别到手柄", note);
    }

    [Fact]
    public void A_Zero_Device_Scan_Keeps_Its_Permission_Guidance()
    {
        var note = DaemonHost.BuildDeviceNote(
            Diagnostic(EvdevScanOutcome.AccessDenied, firstDeniedPath: "/dev/input/event3", errno: 13, nodeCount: 2),
            controllerCount: 0);

        Assert.NotNull(note);
        Assert.Contains("usermod -aG input", note);
    }

    [Fact]
    public void A_Zero_Device_Scan_Quotes_Unhandled_Axes_Beside_Its_Outcome_Guidance()
    {
        // No pad keeps the full guidance: the outcome wording and the unhandled-axis segment
        // both survive, exactly as EvdevScanDiagnostic.Guidance composes them.
        var note = DaemonHost.BuildDeviceNote(
            Diagnostic(EvdevScanOutcome.AccessDenied, firstDeniedPath: "/dev/input/event3", errno: 13, nodeCount: 2, gamepadCount: 1,
                unhandledAxes: [new EvdevUnhandledAxis(0x08, 0, 255, 0, "/dev/input/event4")]),
            controllerCount: 0);

        Assert.NotNull(note);
        Assert.Contains("usermod -aG input", note);
        Assert.Contains("0x08", note);
    }

    [Fact]
    public void A_Connected_Pad_Does_Not_Quote_A_Contradictory_Zero_Device_Diagnostic()
    {
        // A stale scan that still reads a permission wall must not be quoted beside a pad the
        // host is actually serving: "usermod" next to a connected device is worse than silence.
        var note = DaemonHost.BuildDeviceNote(
            Diagnostic(EvdevScanOutcome.AccessDenied, firstDeniedPath: "/dev/input/event3", errno: 13, nodeCount: 2, gamepadCount: 1,
                unhandledAxes: [new EvdevUnhandledAxis(0x08, 0, 255, 0, "/dev/input/event4")]),
            controllerCount: 1);

        Assert.Null(note);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_Null_Diagnostic_Says_Nothing(int controllerCount)
    {
        Assert.Null(DaemonHost.BuildDeviceNote(null, controllerCount));
    }

    private static EvdevScanDiagnostic Diagnostic(
        EvdevScanOutcome outcome,
        string? firstDeniedPath = null,
        int? errno = null,
        int nodeCount = 0,
        int gamepadCount = 0,
        IReadOnlyList<EvdevUnhandledAxis>? unhandledAxes = null)
        => new()
        {
            Outcome = outcome,
            NodeCount = nodeCount,
            DeniedNodeCount = outcome == EvdevScanOutcome.AccessDenied ? nodeCount : 0,
            GamepadCount = gamepadCount,
            FirstDeniedPath = firstDeniedPath,
            Errno = errno,
            UnhandledAxes = unhandledAxes ?? [],
        };

    private static ForegroundRule Rule(
        string process,
        int priority = 0,
        ForegroundAction action = ForegroundAction.Profile,
        int slot = 1)
        => new() { ProcessName = process, Priority = priority, Action = action, Slot = slot };

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    /// <summary>A rule table on disk, in a file of its own, for the daemon to read.</summary>
    private static ForegroundRuleStore RulesOnDisk(ForegroundSettings settings)
    {
        var store = new ForegroundRuleStore(Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json"));
        store.Save(settings);
        return store;
    }

    /// <summary>
    /// A pad the daemon's host can serve, with no input: these tests are about what the rules
    /// ask the engine to do, not about what the pad is holding.
    /// </summary>
    private static EngineHost HostWithPad(IProfileStore store)
    {
        var provider = new FakeGamepadProvider();
        provider.Add("pad-1", "Xbox Wireless Controller");
        var host = new EngineHost(provider, new RecordingOutputSink(), store);
        host.RefreshPresence();
        return host;
    }

    /// <summary>A foreground source the test moves by hand.</summary>
    private sealed class FakeForegroundSource : IForegroundSource
    {
        public FakeForegroundSource(string processName)
        {
            Context = new ForegroundContext(processName, false);
        }

        public ForegroundContext Context { get; set; }

        public ForegroundContext Read() => Context;
    }

    [Fact]
    public void The_Daemon_Watcher_Drives_Its_Own_Engine_Host()
    {
        // In daemon mode this process is the one that owns mapping, so a rule reaches the
        // engine directly: the same target the GUI drives over its own host in local mode.
        var store = new InMemoryProfileStore();
        using var host = HostWithPad(store);
        var rules = RulesOnDisk(new ForegroundSettings
        {
            DefaultSlot = 1,
            Rules = [Rule("chrome", slot: 2), Rule("game", action: ForegroundAction.NativeGamepad)],
        });

        var source = new FakeForegroundSource("chrome");
        using var watcher = DaemonHost.CreateForegroundWatcher(host, store, source, rules);

        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();

        Assert.Equal(2, host.ActivePresetSlot);

        // The game in front takes the pad: the engine stops injecting, the loop keeps running,
        // and the other two pauses are untouched.
        source.Context = new ForegroundContext("game", false);
        watcher.Refresh();

        Assert.True(host.IsNativeGamepadPaused);
        Assert.True(host.IsMappingEnabled);
        Assert.False(host.IsInjectionSuspended);

        // And a program with no rule goes back to the desktop default, unpaused.
        source.Context = new ForegroundContext("explorer", false);
        watcher.Refresh();

        Assert.False(host.IsNativeGamepadPaused);
        Assert.Equal(1, host.ActivePresetSlot);
    }

    [Fact]
    public async Task The_Daemon_Polls_The_Rules_With_No_Window_Connected()
    {
        // The bug this whole change is for: the rules used to run only while a window was open,
        // because the poll lived in the window. Here there is no window, no pipe and no GUI.
        var store = new InMemoryProfileStore();
        using var host = HostWithPad(store);
        var rules = RulesOnDisk(new ForegroundSettings
        {
            Rules = [Rule("game", action: ForegroundAction.NativeGamepad)],
        });

        var source = new FakeForegroundSource("game");
        using var stop = new CancellationTokenSource();
        using var watcher = DaemonHost.CreateForegroundWatcher(host, store, source, rules);
        var poll = DaemonHost.MonitorForegroundAsync(watcher, rules, stop.Token, TimeSpan.FromMilliseconds(10));

        await WaitFor(() => host.IsNativeGamepadPaused);

        // The loop keeps polling: the foreground moving on lifts the pause again.
        source.Context = new ForegroundContext("explorer", false);
        await WaitFor(() => !host.IsNativeGamepadPaused);

        stop.Cancel();
        await poll;
    }

    [Fact]
    public async Task Stopping_The_Daemon_Watcher_Lifts_The_Pause_A_Rule_Left_Behind()
    {
        // The pause belongs to the rule, not to the user: a watcher going away with it still
        // active would leave the resident daemon handing the pad to a program that lost the
        // foreground.
        var store = new InMemoryProfileStore();
        using var host = HostWithPad(store);
        var rules = RulesOnDisk(new ForegroundSettings
        {
            Rules = [Rule("game", action: ForegroundAction.NativeGamepad)],
        });

        using var stop = new CancellationTokenSource();
        using var watcher = DaemonHost.CreateForegroundWatcher(
            host,
            store,
            new FakeForegroundSource("game"),
            rules);
        var poll = DaemonHost.MonitorForegroundAsync(watcher, rules, stop.Token, TimeSpan.FromMilliseconds(10));

        await WaitFor(() => host.IsNativeGamepadPaused);

        stop.Cancel();
        await poll;
        watcher.Dispose();

        Assert.False(host.IsNativeGamepadPaused);
    }

    [Fact]
    public async Task The_Daemon_Picks_Up_A_Rule_Edit_Made_While_It_Runs()
    {
        // The shared table is the channel: a window's 保存 writes it, and the daemon has no
        // control-channel verb for a rule edit.
        var store = new InMemoryProfileStore();
        using var host = HostWithPad(store);
        var rules = RulesOnDisk(new ForegroundSettings { Rules = [Rule("chrome", slot: 2)] });

        using var stop = new CancellationTokenSource();
        using var watcher = DaemonHost.CreateForegroundWatcher(
            host,
            store,
            new FakeForegroundSource("chrome"),
            rules);
        var poll = DaemonHost.MonitorForegroundAsync(watcher, rules, stop.Token, TimeSpan.FromMilliseconds(10));

        await WaitFor(() => host.ActivePresetSlot == 2);

        var edited = new ForegroundSettings { Rules = [Rule("chrome", slot: 3)] };
        File.WriteAllText(rules.FilePath, ForegroundSettingsSerializer.Serialize(edited));

        await WaitFor(() => host.ActivePresetSlot == 3);

        stop.Cancel();
        await poll;
    }

    [Fact]
    public async Task A_Rule_Switch_Reaches_The_Slot_The_Server_Publishes()
    {
        // The watcher drives the engine host directly, while the server keeps the second,
        // published record of the app-wide slot that every connected GUI reads. A rule-driven
        // switch must reach both, or a window would quote a slot the engine had left behind.
        var daemonHost = new FakeDaemonHost();
        daemonHost.AddDevice("pad-1");
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.EnsureDefaults(new GamepadDescriptor("pad-1", "Xbox Wireless Controller"));
        daemonHost.Reload("pad-1", presets.LoadSlot("pad-1", PresetStore.MinSlot)!);
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));

        var published = new List<int>();
        server.StateChanged += message => published.Add(message.Snapshot.ActivePresetSlot);

        using var host = HostWithPad(store);
        var rules = RulesOnDisk(new ForegroundSettings { Rules = [Rule("chrome", slot: 2)] });

        using var stop = new CancellationTokenSource();
        using var foreground = new DaemonHost.DaemonForeground(
            host,
            store,
            server,
            new FakeForegroundSource("chrome"),
            rules,
            stop.Token);

        await WaitFor(() => host.ActivePresetSlot == 2);
        await WaitFor(() => published.Contains(2));

        // The switch reached the daemon server as the same transaction a click on the preset
        // strip sends, so the snapshot every GUI adopts names the slot the engine is running.
        Assert.Contains("pad-1", daemonHost.ReloadedDevices);

        stop.Cancel();
        await foreground.Loop;
    }

    [Fact]
    public void The_Foreground_Note_Names_The_Table_And_The_Cadence()
    {
        var running = DaemonHost.BuildForegroundNote(
            new ForegroundWatcher(
                NullForegroundSource.Instance,
                new NullForegroundTarget(),
                new ForegroundSettings { Rules = [Rule("chrome"), Rule("game")] }));

        Assert.Contains("2 条规则", running);
        Assert.Contains("400 ms", running);

        var off = DaemonHost.BuildForegroundNote(
            new ForegroundWatcher(
                NullForegroundSource.Instance,
                new NullForegroundTarget(),
                new ForegroundSettings { Enabled = false, Rules = [Rule("chrome")] }));

        Assert.Contains("已停用", off);
    }

    [Fact]
    public void The_Foreground_Note_Rejects_No_Watcher()
    {
        Assert.Throws<ArgumentNullException>(() => DaemonHost.BuildForegroundNote(null!));
    }
}
