using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// Physical-gamepad navigation is two-level. A press on the pad SELECTS that element in the
/// model and the source list, and nothing else: it must not open the editor and must not
/// suspend injection, because browsing the pad is not a request to configure. Entering the
/// editor - a click, or Enter - is the second level, and that is the only thing that hijacks
/// the pad: while a mapping is open, presses switch which element is being configured instead
/// of being injected.
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
    public void Pressing_A_Button_Selects_Its_Element_Without_Opening_The_Editor()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        Assert.Equal(new ButtonBinding(GamepadButton.B), vm.SelectedSource);
        Assert.False(vm.IsEditing, "selecting must not open the editor - that is the second level");
        Assert.False(vm.Host.IsInjectionSuspended);
    }

    [Fact]
    public void Pressing_A_Button_Keeps_Injecting_While_The_Engine_Runs()
    {
        var (vm, host, provider, sink) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        // B carries a default mapping (B -> Enter). Pressing it must select the element AND
        // still inject: browsing the pad is not a request to configure, so nothing may be
        // hijacked at the first level.
        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        Assert.Equal(new ButtonBinding(GamepadButton.B), vm.SelectedSource);
        Assert.Contains("down:Enter", sink.Calls);
    }

    [Fact]
    public void Pressing_A_Button_Selects_Its_Mapping_Row_Without_Loading_It()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        var selected = Assert.IsType<MappingRowViewModel>(vm.SelectedMapping);
        Assert.Equal(new ButtonBinding(GamepadButton.B), selected.Entry.Source);
        Assert.False(vm.IsEditing, "the mapping must load only when the editor is entered");
    }

    [Fact]
    public void Pressing_An_Unmapped_Button_Selects_It_Without_Arming_A_Mapping()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        // LeftBumper has no default mapping, so there is no row to select - but the element
        // is still selected in the model so the user sees what they touched.
        SetState(provider, GamepadButtonMask.LeftBumper);
        Sample(vm, host);

        Assert.Equal(new ButtonBinding(GamepadButton.LeftBumper), vm.SelectedSource);
        Assert.Null(vm.SelectedMapping);
        Assert.False(vm.IsEditing);
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
    public void Releasing_And_Pressing_Again_Selects_Again()
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
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public void Holding_A_Button_Does_Not_Re_Select()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Assert.Equal(new ButtonBinding(GamepadButton.B), vm.SelectedSource);

        // A held button is not a new press, so the selection must not change underneath the
        // user while they are looking at that element.
        SetState(provider, GamepadButtonMask.X);
        Sample(vm, host);

        Assert.Equal(new ButtonBinding(GamepadButton.X), vm.SelectedSource);
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public void Clicking_The_Selected_Element_Opens_The_Editor_And_Suspends_Injection()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Assert.False(vm.IsEditing, "the press only selects");

        Click(vm, new ButtonBinding(GamepadButton.B));

        Assert.True(vm.IsEditing);
        Assert.True(vm.Host.IsInjectionSuspended, "entering the editor is what hijacks the pad");
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
    public void Closing_The_Editor_Resumes_Injection()
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

        Assert.Contains("down:Enter", sink.Calls);
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
    public void Pressing_A_Button_With_The_Engine_Stopped_Selects_Without_Opening_The_Editor()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        PresenceSample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        PresenceSample(vm, host);

        Assert.False(host.IsRunning);
        Assert.Equal(new ButtonBinding(GamepadButton.B), vm.SelectedSource);
        Assert.False(vm.IsEditing);
        Assert.False(vm.Host.IsInjectionSuspended);
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
        Assert.Equal(0.5, vm.Visualizer.LeftStickX);
        Assert.Equal(-0.5, vm.Visualizer.LeftStickY);
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
        Assert.False(vm.IsEditing);
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
