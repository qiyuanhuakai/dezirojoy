using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The 屏幕键盘 special key end to end in local mode, where this window's own engine host is
/// the mapping authority. The three things that have to hold together: the engine's toggle
/// reaches the window, opening the keyboard takes the input lease away from the mapping, and
/// what the keyboard types goes out through the host's own output path rather than through a
/// sink of its own.
/// </summary>
public sealed class MainWindowViewModelScreenKeyboardTests
{
    private const string DeviceId = "xinput:0";

    private static (MainWindowViewModel Vm, EngineHost Host, FakeGamepadProvider Provider, RecordingOutputSink Sink) CreateHost()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var vm = new MainWindowViewModel(host, store);

        return (vm, host, provider, sink);
    }

    /// <summary>
    /// One tick of the pad with <paramref name="button"/> held, driven through the view model
    /// the way the view drives it. A press is an edge, so a held button needs a neutral step
    /// between two presses of it.
    /// </summary>
    private static void Neutral(MainWindowViewModel vm, FakeGamepadProvider provider)
    {
        provider.SetState(DeviceId, GamepadState.Neutral);
        SampleHelper.Sample(vm, vm.Host);
    }

    /// <summary>
    /// One tick of the pad with <paramref name="button"/> held, driven through the view model
    /// the way the view drives it. A press is an edge, so a held button needs a neutral step
    /// between two presses of it.
    /// </summary>
    private static void Tick(MainWindowViewModel vm, FakeGamepadProvider provider, GamepadButton button)
    {
        Neutral(vm, provider);
        provider.SetState(DeviceId, GamepadState.Create(button.ToMask(), 0, 0, 0, 0));
        SampleHelper.Sample(vm, vm.Host);
    }

    /// <summary>
    /// Connects the pad and takes physical input out of the window's scope, which is the state
    /// the floating keyboard is actually used in: the target application is the one in front,
    /// so this window's own editor lease is not holding the pad. Without it the lease would be
    /// suspended from the start and the engine would never tick.
    /// </summary>
    private static void Connect(MainWindowViewModel vm, FakeGamepadProvider provider)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Neutral);
        SampleHelper.Sample(vm, vm.Host);
        vm.SetPhysicalInputScope(false);
        Assert.False(vm.Host.IsInjectionSuspended, "with the window out of scope the mapping owns the pad");
    }

    [Fact]
    public void The_Screen_Keyboard_Special_Key_Opens_The_Floating_Keyboard()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(vm, provider);

        // A 屏幕键盘 key on the click of Back, configured through the editor's own picker.
        vm.OpenEditorForSource(new ButtonBinding(GamepadButton.Back));
        vm.Editor.TargetKind = MappingTargetKind.SpecialKey;
        vm.Editor.ActiveSpecialKind = SpecialKeyKind.Keyboard;
        vm.CommitEditorCommand.Execute(null);
        Assert.False(vm.ScreenKeyboard.IsOpen);

        Tick(vm, provider, GamepadButton.Back);

        Assert.True(vm.ScreenKeyboard.IsOpen, "the special key must summon the floating keyboard");
    }

    [Fact]
    public void Opening_The_Keyboard_Takes_The_Injection_Lease_Away_From_The_Mapping()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(vm, provider);
        Assert.False(host.IsInjectionSuspended);

        vm.OpenEditorForSource(new ButtonBinding(GamepadButton.Back));
        vm.Editor.TargetKind = MappingTargetKind.SpecialKey;
        vm.Editor.ActiveSpecialKind = SpecialKeyKind.Keyboard;
        vm.CommitEditorCommand.Execute(null);

        Tick(vm, provider, GamepadButton.Back);
        Assert.True(host.IsInjectionSuspended, "the keyboard owns the pad while it is open");

        // The key that summoned it is also the key that puts it away: the engine is suspended,
        // so the window answers the press itself.
        Tick(vm, provider, GamepadButton.Back);
        Assert.False(vm.ScreenKeyboard.IsOpen);
        Assert.False(host.IsInjectionSuspended, "closing the keyboard hands the lease back");
    }

    [Fact]
    public void While_The_Keyboard_Is_Open_The_Pad_Drives_It_Instead_Of_The_Mapping()
    {
        var (vm, host, provider, sink) = CreateHost();
        Connect(vm, provider);

        // B types a B keypress through the mapping, and Back opens the keyboard.
        vm.OpenEditorForSource(new ButtonBinding(GamepadButton.Back));
        vm.Editor.TargetKind = MappingTargetKind.SpecialKey;
        vm.Editor.ActiveSpecialKind = SpecialKeyKind.Keyboard;
        vm.CommitEditorCommand.Execute(null);

        Tick(vm, provider, GamepadButton.Back);
        Assert.True(vm.ScreenKeyboard.IsOpen);
        sink.Calls.Clear();

        // A held button fires once rather than per sample: the edges come off the same
        // per-device baseline every other navigation reads.
        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.DPadRight, 0, 0, 0, 0));
        SampleHelper.Sample(vm, host);
        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.DPadRight, 0, 0, 0, 0));
        SampleHelper.Sample(vm, host);

        Assert.Equal("b", vm.ScreenKeyboard.Keyboard.FocusedKey!.Label);

        // Nothing reached the mapping behind the keyboard, and the mapping's own B key never
        // fired while the keyboard held the pad.
        Assert.DoesNotContain(sink.Calls, call => call.StartsWith("down:", StringComparison.Ordinal));
    }

    [Fact]
    public void What_The_Keyboard_Types_Goes_Out_Through_The_Hosts_Own_Output_Path()
    {
        var (vm, host, provider, sink) = CreateHost();
        Connect(vm, provider);

        vm.OpenEditorForSource(new ButtonBinding(GamepadButton.Back));
        vm.Editor.TargetKind = MappingTargetKind.SpecialKey;
        vm.Editor.ActiveSpecialKind = SpecialKeyKind.Keyboard;
        vm.CommitEditorCommand.Execute(null);
        Tick(vm, provider, GamepadButton.Back);
        sink.Calls.Clear();

        // A presses the key the cursor is on: the keystroke goes to the focused application
        // through the host, which is the sole injection authority in local mode.
        vm.ScreenKeyboard.Navigate(GamepadButtonMask.A);
        Assert.Contains("text:a", sink.Calls);

        // 退格 is a key tap, not text, and the modifiers are pressed around it.
        vm.ScreenKeyboard.Keyboard.CursorRow = vm.ScreenKeyboard.Keyboard.ActivePage.Rows.Count - 1;
        vm.ScreenKeyboard.Keyboard.CursorColumn = 0;
        vm.ScreenKeyboard.Navigate(GamepadButtonMask.A);

        Assert.Contains("down:Backspace", sink.Calls);
        Assert.Contains("up:Backspace", sink.Calls);
    }

    [Fact]
    public void The_Keyboard_Answers_The_Pad_Even_When_The_Window_Is_Out_Of_Scope()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(vm, provider);

        vm.OpenEditorForSource(new ButtonBinding(GamepadButton.Back));
        vm.Editor.TargetKind = MappingTargetKind.SpecialKey;
        vm.Editor.ActiveSpecialKind = SpecialKeyKind.Keyboard;
        vm.CommitEditorCommand.Execute(null);
        Tick(vm, provider, GamepadButton.Back);

        // The window is not the one in front - the target application is - so physical input
        // is out of the editor's scope, and the keyboard still takes the pad.
        vm.SetPhysicalInputScope(false);

        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.DPadRight, 0, 0, 0, 0));
        SampleHelper.Sample(vm, host);

        Assert.Equal("b", vm.ScreenKeyboard.Keyboard.FocusedKey!.Label);
    }
}
