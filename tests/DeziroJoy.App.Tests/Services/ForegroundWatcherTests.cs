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
    private sealed class FakeForegroundSource : IForegroundSource, IDisposable
    {
        public ForegroundContext Context { get; set; } = ForegroundContext.Empty;

        public IReadOnlyList<ForegroundWindow> Windows { get; set; } = [];

        public int Reads { get; private set; }

        public bool Disposed { get; private set; }

        public ForegroundContext Read()
        {
            Reads++;
            return Context;
        }

        public IReadOnlyList<ForegroundWindow> ListWindows() => Windows;

        public void Dispose() => Disposed = true;
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
    public void A_Manual_Preset_Switch_Is_Respected_While_The_Decision_Is_Unchanged()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
        });

        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();
        Assert.Equal(3, target.ActiveSlot);

        // The user picks another preset while the rule is still in charge. The decision
        // has not changed - same kind, same slot, same rule - so there is nothing new to
        // enforce, and the pick survives every poll that follows.
        target.SwitchProfile(5);
        watcher.Refresh();
        watcher.Refresh();

        Assert.Equal(5, target.ActiveSlot);
        Assert.Equal(["switch:3", "switch:5"], target.Calls);
    }

    [Fact]
    public void A_Changed_Foreground_Still_Applies_Over_A_Manual_Switch()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3), Rule("firefox", slot: 4)],
            DefaultSlot = 1,
        });

        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();
        Assert.Equal(3, target.ActiveSlot);

        // The manual switch survives an unchanged decision...
        target.SwitchProfile(5);
        watcher.Refresh();
        Assert.Equal(5, target.ActiveSlot);

        // ...but a genuine change - another program in front, another rule deciding -
        // still lands on the very next poll.
        source.Context = new ForegroundContext("firefox", false);
        watcher.Refresh();

        Assert.Equal(4, target.ActiveSlot);
        Assert.Equal(["switch:3", "switch:5", "switch:4"], target.Calls);
    }

    [Fact]
    public void An_Edited_Rule_Applies_Its_New_Outcome_Over_A_Manual_Switch()
    {
        // An edited table is a deliberate change, so its outcome lands even where a
        // manual switch would otherwise survive: the rule that decides is a different
        // one now, which is a different effective outcome.
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
        });

        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();
        Assert.Equal(3, target.ActiveSlot);

        target.SwitchProfile(5);
        watcher.Update(new ForegroundSettings { Rules = [Rule("chrome", slot: 3, priority: 2)] });

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
    public void Turning_The_Table_Off_Applies_Nothing_And_Leaves_The_Profile_Alone()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
            DefaultSlot = 2,
        });

        // Enabled first, so the watcher has an applied decision to stand down from.
        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();
        Assert.Equal(3, target.ActiveSlot);

        // The user picks another preset and then turns the table off: neither the pick
        // nor the profile is touched - disabled rules apply nothing at all, so the
        // desktop default is never forced onto a user who switched the feature off.
        target.SwitchProfile(5);
        watcher.Update(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
            DefaultSlot = 2,
            Enabled = false,
        });
        watcher.Refresh();

        Assert.Equal(["switch:3", "switch:5"], target.Calls);
        Assert.Equal(5, target.ActiveSlot);
        Assert.Equal(ForegroundDecisionKind.Default, watcher.LastDecision!.Kind);
        Assert.Contains("停用", watcher.LastDecision!.Reason);
    }

    [Fact]
    public void A_Table_That_Starts_Disabled_Applies_Nothing()
    {
        // The state a user returns to by turning the feature off: the very first poll
        // applies nothing, so the profile the host already carries is left in charge.
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
            DefaultSlot = 2,
            Enabled = false,
        });

        source.Context = new ForegroundContext("chrome", false);
        watcher.Refresh();
        watcher.Refresh();

        Assert.Empty(target.Calls);
        Assert.Equal(1, target.ActiveSlot);
        Assert.Equal(ForegroundDecisionKind.Default, watcher.LastDecision!.Kind);
    }

    [Fact]
    public void Turning_The_Table_Off_Lifts_The_Pause_A_Rule_Left_Behind()
    {
        var (watcher, source, target) = Create(new ForegroundSettings
        {
            Rules = [Rule("game", action: ForegroundAction.NativeGamepad)],
        });

        source.Context = new ForegroundContext("game", false);
        watcher.Refresh();
        Assert.True(target.IsPaused);

        // The pause belongs to the rule, not to the user: with the table off there is no
        // rule left to re-apply it, so it is lifted and mapping runs again.
        watcher.Update(new ForegroundSettings
        {
            Rules = [Rule("game", action: ForegroundAction.NativeGamepad)],
            Enabled = false,
        });

        Assert.False(target.IsPaused);
        Assert.Equal(["pause", "resume"], target.Calls);

        // And enabling the table again puts it straight back: the game is still in front.
        watcher.Update(new ForegroundSettings
        {
            Rules = [Rule("game", action: ForegroundAction.NativeGamepad)],
        });

        Assert.True(target.IsPaused);
        Assert.Equal(["pause", "resume", "pause"], target.Calls);
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
    public void A_Source_That_Cannot_Enumerate_Windows_Lists_None()
    {
        // The fake reads the foreground and nothing more: the window list is the platform's
        // second answer, and a source that has none reports an empty list rather than throwing
        // or inventing one - the picker then has nothing to offer and the user types the name.
        var (watcher, _, _) = Create();

        Assert.Empty(watcher.ListWindows());
    }

    [Fact]
    public void The_Null_Source_Reports_No_Foreground_Program_And_No_Windows()
    {
        // The stand-in for a platform with no foreground query at all - Wayland, a box with no X
        // server. Both answers are empty: no rule matches, the desktop default runs, and the
        // picker has nothing to offer.
        var watcher = new ForegroundWatcher(NullForegroundSource.Instance, new FakeForegroundTarget());

        watcher.Refresh();

        Assert.Null(watcher.ForegroundProcessName);
        Assert.Null(watcher.LastDecision!.Rule);
        Assert.Equal(ForegroundDecisionKind.Default, watcher.LastDecision!.Kind);
        Assert.Empty(watcher.ListWindows());
        Assert.Contains("不支持", watcher.SourceNote);
    }

    [Fact]
    public void Disposing_The_Watcher_Releases_A_Source_That_Holds_A_Connection()
    {
        // The source is the watcher's for its lifetime and nothing else reads it, so a source
        // that holds one - the X11 one holds a connection - is closed with the watcher rather
        // than left to the process exit.
        var source = new FakeForegroundSource();
        using var watcher = new ForegroundWatcher(source, new FakeForegroundTarget());

        watcher.Dispose();

        Assert.True(source.Disposed);
    }

    [Fact]
    public void The_Window_List_Comes_From_The_Source_Unchanged()
    {
        // The watcher adds no second copy of the platform's answer: the panel shows what the
        // source enumerated, in the order it enumerated it, so a fake can pin the whole path.
        var source = new FakeForegroundSource
        {
            Windows = [new ForegroundWindow("chrome", "新标签页"), new ForegroundWindow("game", null)],
        };
        var watcher = new ForegroundWatcher(source, new FakeForegroundTarget());

        var windows = watcher.ListWindows();

        Assert.Equal(2, windows.Count);
        Assert.Equal("chrome", windows[0].ProcessName);
        Assert.Equal("新标签页", windows[0].Title);
        Assert.Equal("game", windows[1].ProcessName);
        Assert.Null(windows[1].Title);
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
