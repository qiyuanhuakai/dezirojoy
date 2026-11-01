using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// Physical-gamepad navigation: pressing a button - or deflecting a stick - on the connected
/// pad opens that element's mapping settings, exactly as clicking it on the model does. While
/// a mapping is open the press navigates the editor instead of being injected, so the engine
/// is suspended for the duration of the edit.
/// </summary>
public sealed class GamepadNavigationTests
{
    private const string DeviceId = "xinput:0";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 64.0);

    private static (MainWindowViewModel Vm, EngineHost Host, FakeGamepadProvider Provider, RecordingOutputSink Sink) CreateHost()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var vm = new MainWindowViewModel(host, store);

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

    [Fact]
    public void Pressing_A_Button_While_The_Engine_Runs_Opens_Its_Mapping_Settings()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        Assert.True(vm.IsEditing, "a physical press must open the mapping settings");
        Assert.Equal(GamepadButton.B, vm.Editor.SelectedButton);
    }

    [Fact]
    public void Pressing_A_Button_Selects_Its_Mapping_Row_And_Loads_Its_Mapping()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        // B carries a default mapping (B -> Enter), so the press must select that row and
        // load the mapping rather than arming a fresh one that would duplicate the source.
        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        var selected = Assert.IsType<MappingRowViewModel>(vm.SelectedMapping);
        Assert.Equal(new ButtonBinding(GamepadButton.B), selected.Entry.Source);
        Assert.Equal(MappingTargetKind.KeyPress, vm.Editor.TargetKind);
        Assert.Equal(GamepadKey.Enter, vm.Editor.SelectedKey);
    }

    [Fact]
    public void Pressing_An_Unmapped_Button_Arms_A_Fresh_Mapping()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        // LeftBumper has no default mapping, so there is no row to select.
        SetState(provider, GamepadButtonMask.LeftBumper);
        Sample(vm, host);

        Assert.True(vm.IsEditing);
        Assert.Equal(GamepadButton.LeftBumper, vm.Editor.SelectedButton);
        Assert.Null(vm.SelectedMapping);
        Assert.Equal(MappingTargetKind.KeyPress, vm.Editor.TargetKind);
    }

    [Fact]
    public void Deflecting_A_Stick_Selects_Its_Mapping_Row()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, default, 0f, 0f, 0.9f, 0f);
        Sample(vm, host);

        Assert.True(vm.IsEditing);
        Assert.True(vm.Editor.SourceIsStick);
        Assert.Equal(GamepadStick.Right, vm.Editor.SelectedStick);

        var selected = Assert.IsType<MappingRowViewModel>(vm.SelectedMapping);
        Assert.Equal(new StickBinding(GamepadStick.Right), selected.Entry.Source);
    }

    [Fact]
    public void A_Button_Held_From_Connect_Does_Not_Navigate()
    {
        var (vm, host, provider, _) = CreateHost();

        // The pad is already holding B when the first sample arrives: that is the baseline,
        // not a press, so nothing must open.
        Connect(provider, GamepadButtonMask.B);
        Sample(vm, host);

        Assert.False(vm.IsEditing);
    }

    [Fact]
    public void Releasing_And_Pressing_Again_Navigates_Again()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Assert.False(vm.IsEditing);

        SetState(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);

        Assert.True(vm.IsEditing);
        Assert.Equal(GamepadButton.B, vm.Editor.SelectedButton);
    }

    [Fact]
    public void Holding_A_Button_Does_Not_Re_Navigate()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Assert.Equal(GamepadButton.B, vm.Editor.SelectedButton);

        // The user retargets the mapping while still holding the button; a held button is not
        // a new press, so the editor must not reload and throw the edit away.
        vm.Editor.SelectedKey = GamepadKey.Space;

        Sample(vm, host);

        Assert.Equal(GamepadKey.Space, vm.Editor.SelectedKey);
    }

    [Fact]
    public void Pressing_Another_Button_While_Editing_Switches_The_Editor()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);
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

        // The press that opens the editor may still inject once - the engine was live and
        // unsuspended at that moment - but everything pressed while editing must not.
        SetState(provider, GamepadButtonMask.B);
        Sample(vm, host);
        Assert.True(vm.IsEditing);

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

        Assert.True(vm.IsEditing);
        Assert.False(host.IsRunning, "navigation must sample, never start the mapping loop");
    }

    [Fact]
    public void Pressing_A_Button_With_The_Engine_Stopped_Opens_Its_Mapping_Settings()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        PresenceSample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        PresenceSample(vm, host);

        Assert.False(host.IsRunning);
        Assert.True(vm.IsEditing);
        Assert.Equal(GamepadButton.B, vm.Editor.SelectedButton);
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
    public void Deflecting_A_Stick_Navigates_With_The_Engine_Stopped()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        PresenceSample(vm, host);

        SetState(provider, default, 0f, 0f, 0.9f, 0f);
        PresenceSample(vm, host);

        Assert.True(vm.IsEditing);
        Assert.Equal(GamepadStick.Right, vm.Editor.SelectedStick);
    }

    [Fact]
    public void Presence_Navigation_Never_Ticks_Or_Injects()
    {
        var (vm, host, provider, sink) = CreateHost();
        Connect(provider);
        PresenceSample(vm, host);

        SetState(provider, GamepadButtonMask.B);
        PresenceSample(vm, host);

        Assert.True(vm.IsEditing);
        Assert.Empty(sink.Calls);
    }
}
