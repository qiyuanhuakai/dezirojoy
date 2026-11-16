using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The generic panel's display strategy, in isolation from the window: what the device
/// declares decides what exists, what a real sample has latched decides what reads as
/// detected, and what the profile maps decides what is always shown. Nothing here may
/// reorder, and nothing here may touch a mapping.
/// </summary>
public sealed class GenericGamepadPanelViewModelTests
{
    private const string DeviceId = "pad-01";

    private static GamepadControlDescriptor Button(
        string label,
        GamepadButton button,
        GamepadControlZone zone)
        => new(new ButtonBinding(button), label, GamepadControlKind.Button, zone);

    private static GamepadControlDescriptor Stick(string label, GamepadStick stick)
        => new(new StickBinding(stick), label, GamepadControlKind.Direction, GamepadControlZone.Directions);

    private static GamepadControlDescriptor Trigger(
        string label,
        GamepadButton button)
        => new(new ButtonBinding(button), label, GamepadControlKind.Axis, GamepadControlZone.Shoulders);

    /// <summary>
    /// A pad that declares a stick, the four face buttons, both bumpers, both triggers, the
    /// guide and one extra code - the Core's own build order is the panel order, and the
    /// zones come from the declaration.
    /// </summary>
    private static List<GamepadControlDescriptor> DeclaredControls()
    =>
        [
            Stick("左摇杆", GamepadStick.Left),
            Button("十字键 上", GamepadButton.DPadUp, GamepadControlZone.Directions),
            Button("十字键 下", GamepadButton.DPadDown, GamepadControlZone.Directions),
            Button("A", GamepadButton.A, GamepadControlZone.MainButtons),
            Button("B", GamepadButton.B, GamepadControlZone.MainButtons),
            Button("LB", GamepadButton.LeftBumper, GamepadControlZone.Shoulders),
            Trigger("LT", GamepadButton.LeftTrigger),
            Button("Guide", GamepadButton.Guide, GamepadControlZone.Functions),
            new GamepadControlDescriptor(
                new ButtonBinding(GamepadButton.Extra, 305),
                "附加键 305",
                GamepadControlKind.Other,
                GamepadControlZone.Extensions),
        ];

    private static GenericGamepadPanelViewModel PanelWith(Func<InputBinding, bool>? isBound = null)
    {
        var panel = new GenericGamepadPanelViewModel();
        panel.Load(DeclaredControls(), isBound ?? (_ => false));
        return panel;
    }

    [Fact]
    public void A_Declared_Control_That_Has_Never_Been_Detected_Waits_In_More_Inputs()
    {
        var panel = PanelWith();

        Assert.True(panel.HasItems);
        Assert.All(panel.Items, item =>
        {
            Assert.False(item.IsShown);
            Assert.True(item.IsInMoreInputs);
        });
        Assert.Equal(panel.Items.Count, panel.MoreInputCount);
        Assert.All(panel.Zones, zone => Assert.Empty(zone));
    }

    [Fact]
    public void A_Bound_Control_Is_Always_Shown_Even_When_The_Device_Has_Not_Detected_It()
    {
        var panel = PanelWith(source => source is ButtonBinding { Button: GamepadButton.Guide });

        var guide = panel.Items.Single(item => item.Label == "Guide");

        // Nothing has been pressed yet, and the guide still holds its slot.
        Assert.False(guide.IsDetected);
        Assert.True(guide.IsBound);
        Assert.True(guide.IsShown);
        Assert.False(guide.IsInMoreInputs);
        Assert.Contains(guide, panel.FunctionItems);
        Assert.Equal(panel.Items.Count - 1, panel.MoreInputCount);
    }

    [Fact]
    public void A_Real_Sample_Latches_Detection_And_A_Release_Does_Not_Take_It_Back()
    {
        var panel = PanelWith();

        var a = panel.Items.Single(item => item.Label == "A");
        var stick = panel.Items.Single(item => item.Label == "左摇杆");

        panel.Update(GamepadState.Create(GamepadButtonMask.A, 0.9f, 0f, 0f, 0f));

        // The press latched both the button and the deflected stick.
        Assert.True(a.IsDetected);
        Assert.True(a.IsLive);
        Assert.True(stick.IsDetected);
        Assert.True(stick.IsLive);
        Assert.True(a.IsPressed);

        // Releasing - and a later snapshot's worth of quiet samples - leaves them on the
        // panel: the latch is one-way, because "never touched" and "gone" are different facts.
        panel.Update(GamepadState.Create(default, 0f, 0f, 0f, 0f));
        panel.Update(GamepadState.Create(default, 0f, 0f, 0f, 0f));

        Assert.False(a.IsPressed);
        Assert.True(a.IsDetected);
        Assert.True(a.IsShown);
        Assert.False(a.IsInMoreInputs);
        Assert.True(stick.IsDetected);
        Assert.True(stick.IsShown);
        Assert.Contains(a, panel.MainButtonItems);
        Assert.Contains(stick, panel.DirectionItems);
        Assert.Equal(panel.Items.Count - 2, panel.MoreInputCount);
    }

