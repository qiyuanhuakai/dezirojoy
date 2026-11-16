using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// Physical-gamepad navigation under the temporary hijack. A press on the pad jumps straight
/// to that element's mapping configuration - select and open in one step - which is what the
/// hijack is for: configuring by touching the pad. A stick deflection stays a level-1
/// selection, because analog movement is not a button press. The explicit hijack preference,
/// not editor visibility, owns injection: enabled and in scope means the UI consumes presses
/// while mapping injection is suspended; disabled means every press follows the active
/// mapping even with an editor open.
/// </summary>
public sealed class GamepadNavigationTests
{
    private const string DeviceId = "xinput:0";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 64.0);

    private static (MainWindowViewModel Vm, EngineHost Host, FakeGamepadProvider Provider, RecordingOutputSink Sink) CreateHost(
        ThemeService? settings = null)
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var vm = new MainWindowViewModel(host, store, settings: settings);

        // These tests exercise gamepad navigation, which the temporary hijack switch gates. It
        // ships off by default, so arm it here; the tests that check the off path set it back.
        vm.SetGamepadNavigationEnabled(true);

        return (vm, host, provider, sink);
    }

    /// <summary>One engine tick fed to the view model, exactly like the view does.</summary>
    private static void Sample(MainWindowViewModel vm, EngineHost host)
    {
        ControllerSampledEventArgs? sampled = null;
        host.Sampled += (_, args) => sampled = args;

        host.TickOnce(Tick);

        if (sampled is not null)
        {
            vm.ApplySample(sampled);
        }
    }

    /// <summary>
    /// One presence sample fed to the view model, exactly like the view does while the mapping
    /// loop is stopped: the host polls without ticking or injecting and raises
    /// <see cref="EngineHost.PresenceSampled"/>.
    /// </summary>
    private static void PresenceSample(MainWindowViewModel vm, EngineHost host)
    {
        ControllerSampledEventArgs? sampled = null;
        host.PresenceSampled += (_, args) => sampled = args;

        host.SamplePresence();

        if (sampled is not null)
        {
            vm.ApplyPresenceSample(sampled);
        }
    }

    private static void Connect(FakeGamepadProvider provider, GamepadButtonMask buttons = default)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(buttons, 0f, 0f, 0f, 0f));
    }

    private static void SetState(
        FakeGamepadProvider provider,
        GamepadButtonMask buttons = default,
        float leftX = 0f,
        float leftY = 0f,
        float rightX = 0f,
        float rightY = 0f)
        => provider.SetState(DeviceId, GamepadState.Create(buttons, leftX, leftY, rightX, rightY));

    /// <summary>Enters the second level for one source, exactly as clicking it on the model does.</summary>
    private static void Click(MainWindowViewModel vm, InputBinding source) => vm.OpenEditorForSource(source);

    [Fact]
    public void Pressing_A_Button_Jumps_To_Its_Mapping_Configuration()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        // The jump: select and open in one step, with the existing entry loaded.
        Assert.Equal(new ButtonBinding(GamepadButton.B), vm.SelectedSource);
        Assert.True(vm.IsEditing, "a press with hijack on opens the element's configuration");
        Assert.Equal(GamepadButton.B, vm.Editor.SelectedButton);
        Assert.True(vm.Host.IsInjectionSuspended, "the explicit hijack setting owns the pad in scope");
    }

    [Fact]
    public void Pressing_A_Button_With_Hijack_Enabled_Pauses_Injection()
    {
        var (vm, host, provider, sink) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        // B carries a default mapping (B -> Enter). With hijack enabled it opens the
        // element's configuration for the UI and the injector stays suspended.
        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        Assert.Equal(new ButtonBinding(GamepadButton.B), vm.SelectedSource);
        Assert.DoesNotContain("down:Enter", sink.Calls);
    }

    [Fact]
    public void Pressing_A_Mapped_Button_Loads_Its_Existing_Entry()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        var selected = Assert.IsType<MappingRowViewModel>(vm.SelectedMapping);
        Assert.Equal(new ButtonBinding(GamepadButton.B), selected.Entry.Source);
        Assert.True(vm.IsEditing, "an element that already has a mapping opens that entry");
        Assert.Equal(GamepadKey.Enter, vm.Editor.SelectedKey);
    }

    [Fact]
    public void Pressing_An_Unmapped_Button_Opens_A_Fresh_Draft()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        // LeftBumper has no default mapping, so the jump arms a new draft for it rather than
        // loading anything: the element is selected and the editor is open on it.
        SetState(provider, GamepadButtonMask.LeftBumper);
        Sample(vm, host);

        Assert.Equal(new ButtonBinding(GamepadButton.LeftBumper), vm.SelectedSource);
        Assert.Null(vm.SelectedMapping);
        Assert.True(vm.IsEditing);
        Assert.Equal(GamepadButton.LeftBumper, vm.Editor.SelectedButton);
        Assert.False(vm.Editor.IsCapturingButton, "a jump must not arm a capture");
        Assert.Equal(10, vm.ActiveProfile!.Mappings.Count);
    }

    [Fact]
    public void Deflecting_A_Stick_Selects_Its_Mapping_Row()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, default, 0f, 0f, 0.9f, 0f);
        Sample(vm, host);

        Assert.Equal(new StickBinding(GamepadStick.Right), vm.SelectedSource);
        Assert.False(vm.IsEditing);

        var selected = Assert.IsType<MappingRowViewModel>(vm.SelectedMapping);
        Assert.Equal(new StickBinding(GamepadStick.Right), selected.Entry.Source);
    }

    [Fact]
    public void A_Button_Held_From_Connect_Does_Not_Select()
    {
        var (vm, host, provider, _) = CreateHost();

        // The pad is already holding B when the first sample arrives: that is the baseline,
        // not a press, so nothing must be selected.
        Connect(provider, GamepadButtonMask.B);
        Sample(vm, host);

        Assert.Null(vm.SelectedSource);
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public void Releasing_And_Pressing_Again_Jumps_Again()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Assert.Null(vm.SelectedSource);

        SetState(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        Assert.Equal(new ButtonBinding(GamepadButton.B), vm.SelectedSource);
        Assert.True(vm.IsEditing);
    }

    [Fact]
    public void Holding_A_Button_Does_Not_Jump_Again()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Assert.Equal(new ButtonBinding(GamepadButton.B), vm.SelectedSource);
        Assert.True(vm.IsEditing);

        // A held button is not a new press, so the editor must not reload underneath the
        // user while they are looking at that element - a reload would drop unsaved edits.
        vm.Editor.SelectedKey = GamepadKey.Space;
        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        Assert.Equal(GamepadKey.Space, vm.Editor.SelectedKey);
    }

    [Fact]
    public void Clicking_The_Selected_Element_Still_Opens_The_Editor_And_Suspends_Injection()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        // The jump already opened B; clicking the same element on the model is the mouse
        // route to the same place and reloads the entry rather than stacking a second one.
        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Assert.True(vm.IsEditing);

        Click(vm, new ButtonBinding(GamepadButton.B));

        Assert.True(vm.IsEditing);
        Assert.True(vm.Host.IsInjectionSuspended, "the explicit hijack setting already owns the pad");
        Assert.Equal(GamepadButton.B, vm.Editor.SelectedButton);
    }

    [Fact]
    public void Pressing_Another_Button_While_Editing_Switches_The_Editor()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Click(vm, new ButtonBinding(GamepadButton.B));
        Assert.Equal(GamepadButton.B, vm.Editor.SelectedButton);

        SetState(provider, GamepadButtonMask.B | GamepadButtonMask.X);
        Sample(vm, host);

        Assert.True(vm.IsEditing);
        Assert.Equal(GamepadButton.X, vm.Editor.SelectedButton);
    }

    [Fact]
    public void Pressing_The_Button_Being_Edited_Again_Keeps_Unsaved_Edits()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Click(vm, new ButtonBinding(GamepadButton.B));
        vm.Editor.SelectedKey = GamepadKey.Space;

        // Release and press the same button again: it is a fresh press, but the editor is
        // already open for it, so reloading would only discard the unsaved target.
        SetState(provider);
        Sample(vm, host);
        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        Assert.Equal(GamepadKey.Space, vm.Editor.SelectedKey);
    }

    [Fact]
    public void Button_Capture_Wins_Over_Navigation()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.A);
        Sample(vm, host);
        var row = Assert.IsType<MappingRowViewModel>(vm.SelectedMapping);

        Click(vm, new ButtonBinding(GamepadButton.A));

        // Capture is armed: the next press is the mapping's new source, not a navigation.
        vm.Editor.IsCapturingButton = true;
        SetState(provider, GamepadButtonMask.X);
        Sample(vm, host);

        Assert.Equal(GamepadButton.X, vm.Editor.SelectedButton);
        Assert.False(vm.Editor.IsCapturingButton, "one button is enough, capture must stand down");
        Assert.Same(row, vm.SelectedMapping);
    }

    [Fact]
    public void Keyboard_Capture_Swallows_Gamepad_Presses()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.A);
        Sample(vm, host);
        var row = Assert.IsType<MappingRowViewModel>(vm.SelectedMapping);

        Click(vm, new ButtonBinding(GamepadButton.A));

        // Keyboard capture owns the input while it is armed: a gamepad button is not a
        // keyboard key, and navigating away would abort the capture the user just started.
        vm.Editor.IsCapturingKey = true;
        SetState(provider, GamepadButtonMask.X);
        Sample(vm, host);

        Assert.Equal(GamepadButton.A, vm.Editor.SelectedButton);
        Assert.True(vm.Editor.IsCapturingKey);
        Assert.Same(row, vm.SelectedMapping);
    }

    [Fact]
    public void Editing_Suspends_Injection_So_The_Press_Only_Navigates()
    {
        var (vm, host, provider, sink) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Click(vm, new ButtonBinding(GamepadButton.B));

        sink.Calls.Clear();
        SetState(provider, GamepadButtonMask.B | GamepadButtonMask.X);
        Sample(vm, host);

        Assert.Equal(GamepadButton.X, vm.Editor.SelectedButton);
        Assert.DoesNotContain(sink.Calls, call => call.StartsWith("down:", StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Calls, call => call.StartsWith("up:", StringComparison.Ordinal));
    }

    [Fact]
    public void Closing_The_Editor_Does_Not_Release_The_Explicit_Hijack_Lease()
    {
        var (vm, host, provider, sink) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Click(vm, new ButtonBinding(GamepadButton.B));
        Assert.True(vm.IsEditing);

        vm.CancelEditorCommand.Execute(null);
        Assert.False(vm.IsEditing);

        sink.Calls.Clear();
        host.TickOnce(Tick);

        Assert.True(vm.Host.IsInjectionSuspended);
        Assert.DoesNotContain("down:Enter", sink.Calls);
    }

    [Fact]
    public void Disabling_Hijack_While_Editing_Immediately_Restores_Mapping()
    {
        var (vm, host, provider, sink) = CreateHost();
        Connect(provider);
        Sample(vm, host);
        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Click(vm, new ButtonBinding(GamepadButton.B));
        sink.Calls.Clear();

        vm.SetGamepadNavigationEnabled(false);
        host.TickOnce(Tick);

        Assert.True(vm.IsEditing);
        Assert.False(vm.Host.IsInjectionSuspended);
        Assert.Contains("down:Enter", sink.Calls);
        Assert.Equal(GamepadButton.B, vm.Editor.SelectedButton);
    }

    [Fact]
    public void Navigation_Does_Not_Start_The_Engine()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        Assert.False(host.IsRunning, "navigation must sample, never start the mapping loop");
    }

    [Fact]
    public void Pressing_A_Button_With_The_Engine_Stopped_Jumps_To_Its_Configuration()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        PresenceSample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        PresenceSample(vm, host);

        Assert.False(host.IsRunning);
        Assert.Equal(new ButtonBinding(GamepadButton.B), vm.SelectedSource);
        Assert.True(vm.IsEditing);
        Assert.Equal(GamepadButton.B, vm.Editor.SelectedButton);
        Assert.True(vm.Host.IsInjectionSuspended);
    }

    [Fact]
    public void Presence_Sampling_Keeps_The_Visualizer_Live_While_Stopped()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        PresenceSample(vm, host);

        SetState(provider, GamepadButtonMask.A | GamepadButtonMask.DPadUp, 0.5f, -0.5f);
        PresenceSample(vm, host);

        Assert.True(vm.Visualizer.IsConnected);
        Assert.True(vm.Visualizer.IsAPressed);
        Assert.True(vm.Visualizer.IsDPadUpPressed);
        // The preview caps share the mapping path's radial deadzone, so this 0.7071 push
        // reports the ramped 0.4635: the visualizer is still live while stopped.
        Assert.Equal(0.4634517, vm.Visualizer.LeftStickX, 6);
        Assert.Equal(-0.4634517, vm.Visualizer.LeftStickY, 6);
    }

    [Fact]
    public void Deflecting_A_Stick_Selects_With_The_Engine_Stopped()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        PresenceSample(vm, host);

        SetState(provider, default, 0f, 0f, 0.9f, 0f);
        PresenceSample(vm, host);

        Assert.Equal(new StickBinding(GamepadStick.Right), vm.SelectedSource);
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public void Presence_Navigation_Never_Ticks_Or_Injects()
    {
        var (vm, host, provider, sink) = CreateHost();
        Connect(provider);
        PresenceSample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        PresenceSample(vm, host);

        Assert.Equal(new ButtonBinding(GamepadButton.B), vm.SelectedSource);
        Assert.True(vm.IsEditing);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void Out_Of_Scope_A_Press_Neither_Selects_Nor_Captures()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        vm.SetPhysicalInputScope(false);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        Assert.Null(vm.SelectedSource);
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public void Gamepad_Navigation_Off_A_Press_Neither_Selects_Nor_Opens_The_Editor()
    {
        var settings = new ThemeService(Path.Combine(Path.GetTempPath(), "dezirojoy-nav-off", Guid.NewGuid().ToString("N")));
        var (vm, host, provider, sink) = CreateHost(settings);
        vm.SetGamepadNavigationEnabled(false);
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        Assert.Null(vm.SelectedSource);
        Assert.False(vm.IsEditing);

        // The switch is about the pad driving the UI, not about the mapping: the profile's
        // own injection must keep firing.
        Assert.NotEmpty(sink.Calls);
    }

    [Fact]
    public void Gamepad_Navigation_Off_Still_Highlights_The_Pressed_Element()
    {
        var settings = new ThemeService(Path.Combine(Path.GetTempPath(), "dezirojoy-nav-highlight", Guid.NewGuid().ToString("N")));
        var (vm, host, provider, _) = CreateHost(settings);
        vm.SetGamepadNavigationEnabled(false);
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        Assert.True(vm.Visualizer.IsBPressed);
        Assert.True(vm.Visualizer.IsConnected);
    }

    [Fact]
    public void Reentry_Resets_The_Held_Edges()
    {
        var (vm, host, provider, _) = CreateHost();

        // The pad is already holding B when the first sample arrives: that is the baseline.
        Connect(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Assert.Null(vm.SelectedSource);

        vm.SetPhysicalInputScope(false);
        vm.SetPhysicalInputScope(true);

        // B is still held across the boundary: reentry makes it the new baseline, so it is
        // not read as a press.
        Sample(vm, host);
        Assert.Null(vm.SelectedSource);

        SetState(provider);
        Sample(vm, host);
        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        Assert.Equal(new ButtonBinding(GamepadButton.B), vm.SelectedSource);
    }

    [Fact]
    public void Leaving_Scope_Stops_Capture_But_Keeps_The_Draft()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Click(vm, new ButtonBinding(GamepadButton.B));
        vm.Editor.SelectedKey = GamepadKey.Space;
        vm.Editor.IsCapturingKey = true;

        vm.SetPhysicalInputScope(false);

        Assert.False(vm.Editor.IsCapturingKey, "leaving scope must stop the armed capture");
        Assert.True(vm.IsEditing, "leaving scope must keep the uncommitted draft");
        Assert.Equal(GamepadKey.Space, vm.Editor.SelectedKey);
    }

    [Fact]
    public void Leaving_Scope_Releases_The_Editor_Lease_And_Reentry_Restores_It()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Click(vm, new ButtonBinding(GamepadButton.B));
        Assert.True(vm.Host.IsInjectionSuspended, "the open editor holds the pad");

        vm.SetPhysicalInputScope(false);
        Assert.False(vm.Host.IsInjectionSuspended, "leaving scope releases this connection's lease");

        vm.SetPhysicalInputScope(true);
        Assert.True(vm.Host.IsInjectionSuspended, "reentry restores the lease for the retained draft");
    }

    [Fact]
    public void Closing_The_Editor_While_Out_Of_Scope_Leaves_The_Lease_Released()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Click(vm, new ButtonBinding(GamepadButton.B));

        vm.SetPhysicalInputScope(false);
        vm.CancelEditorCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.False(vm.Host.IsInjectionSuspended, "a discarded draft must not re-arm the lease");
    }

    [Fact]
    public void Out_Of_Scope_Highlighting_Still_Tracks_The_Pad()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        vm.SetPhysicalInputScope(false);

        // Only navigation and capture are scoped: the visualizer keeps reflecting the pad so
        // the model stays live the moment the user comes back.
        SetState(provider, GamepadButtonMask.A | GamepadButtonMask.DPadUp, 0.5f, -0.5f);
        Sample(vm, host);

        Assert.True(vm.Visualizer.IsAPressed);
        Assert.True(vm.Visualizer.IsDPadUpPressed);
        Assert.Null(vm.SelectedSource);
    }

    [Fact]
    public void A_Button_Held_While_Out_Of_Scope_Is_Not_Captured_On_Reentry()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        // The pad holds B while the window is out of scope - the ordinary case for a GUI left
        // open behind a game.
        SetState(provider, GamepadButtonMask.B);
        vm.SetPhysicalInputScope(false);
        Sample(vm, host);

        vm.SetPhysicalInputScope(true);
        Sample(vm, host);

        Click(vm, new ButtonBinding(GamepadButton.B));
        vm.Editor.IsCapturingButton = true;
        Sample(vm, host);

        Assert.True(vm.Editor.IsCapturingButton, "a button held before capture was armed must be ignored");
    }
}
