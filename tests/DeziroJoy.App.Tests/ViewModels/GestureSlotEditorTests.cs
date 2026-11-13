using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

public class GestureSlotEditorTests
{
    [Fact]
    public void A_Pristine_Draft_Builds_A_Key_Press()
    {
        var slot = new GestureSlotEditor();

        Assert.Equal(MappingTargetKind.KeyPress, slot.TargetKind);
        Assert.Equal(new KeyPressTarget(GamepadKey.Enter), slot.BuildTarget());
        Assert.Equal(new KeyPressTarget(GamepadKey.Enter), slot.BuildAction());
    }

    [Fact]
    public void Loading_A_Null_Action_Leaves_The_Slot_Re_Firing_The_Click()
    {
        var slot = new GestureSlotEditor();

        slot.LoadAction(null);

        Assert.True(slot.RepeatsClick);
        Assert.Null(slot.BuildAction());
    }

    [Fact]
    public void Loading_No_Function_Is_Explicit_And_Round_Trips()
    {
        var slot = new GestureSlotEditor();

        slot.LoadAction(new NoneTarget());

        Assert.True(slot.IsNone);
        Assert.IsType<NoneTarget>(slot.BuildAction());

        var reloaded = new GestureSlotEditor();
        reloaded.LoadAction(slot.BuildAction());

        Assert.True(reloaded.IsNone);
        Assert.IsType<NoneTarget>(reloaded.BuildAction());
    }

    [Fact]
    public void Loading_Each_Target_Kind_Fills_The_Matching_Fields()
    {
        var slot = new GestureSlotEditor();

        slot.LoadAction(new KeyPressTarget(GamepadKey.Space));

        Assert.Equal(MappingTargetKind.KeyPress, slot.TargetKind);
        Assert.Equal(GamepadKey.Space, slot.SelectedKey);
        Assert.Equal(new KeyPressTarget(GamepadKey.Space), slot.BuildTarget());

        slot.LoadAction(new KeyComboTarget(KeyModifiers.Ctrl | KeyModifiers.Shift, GamepadKey.C));

        Assert.Equal(MappingTargetKind.KeyCombo, slot.TargetKind);
        Assert.Equal(GamepadKey.C, slot.SelectedKey);
        Assert.True(slot.ModifierCtrl);
        Assert.True(slot.ModifierShift);
        Assert.False(slot.ModifierAlt);
        Assert.Equal(KeyModifiers.Ctrl | KeyModifiers.Shift, slot.Modifiers);
        Assert.Equal(new KeyComboTarget(KeyModifiers.Ctrl | KeyModifiers.Shift, GamepadKey.C), slot.BuildTarget());

        slot.LoadAction(new MouseButtonTarget(MouseButton.Right));

        Assert.Equal(MappingTargetKind.MouseButton, slot.TargetKind);
        Assert.Equal(MouseButton.Right, slot.SelectedMouseButton);
        Assert.Equal(new MouseButtonTarget(MouseButton.Right), slot.BuildTarget());

        var settings = new MouseStickSettings { MaxSpeed = 2200f };
        slot.LoadAction(new MouseMoveTarget(settings));

        Assert.Equal(MappingTargetKind.MouseMove, slot.TargetKind);
        Assert.Equal(2200f, slot.MouseSettings.MaxSpeed);
        Assert.Equal(new MouseMoveTarget(settings), slot.BuildTarget());

        var scroll = new ScrollStickSettings { MaxTicksPerSecond = 20f };
        slot.LoadAction(new WheelScrollTarget(scroll));

        Assert.Equal(MappingTargetKind.WheelScroll, slot.TargetKind);
        Assert.Equal(20f, slot.ScrollSettings.MaxTicksPerSecond);
        Assert.Equal(new WheelScrollTarget(scroll), slot.BuildTarget());
    }

    [Fact]
    public void Leaving_A_Combo_Clears_The_Modifiers()
    {
        var slot = new GestureSlotEditor
        {
            TargetKind = MappingTargetKind.KeyCombo,
            ModifierCtrl = true,
            ModifierWin = true,
        };

        slot.TargetKind = MappingTargetKind.KeyPress;

        Assert.Equal(KeyModifiers.None, slot.Modifiers);
    }

    [Fact]
    public void Editing_A_Field_Gives_A_Re_Firing_Slot_A_Function_Of_Its_Own()
    {
        var slot = new GestureSlotEditor();
        slot.LoadAction(null);

        slot.SelectedKey = GamepadKey.F13;

        Assert.False(slot.RepeatsClick);
        Assert.Equal(new KeyPressTarget(GamepadKey.F13), slot.BuildAction());
    }

    [Fact]
    public void No_Function_Is_Not_Overridden_By_A_Field_Edit()
    {
        var slot = new GestureSlotEditor();
        slot.LoadAction(new NoneTarget());

        slot.SelectedKey = GamepadKey.F13;

        Assert.True(slot.IsNone);
        Assert.IsType<NoneTarget>(slot.BuildAction());
    }

    [Fact]
    public void A_Layer_Special_Key_Round_Trips_Its_Layer_Id()
    {
        var slot = new GestureSlotEditor();
        slot.TargetKind = MappingTargetKind.SpecialKey;
        slot.SpecialKind = SpecialKeyKind.Layer;
        slot.SpecialLayerId = 3;

        Assert.Equal(
            new SpecialKeyTarget(
                SpecialKeyKind.Layer, PrecisionMode.Toggle, LeaderSwitchMode.Hold, 1, 2, LayerId: 3),
            slot.BuildTarget());

        var reloaded = new GestureSlotEditor();
        reloaded.LoadAction(slot.BuildTarget());

        Assert.Equal(SpecialKeyKind.Layer, reloaded.SpecialKind);
        Assert.Equal(3, reloaded.SpecialLayerId);
        Assert.Equal(slot.BuildTarget(), reloaded.BuildTarget());
        Assert.Equal(slot.BuildAction(), reloaded.BuildAction());
    }