    [Fact]
    public void A_Disconnect_Clears_The_Latches_So_The_Next_Device_Starts_Fresh()
    {
        var panel = PanelWith();

        panel.Update(GamepadState.Create(GamepadButtonMask.A, 0f, 0f, 0f, 0f));
        Assert.True(panel.Items.Single(item => item.Label == "A").IsDetected);

        panel.Clear();

        Assert.All(panel.Items, item =>
        {
            Assert.False(item.IsDetected);
            Assert.False(item.IsPressed);
        });
        Assert.Equal(panel.Items.Count, panel.MoreInputCount);
    }

    [Fact]
    public void Detection_Never_Reorders_The_Panel()
    {
        var panel = PanelWith();

        var restOrder = panel.Items.Select(item => item.Label).ToArray();

        // Detection arrives, then leaves, then arrives again in a different shape.
        panel.Update(GamepadState.Create(GamepadButtonMask.B, 0f, 0f, 0f, 0f));
        panel.Update(GamepadState.Create(default, 0f, 0f, 0f, 0f));
        panel.Update(GamepadState.Create(GamepadButtonMask.A, 0f, 0f, 0f, 0f));

        // The declaration order never moves, whatever the samples latched.
        Assert.Equal(restOrder, panel.Items.Select(item => item.Label).ToArray());

        // ...and each zone bucket keeps the declaration order inside it. The stick was never
        // pressed in this test, so it still waits in 更多输入 - detection is what promotes a
        // control, and nothing here pressed it.
        Assert.Empty(panel.DirectionItems);
        Assert.Equal(["A", "B"], panel.MainButtonItems.Select(item => item.Label).ToArray());
        Assert.Equal(
            ["左摇杆", "十字键 上", "十字键 下", "LB", "LT", "Guide", "附加键 305"],
            panel.MoreInputItems.Select(item => item.Label).ToArray());
    }

    [Fact]
    public void Showing_All_Inputs_Promotes_The_Waiting_Controls_Onto_The_Panel()
    {
        var panel = PanelWith();

        Assert.Equal(panel.Items.Count, panel.MoreInputCount);

        panel.ToggleShowAllCommand.Execute(null);

        Assert.True(panel.ShowAllInputs);
        Assert.Equal(0, panel.MoreInputCount);
        Assert.All(panel.Items, item => Assert.True(item.IsShown));

        // The promotion keeps the declaration order inside each zone, and the Core's five
        // zones stay five - the trigger stays a shoulder-zone axis and the extra code stays
        // an extension, neither merged into a neighbour.
        Assert.Equal(
            ["左摇杆", "十字键 上", "十字键 下"],
            panel.DirectionItems.Select(item => item.Label).ToArray());
        Assert.Equal(["A", "B"], panel.MainButtonItems.Select(item => item.Label).ToArray());
        Assert.Equal(["LB", "LT"], panel.ShoulderItems.Select(item => item.Label).ToArray());
        Assert.Equal(["Guide"], panel.FunctionItems.Select(item => item.Label).ToArray());
        Assert.Equal(["附加键 305"], panel.ExtensionItems.Select(item => item.Label).ToArray());
        Assert.Equal(
            GamepadControlKind.Axis,
            panel.Items.Single(item => item.Label == "LT").Kind);
        Assert.Equal(
            GamepadControlKind.Other,
            panel.Items.Single(item => item.Label == "附加键 305").Kind);

        // And it collapses back, with the waiting controls collected again.
        panel.ToggleShowAllCommand.Execute(null);

        Assert.False(panel.ShowAllInputs);
        Assert.Equal(panel.Items.Count, panel.MoreInputCount);
    }

    [Fact]
    public void Hiding_Is_Display_Only_And_Restorable()
    {
        var panel = PanelWith(source => source is ButtonBinding { Button: GamepadButton.A });
        panel.Update(GamepadState.Create(GamepadButtonMask.B, 0f, 0f, 0f, 0f));

        var b = panel.Items.Single(item => item.Label == "B");
        var a = panel.Items.Single(item => item.Label == "A");

        panel.HideCommand.Execute(b);

        Assert.True(b.IsUserHidden);
        Assert.False(b.IsShown);
        Assert.DoesNotContain(b, panel.MainButtonItems);
        Assert.Equal(1, panel.HiddenCount);

        // Hiding never touches the mapping behind a control, and the bound control beside
        // it keeps its slot.
        Assert.True(a.IsBound);
        Assert.Contains(a, panel.MainButtonItems);

        panel.RestoreHiddenCommand.Execute(null);

        Assert.False(b.IsUserHidden);
        Assert.Contains(b, panel.MainButtonItems);
        Assert.Equal(0, panel.HiddenCount);
    }

    [Fact]
    public void A_Bound_Control_Refuses_To_Be_Hidden()
    {
        var panel = PanelWith(source => source is ButtonBinding { Button: GamepadButton.A });

        var a = panel.Items.Single(item => item.Label == "A");

        Assert.False(a.CanHide);

        panel.HideCommand.Execute(a);

        Assert.False(a.IsUserHidden);
        Assert.True(a.IsShown);
        Assert.Contains(a, panel.MainButtonItems);
    }

