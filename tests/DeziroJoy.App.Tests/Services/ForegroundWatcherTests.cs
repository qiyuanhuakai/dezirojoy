using DeziroJoy.App.Services;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The foreground watcher end to end, against a fake source and a fake target: what the rule
/// table decides must reach the engine as a profile switch or a pause, a miss must fall back to
/// the desktop default, and a manual lock must override all of it. No window system, no engine
/// and no injection mutex are involved - which is the point: the watcher drives existing APIs
/// and nothing else.
/// </summary>
public sealed class ForegroundWatcherTests
{
    /// <summary>A foreground source the test moves by hand.</summary>
    private sealed class FakeForegroundSource : IForegroundSource
    {
        public ForegroundContext Context { get; set; } = ForegroundContext.Empty;

        public int Reads { get; private set; }

        public ForegroundContext Read()
        {
            Reads++;
            return Context;
        }
    }

    /// <summary>
    /// The target as the test observes it: the slot it was moved to, the pauses it was asked
    /// for, and every call in order. Starts on the desktop default slot with nothing paused,
    /// exactly as a fresh host does.
    /// </summary>
    private sealed class FakeForegroundTarget : IForegroundTarget
    {
        public FakeForegroundTarget(int activeSlot = 1)
        {
            ActiveSlot = activeSlot;
        }

        public int ActiveSlot { get; private set; }

        public bool IsPaused { get; private set; }

        public List<string> Calls { get; } = [];

        public void SwitchProfile(int slot)
        {
            ActiveSlot = slot;
            Calls.Add($"switch:{slot}");
        }

        public void SetPaused(bool paused)
        {
            IsPaused = paused;
            Calls.Add(paused ? "pause" : "resume");
        }
    }

    private static ForegroundRule Rule(
        string process,
        int priority = 0,
        ForegroundAction action = ForegroundAction.Profile,
        int slot = 1)
        => new() { ProcessName = process, Priority = priority, Action = action, Slot = slot };

    /// <summary>
    /// A pad the host can serve, with no input: these tests are about what the watcher asks the
    /// host to do, not about what the pad is holding.
    /// </summary>
    private sealed class IdleProvider : IGamepadProvider
    {
        private readonly Dictionary<string, GamepadDescriptor> _devices = [];

        public void Add(string deviceId, string name) => _devices[deviceId] = new GamepadDescriptor(deviceId, name);

        public IReadOnlyList<GamepadDescriptor> GetConnected() => [.. _devices.Values];

        public IGamepadState? Poll(string deviceId) => GamepadState.Neutral;

        public void Dispose()
        {
        }
    }

    private sealed class IdleSink : IOutputSink
    {
        public List<string> Calls { get; } = [];

        public void KeyDown(GamepadKey key) => Calls.Add($"down:{key}");

        public void KeyUp(GamepadKey key) => Calls.Add($"up:{key}");

        public void SendText(string text) => Calls.Add($"text:{text}");

        public void MouseMove(int dx, int dy) => Calls.Add($"move:{dx},{dy}");

        public void ScrollWheel(int horizontalNotches, int verticalNotches)
            => Calls.Add($"wheel:{horizontalNotches},{verticalNotches}");

        public void MouseButtonDown(MouseButton button) => Calls.Add($"mdown:{button}");

        public void MouseButtonUp(MouseButton button) => Calls.Add($"mup:{button}");

        public void Dispose()
        {
        }
    }

    private static (ForegroundWatcher Watcher, FakeForegroundSource Source, FakeForegroundTarget Target) Create(
        ForegroundSettings? settings = null,
        int activeSlot = 1)
    {
        var source = new FakeForegroundSource();
        var target = new FakeForegroundTarget(activeSlot);
        var watcher = new ForegroundWatcher(source, target, settings);
        return (watcher, source, target);
    }

