using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The 手柄劫持（临时） jump: with the hijack on, a new button press opens that element's
/// mapping configuration directly - the entry it already carries, or a fresh draft - and
/// asks for its card to be scrolled into view. Capture still wins, the same press is never
/// consumed twice, a stick deflection is not a button press, and turning the hijack off
/// returns every press to the active mapping at once.
/// </summary>
public sealed class GamepadHijackJumpTests
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
        vm.SetGamepadNavigationEnabled(true);
        return (vm, host, provider, sink);
    }

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

    private static void Connect(FakeGamepadProvider provider, GamepadButtonMask buttons = default)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(buttons, 0f, 0f, 0f, 0f));
    }

    private static void Press(
        MainWindowViewModel vm,
        EngineHost host,
        FakeGamepadProvider provider,
        GamepadButtonMask buttons,
        IReadOnlyList<int>? extraButtons = null)
    {
        // A press is an edge, so a held button needs a neutral step between two presses.
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        Sample(vm, host);
        provider.SetState(DeviceId, GamepadState.Create(buttons, 0f, 0f, 0f, 0f, extraButtons));
        Sample(vm, host);
    }

    [Fact]
    public void A_Mapped_Button_Opens_Its_Existing_Base_Entry()
    {
        var (vm, host, provider, sink) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        // B carries the default mapping (B -> Enter): the jump loads that entry rather than
        // arming a second one for the same element.
        Press(vm, host, provider, GamepadButtonMask.B);

        Assert.True(vm.IsEditing);
        Assert.Equal(GamepadButton.B, vm.Editor.SelectedButton);
        Assert.Equal(GamepadKey.Enter, vm.Editor.SelectedKey);
        Assert.Equal(10, vm.ActiveProfile!.Mappings.Count);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void An_Unmapped_Button_Opens_A_Fresh_Draft_And_Writes_Nothing()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        Press(vm, host, provider, GamepadButtonMask.LeftBumper);

        Assert.True(vm.IsEditing);
        Assert.Equal(GamepadButton.LeftBumper, vm.Editor.SelectedButton);
        Assert.Null(vm.SelectedMapping);
        Assert.False(vm.Editor.IsCapturingButton, "a jump must never arm a capture");
        Assert.Equal(10, vm.ActiveProfile!.Mappings.Count);
    }

    [Fact]
    public void A_DPad_Press_Jumps_To_The_DPad_Entry()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        // DPadUp carries the default mapping (DPadUp -> ArrowUp).
        Press(vm, host, provider, GamepadButtonMask.DPadUp);

        Assert.True(vm.IsEditing);
        Assert.Equal(GamepadButton.DPadUp, vm.Editor.SelectedButton);
        Assert.Equal(GamepadKey.ArrowUp, vm.Editor.SelectedKey);
    }

    [Fact]
    public void An_Extra_Button_Press_Jumps_To_That_Raw_Code()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        Press(vm, host, provider, default, extraButtons: [7]);

        Assert.True(vm.IsEditing);
        Assert.Equal(GamepadButton.Extra, vm.Editor.SelectedButton);
        Assert.Equal(7, vm.Editor.SelectedExtraCode);
    }

    [Fact]
    public void An_Extra_Button_With_A_Mapping_Opens_That_Entry()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        // Configure extra button 7 through the editor first, exactly as a user would.
        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.Extra;
        vm.Editor.SelectedExtraCode = 7;
        vm.Editor.TargetKind = MappingTargetKind.KeyPress;
        vm.Editor.SelectedKey = GamepadKey.F13;
        vm.CommitEditorCommand.Execute(null);
        Assert.False(vm.IsEditing);

        Press(vm, host, provider, default, extraButtons: [7]);

        Assert.True(vm.IsEditing);
        Assert.Equal(GamepadButton.Extra, vm.Editor.SelectedButton);
        Assert.Equal(7, vm.Editor.SelectedExtraCode);
        Assert.Equal(GamepadKey.F13, vm.Editor.SelectedKey);
    }

    [Fact]
    public void Pressing_Another_Button_While_Editing_Jumps_To_Its_Card()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        Press(vm, host, provider, GamepadButtonMask.B);
        Assert.Equal(GamepadButton.B, vm.Editor.SelectedButton);

        // With the editor already open the next button still jumps: X carries the default
        // mapping (X -> Ctrl+C), so that entry loads.
        Press(vm, host, provider, GamepadButtonMask.X);

        Assert.True(vm.IsEditing);
        Assert.Equal(GamepadButton.X, vm.Editor.SelectedButton);
        Assert.Equal(KeyModifiers.Ctrl, vm.Editor.Modifiers);
        Assert.Equal(GamepadKey.C, vm.Editor.SelectedKey);
    }

    [Fact]
    public void Button_Capture_Wins_Over_The_Jump()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        Press(vm, host, provider, GamepadButtonMask.B);
        var row = Assert.IsType<MappingRowViewModel>(vm.SelectedMapping);

        // Capture is armed inside the editor the jump opened: the next press becomes the
        // mapping's new source, not a jump to another card.
        vm.Editor.IsCapturingButton = true;
        Press(vm, host, provider, GamepadButtonMask.X);

        Assert.Equal(GamepadButton.X, vm.Editor.SelectedButton);
        Assert.False(vm.Editor.IsCapturingButton, "one button is enough, capture must stand down");
        Assert.Same(row, vm.SelectedMapping);
    }

    [Fact]
    public void Keyboard_Capture_Swallows_The_Press_Instead_Of_Jumping()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        Press(vm, host, provider, GamepadButtonMask.B);
        var row = Assert.IsType<MappingRowViewModel>(vm.SelectedMapping);

        vm.Editor.IsCapturingKey = true;
        Press(vm, host, provider, GamepadButtonMask.X);

        Assert.Equal(GamepadButton.B, vm.Editor.SelectedButton);
        Assert.True(vm.Editor.IsCapturingKey);
        Assert.Same(row, vm.SelectedMapping);
    }

    [Fact]
    public void The_Press_That_Opened_The_Editor_Is_Not_Consumed_Again()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        // The jump opens B's entry while B is still held: the very same press must not be
        // read a second time by the editor it just opened - not as a capture, and not as a
        // reload that would drop the loaded entry.
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        Sample(vm, host);
        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.B, 0f, 0f, 0f, 0f));
        Sample(vm, host);
        Assert.True(vm.IsEditing);
        Assert.Equal(GamepadKey.Enter, vm.Editor.SelectedKey);

        // Still holding B: no new edge, so nothing reloads and no capture arms.
        Sample(vm, host);
        Sample(vm, host);

        Assert.Equal(GamepadKey.Enter, vm.Editor.SelectedKey);
        Assert.False(vm.Editor.IsCapturingButton);

        // An unsaved edit survives the held press too.
        vm.Editor.SelectedKey = GamepadKey.Space;
        Sample(vm, host);

        Assert.Equal(GamepadKey.Space, vm.Editor.SelectedKey);
    }

    [Fact]
    public void A_Stick_Deflection_Selects_Without_Jumping()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        // Pure analog movement is not a button press: it selects the stick's element and
        // nothing more, so flicking a stick never opens an editor.
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0.9f, 0f));
        Sample(vm, host);

        Assert.Equal(new StickBinding(GamepadStick.Right), vm.SelectedSource);
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public void A_Stick_Click_Button_Press_Jumps_Like_Any_Other_Button()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        // Map the left stick's own click button first: the stick's click is a button, so a
        // press of it jumps to that entry even though a deflection does not.
        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.LeftStick;
        vm.Editor.TargetKind = MappingTargetKind.KeyPress;
        vm.Editor.SelectedKey = GamepadKey.F14;
        vm.CommitEditorCommand.Execute(null);
        Assert.False(vm.IsEditing);

        Press(vm, host, provider, GamepadButtonMask.LeftStick);

        Assert.True(vm.IsEditing);
        Assert.Equal(GamepadButton.LeftStick, vm.Editor.SelectedButton);
        Assert.Equal(GamepadKey.F14, vm.Editor.SelectedKey);
    }

    [Fact]
    public void The_Jump_Asks_For_The_Mapped_Card_To_Be_Scrolled_Into_View()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        MappingCardViewModel? requested = null;
        vm.MappingCardVisibleRequested += card => requested = card;

        Press(vm, host, provider, GamepadButtonMask.B);

        var card = Assert.IsType<MappingCardViewModel>(requested);
        Assert.IsType<ButtonBinding>(card.Source);
        Assert.Equal(GamepadButton.B, Assert.IsType<ButtonBinding>(card.Source).Button);
    }

    [Fact]
    public void An_Unmapped_Element_Has_No_Card_To_Scroll_To()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        var requests = 0;
        vm.MappingCardVisibleRequested += _ => requests++;

        Press(vm, host, provider, GamepadButtonMask.LeftBumper);

        Assert.True(vm.IsEditing, "the draft still opens");
        Assert.Equal(0, requests);
    }

    [Fact]
    public void Turning_Hijack_Off_Returns_The_Next_Press_To_The_Mapping()
    {
        var (vm, host, provider, sink) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        Press(vm, host, provider, GamepadButtonMask.B);
        Assert.True(vm.IsEditing);

        vm.SetGamepadNavigationEnabled(false);
        sink.Calls.Clear();

        // Off is immediate: the press follows the active mapping again instead of jumping.
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        Sample(vm, host);
        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.B, 0f, 0f, 0f, 0f));
        Sample(vm, host);

        Assert.Contains("down:Enter", sink.Calls);
        Assert.True(vm.IsEditing, "the open draft stays open - only the pad's job changed");
    }

    [Fact]
    public void Leaving_Scope_Stops_The_Jump_And_Clears_The_Baseline()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(provider);
        Sample(vm, host);

        vm.SetPhysicalInputScope(false);

        // Out of scope a press neither jumps nor reaches the mapping.
        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.B, 0f, 0f, 0f, 0f));
        Sample(vm, host);
        Assert.False(vm.IsEditing);

        vm.SetPhysicalInputScope(true);

        // B is still held across the boundary: reentry makes it the new baseline, so the
        // stale hold cannot fire a jump on the way back in.
        Sample(vm, host);
        Assert.False(vm.IsEditing);

        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        Sample(vm, host);
        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.B, 0f, 0f, 0f, 0f));
        Sample(vm, host);

        Assert.True(vm.IsEditing, "a fresh press after reentry jumps again");
        Assert.Equal(GamepadButton.B, vm.Editor.SelectedButton);
    }
}