    [Fact]
    public void Labels_Come_From_The_Devices_Own_Declaration()
    {
        var panel = PanelWith();

        Assert.Equal(
            DeclaredControls().Select(descriptor => descriptor.Label),
            panel.Items.Select(item => item.Label));
        Assert.Equal(
            DeclaredControls().Select(descriptor => descriptor.Zone),
            panel.Items.Select(item => item.Zone));
        Assert.Equal(
            DeclaredControls().Select(descriptor => descriptor.Kind),
            panel.Items.Select(item => item.Kind));
    }

    [Fact]
    public void The_Capture_Target_Is_Marked_While_Capture_Is_Armed()
    {
        var panel = PanelWith();

        // A direction on the stick still marks the stick's card: the control is the stick,
        // whatever direction the draft happens to carry.
        panel.UpdateCapture(isCapturing: true, new StickBinding(GamepadStick.Left) { Direction = InputDirection.Up });

        var stick = panel.Items.Single(item => item.Label == "左摇杆");
        Assert.True(stick.IsCaptureTarget);
        Assert.All(panel.Items.Where(item => item != stick), item => Assert.False(item.IsCaptureTarget));

        panel.UpdateCapture(isCapturing: false, null);

        Assert.All(panel.Items, item => Assert.False(item.IsCaptureTarget));
    }

    [Fact]
    public void A_Sticks_Mode_Selector_Follows_The_Mappings_The_Profile_Carries()
    {
        var panel = PanelWith();
        var stick = panel.Items.Single(item => item.Label == "左摇杆");

        panel.SyncStickModes([new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Left),
            Target = new MouseMoveTarget(new MouseStickSettings()),
        }]);
        Assert.Equal(GenericStickMode.Continuous, stick.StickMode);
        Assert.Equal("连续轴", stick.StickModeLabel);

        panel.SyncStickModes(
        [
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Left) { Direction = InputDirection.Up },
                Target = new KeyPressTarget(GamepadKey.W),
            },
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Left) { Direction = InputDirection.Down },
                Target = new KeyPressTarget(GamepadKey.S),
            },
        ]);
        Assert.Equal(GenericStickMode.FourWay, stick.StickMode);
        Assert.Equal("四方向", stick.StickModeLabel);
    }

    [Fact]
    public async Task Switching_A_Sticks_Mode_Asks_The_Window_And_Reverts_A_Refusal()
    {
        var panel = PanelWith();
        var stick = panel.Items.Single(item => item.Label == "左摇杆");

        GenericStickMode? asked = null;
        panel.RequestStickModeChange = (item, mode) =>
        {
            asked = mode;
            return Task.FromResult(true);
        };

        await panel.SetStickModeCommand.ExecuteAsync(stick);

        Assert.Equal(GenericStickMode.FourWay, asked);
        Assert.Equal(GenericStickMode.FourWay, stick.StickMode);

        panel.RequestStickModeChange = (_, _) => Task.FromResult(false);
        await panel.SetStickModeCommand.ExecuteAsync(stick);

        // The daemon said no, so the selector goes back to the mode the mappings carry
        // rather than claiming one they do not.
        Assert.Equal(GenericStickMode.FourWay, stick.StickMode);
    }

    [Fact]
    public void The_Appearance_Switch_Redraws_A_Stick_Without_Touching_Its_Mappings()
    {
        var panel = PanelWith();
        var stick = panel.Items.Single(item => item.Label == "左摇杆");
        var button = panel.Items.Single(item => item.Label == "A");

        // Appearance and execution are two switches: the stick starts on the stick glyph and
        // the continuous axis, and flipping the glyph moves neither the mode nor a mapping.
        Assert.Equal(GenericStickAppearance.Stick, stick.StickAppearance);
        Assert.Equal("摇杆示意", stick.StickAppearanceLabel);
        Assert.True(stick.IsStickGlyph);
        Assert.False(stick.IsDpadGlyph);

        stick.SwitchAppearanceCommand.Execute(null);

        Assert.Equal(GenericStickAppearance.Dpad, stick.StickAppearance);
        Assert.Equal("十字方向示意", stick.StickAppearanceLabel);
        Assert.False(stick.IsStickGlyph);
        Assert.True(stick.IsDpadGlyph);

        // The execution mode beside it did not move, and the source a mapping binds to is
        // untouched - the glyph is a drawing, not a direction.
        Assert.Equal(GenericStickMode.Continuous, stick.StickMode);
        Assert.Equal(new StickBinding(GamepadStick.Left), stick.Source);
        Assert.Null(Assert.IsType<StickBinding>(stick.Source).Direction);

        stick.SwitchAppearanceCommand.Execute(null);

        Assert.Equal(GenericStickAppearance.Stick, stick.StickAppearance);

        // A control that is not a stick offers neither switch.
        Assert.False(button.IsStick);
        button.SwitchAppearanceCommand.Execute(null);
        Assert.Equal(GenericStickAppearance.Stick, button.StickAppearance);
    }
}