    [Fact]
    public void A_Rule_Switches_The_Profile_The_Rule_Names()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
            DefaultSlot = 1,
        });

        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();

        Assert.Equal(["switch:3"], target.Calls);
        Assert.Equal(3, target.ActiveSlot);
        Assert.Equal(ForegroundDecisionKind.Profile, watcher.LastDecision!.Kind);
    }

    [Fact]
    public void No_Match_Falls_Back_To_The_Desktop_Default()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
            DefaultSlot = 2,
        });

        source.Context = new ForegroundContext("firefox", false);
        watcher.Refresh();

        Assert.Equal(["switch:2"], target.Calls);
        Assert.Equal(2, target.ActiveSlot);
    }

    [Fact]
    public void The_Desktop_Falls_Back_To_The_Desktop_Default()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
            DefaultSlot = 2,
        });

        watcher.Refresh();

        Assert.Equal(["switch:2"], target.Calls);
        Assert.Null(watcher.ForegroundProcessName);
    }

    [Fact]
    public void Priority_Orders_The_Rules_That_Reach_The_Target()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("chrome", priority: 1, slot: 2), Rule("chrome", priority: 8, slot: 4)],
        });

        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();

        Assert.Equal(["switch:4"], target.Calls);
    }

    [Fact]
    public void A_Native_Gamepad_Rule_Pauses_Mapping_And_Leaves_The_Profile_Alone()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("game", action: ForegroundAction.NativeGamepad)],
            DefaultSlot = 1,
        });

        source.Context = new ForegroundContext("game", false);
        watcher.Refresh();

        Assert.Equal(["pause"], target.Calls);
        Assert.True(target.IsPaused);
        Assert.Equal(1, target.ActiveSlot);
        Assert.Equal(ForegroundDecisionKind.Paused, watcher.LastDecision!.Kind);
    }

    [Fact]
    public void Leaving_A_Native_Gamepad_Program_Resumes_Mapping_And_Restores_The_Default()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("game", action: ForegroundAction.NativeGamepad)],
            DefaultSlot = 2,
        });

        source.Context = new ForegroundContext("game", false);
        watcher.Refresh();
        Assert.True(target.IsPaused);

        source.Context = new ForegroundContext("explorer", false);
        watcher.Refresh();

        Assert.Equal(["pause", "resume", "switch:2"], target.Calls);
        Assert.False(target.IsPaused);
        Assert.Equal(2, target.ActiveSlot);
    }

    [Fact]
    public void A_Poll_That_Changes_Nothing_Calls_The_Target_With_Nothing()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
        });

        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();
        Assert.Equal(["switch:3"], target.Calls);

        watcher.Refresh();
        watcher.Refresh();

        // No second reload: the engine keeps its press edges and whatever the pad was holding.
        Assert.Equal(["switch:3"], target.Calls);
        Assert.Equal(3, source.Reads);
    }

    [Fact]
    public void A_Manual_Preset_Switch_Is_Brought_Back_In_Line_On_The_Next_Poll()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
        });

        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();
        Assert.Equal(3, target.ActiveSlot);

        // The user picks another preset while the rule is still in charge.
        target.SwitchProfile(5);
        watcher.Refresh();

        Assert.Equal(3, target.ActiveSlot);
        Assert.Equal(["switch:3", "switch:5", "switch:3"], target.Calls);
    }

    [Fact]
    public void Locking_Overrides_The_Rules_And_Keeps_What_Is_Running()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
            DefaultSlot = 2,
        });

        source.Context = new ForegroundContext("explorer", false);
        watcher.Refresh();
        Assert.Equal(2, target.ActiveSlot);

        watcher.SetLocked(true);
        Assert.True(watcher.IsLocked);

        // The foreground moves to a program with a rule: the lock keeps slot 2 running.
        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();

        Assert.Equal(["switch:2"], target.Calls);
        Assert.Equal(2, target.ActiveSlot);
        Assert.Equal(ForegroundDecisionKind.Profile, watcher.LastDecision!.Kind);
    }

    [Fact]
    public void Locking_During_A_Pause_Keeps_The_Pause_And_Unlocking_Releases_It()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("game", action: ForegroundAction.NativeGamepad)],
        });

        source.Context = new ForegroundContext("game", false);
        watcher.Refresh();
        Assert.True(target.IsPaused);

        watcher.SetLocked(true);
        source.Context = new ForegroundContext("explorer", false);
        watcher.Refresh();

        // Locked means the rules stand down: the pause stays until it is unlocked.
        Assert.True(target.IsPaused);
        Assert.Equal(["pause"], target.Calls);

        watcher.SetLocked(false);

        Assert.False(target.IsPaused);
        Assert.Equal(["pause", "resume"], target.Calls);
    }

    [Fact]
    public void Turning_The_Table_Off_Leaves_The_Desktop_Default_In_Charge()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
            DefaultSlot = 2,
            Enabled = false,
        });

        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();

        Assert.Equal(["switch:2"], target.Calls);
        Assert.Equal(ForegroundDecisionKind.Default, watcher.LastDecision!.Kind);
    }

    [Fact]
    public void An_Edit_Takes_Effect_Without_Waiting_For_The_Next_Poll()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
        });

        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();
        Assert.Equal(3, target.ActiveSlot);

        watcher.Update(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 4)],
        });

        Assert.Equal(4, target.ActiveSlot);
        Assert.Equal(4, watcher.Settings.Rules.Single().Slot);
    }

    [Fact]
    public void Every_Poll_Explains_Why_The_Active_Profile_Is_The_Active_One()
    {
        var (watcher, source, _) = Create(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3), Rule("game", action: ForegroundAction.NativeGamepad)],
            DefaultSlot = 2,
        });

        source.Context = new ForegroundContext("Chrome.exe", false);
        watcher.Refresh();
        Assert.Contains("预设 3", watcher.LastDecision!.Reason);

        source.Context = new ForegroundContext("game", false);
        watcher.Refresh();
        Assert.Contains("原生手柄", watcher.LastDecision!.Reason);

        source.Context = new ForegroundContext("explorer", false);
        watcher.Refresh();
        Assert.Contains("预设 2", watcher.LastDecision!.Reason);

        source.Context = ForegroundContext.Empty;
        watcher.Refresh();
        Assert.Contains("桌面", watcher.LastDecision!.Reason);
    }

    [Fact]
    public void State_Changed_Fires_On_Every_Poll()
    {
        var (watcher, source, _) = Create(new ForegroundSettings { Rules = [Rule("chrome", slot: 3)] });

        var raised = 0;
        watcher.StateChanged += () => raised++;

        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();
        watcher.Refresh();

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Disposing_During_A_Pause_Resumes_Mapping()
    {
        // The pause belongs to the foreground rule, not to the user: a watcher going away with
        // it still active would leave mapping off in a program that no longer has the foreground.
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("game", action: ForegroundAction.NativeGamepad)],
        });

        source.Context = new ForegroundContext("game", false);
        watcher.Refresh();
        Assert.True(target.IsPaused);

        watcher.Dispose();

        Assert.False(target.IsPaused);
    }

    [Fact]
    public void Disposing_Leaves_A_Profile_Switch_Alone()
    {
        var (watcher, source, target) = Create(new ForegroundSettings { Rules = [Rule("chrome", slot: 3)] });

        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();

        watcher.Dispose();

        Assert.Equal(3, target.ActiveSlot);
        Assert.Equal(["switch:3"], target.Calls);
    }

    [Fact]
    public void A_Rule_Drives_A_Real_Host_From_Profile_Switch_To_Native_Gamepad_Pause()
    {
        // The whole feature against the real engine: a rule switches the host's profile, a
        // native-gamepad rule pauses its injection, and a miss puts the desktop default back.
        var store = new InMemoryProfileStore();
        var provider = new IdleProvider();
        var sink = new IdleSink();
        using var host = new EngineHost(provider, sink, store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        host.RefreshPresence();

        var source = new FakeForegroundSource();
        var watcher = new ForegroundWatcher(
            source,
            new EngineHostForegroundTarget(host, new PresetStore(store)),
            new ForegroundSettings
            {
                DefaultSlot = 1,
                Rules =
                [
                    Rule("chrome", priority: 2, slot: 2),
                    Rule("game", priority: 5, action: ForegroundAction.NativeGamepad),
                ],
            });

        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();

        Assert.Equal(2, host.ActivePresetSlot);
        Assert.Contains("预设 2", host.Controllers.Single().Profile.Name, StringComparison.Ordinal);
        Assert.False(host.IsNativeGamepadPaused);

        // The game in front takes the pad: the host stops injecting and releases whatever it held.
        source.Context = new ForegroundContext("game", false);
        watcher.Refresh();

        Assert.True(host.IsNativeGamepadPaused);
        // The three pauses are independent: the native-gamepad one never touches the user's own.
        Assert.True(host.IsMappingEnabled);
        Assert.False(host.IsInjectionSuspended);

        // And a program with no rule goes back to the desktop default, unpaused.
        source.Context = new ForegroundContext("explorer", false);
        watcher.Refresh();

        Assert.False(host.IsNativeGamepadPaused);
        Assert.Equal(1, host.ActivePresetSlot);
    }
}