    [Fact]
    public void A_Layer_Leader_Is_Hold_Only()
    {
        // The engine rejects a layer key that switches on a press edge, so choosing the kind
        // pins the switch mode instead of leaving a draft that could never run.
        var slot = new GestureSlotEditor();
        slot.TargetKind = MappingTargetKind.SpecialKey;
        slot.SpecialSwitchMode = LeaderSwitchMode.Press;

        slot.SpecialKind = SpecialKeyKind.Layer;

        Assert.Equal(LeaderSwitchMode.Hold, slot.SpecialSwitchMode);
        Assert.Equal(
            LeaderSwitchMode.Hold,
            Assert.IsType<SpecialKeyTarget>(slot.BuildTarget()).SwitchMode);

        // A hand-edited profile that carries a Press-mode layer key is repaired on load.
        var loaded = new GestureSlotEditor();
        loaded.LoadAction(
            new SpecialKeyTarget(SpecialKeyKind.Layer, PrecisionMode.Toggle, LeaderSwitchMode.Press, LayerId: 2));

        Assert.Equal(LeaderSwitchMode.Hold, loaded.SpecialSwitchMode);
        Assert.Equal(2, loaded.SpecialLayerId);
        Assert.Equal(
            new SpecialKeyTarget(
                SpecialKeyKind.Layer, PrecisionMode.Toggle, LeaderSwitchMode.Hold, 1, 2, LayerId: 2),
            loaded.BuildTarget());
    }

    [Fact]
    public void A_Fresh_Layer_Draft_Starts_On_The_First_Layer()
    {
        // Layer 0 is the base layer every mapping already runs, so a layer key names 1 up.
        var slot = new GestureSlotEditor();

        slot.TargetKind = MappingTargetKind.SpecialKey;
        slot.SpecialKind = SpecialKeyKind.Layer;

        Assert.Equal(GestureSlotEditor.FirstLayerId, slot.SpecialLayerId);
        Assert.Equal(1, Assert.IsType<SpecialKeyTarget>(slot.BuildTarget()).LayerId);
    }

    [Fact]
    public void Reset_Returns_A_Layer_Draft_To_The_First_Layer()
    {
        var slot = new GestureSlotEditor();
        slot.LoadAction(new SpecialKeyTarget(SpecialKeyKind.Layer, LayerId: 4));

        slot.Reset();

        Assert.Equal(SpecialKeyKind.Precision, slot.SpecialKind);
        Assert.Equal(GestureSlotEditor.FirstLayerId, slot.SpecialLayerId);
        Assert.Equal(LeaderSwitchMode.Press, slot.SpecialSwitchMode);
        Assert.Equal(new KeyPressTarget(GamepadKey.Enter), slot.BuildTarget());
    }

    [Fact]
    public void Reset_Returns_The_Draft_To_Its_Defaults()
    {
        var slot = new GestureSlotEditor();
        slot.LoadAction(new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C));

        slot.Reset();

        Assert.Equal(MappingTargetKind.KeyPress, slot.TargetKind);
        Assert.Equal(GamepadKey.Enter, slot.SelectedKey);
        Assert.Equal(KeyModifiers.None, slot.Modifiers);
        Assert.False(slot.IsNone);
        Assert.False(slot.RepeatsClick);
        Assert.Equal(new KeyPressTarget(GamepadKey.Enter), slot.BuildTarget());
    }

    [Fact]
    public void A_Fresh_Complex_Shortcut_Draft_Starts_From_The_Example_Script()
    {
        var slot = new GestureSlotEditor();

        slot.TargetKind = MappingTargetKind.ComplexShortcut;

        Assert.Equal(GestureSlotEditor.ExampleScript, slot.Script);
        Assert.Equal(
            new ComplexShortcutTarget { Script = GestureSlotEditor.ExampleScript },
            slot.BuildTarget());
    }

    [Fact]
    public void Loading_A_Complex_Shortcut_Fills_The_Kind_And_The_Script()
    {
        var slot = new GestureSlotEditor();
        var program = new ComplexShortcutTarget { Script = "hold Alt\nevery 1s tap Tab" };

        slot.LoadAction(program);

        Assert.Equal(MappingTargetKind.ComplexShortcut, slot.TargetKind);
        Assert.False(slot.RepeatsClick);
        Assert.Equal(program.Script, slot.Script);
        Assert.Equal(program, slot.BuildTarget());
        Assert.Equal(program, slot.BuildAction());

        var reloaded = new GestureSlotEditor();
        reloaded.LoadAction(slot.BuildAction());

        Assert.Equal(program, reloaded.BuildTarget());
    }

    [Fact]
    public void Reset_Returns_The_Script_To_The_Example()
    {
        var slot = new GestureSlotEditor();
        slot.LoadAction(new ComplexShortcutTarget { Script = "tap F5" });

        slot.Reset();

        Assert.Equal(GestureSlotEditor.ExampleScript, slot.Script);
        Assert.Equal(MappingTargetKind.KeyPress, slot.TargetKind);
    }
}
