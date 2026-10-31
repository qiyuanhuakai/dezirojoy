using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

public class MappingEditorViewModelTests
{
    [Fact]
    public void Defaults_Target_A_Key_Press()
    {
        var editor = new MappingEditorViewModel();

        Assert.False(editor.SourceIsStick);
        Assert.Equal(MappingTargetKind.KeyPress, editor.TargetKind);
    }

    [Fact]
    public void BuildEntry_Creates_A_Key_Press_Target()
    {
        var editor = new MappingEditorViewModel
        {
            SelectedButton = GamepadButton.B,
            TargetKind = MappingTargetKind.KeyPress,
            SelectedKey = GamepadKey.Enter,
        };

        var entry = editor.BuildEntry();

        var binding = Assert.IsType<ButtonBinding>(entry.Source);
        Assert.Equal(GamepadButton.B, binding.Button);
        Assert.Equal(new KeyPressTarget(GamepadKey.Enter), entry.Target);
    }

    [Fact]
    public void BuildEntry_Creates_A_Combo_Target_With_Modifiers()
    {
        var editor = new MappingEditorViewModel
        {
            SelectedButton = GamepadButton.X,
            TargetKind = MappingTargetKind.KeyCombo,
            SelectedKey = GamepadKey.C,
            ModifierCtrl = true,
        };

        var entry = editor.BuildEntry();

        var combo = Assert.IsType<KeyComboTarget>(entry.Target);
        Assert.Equal(KeyModifiers.Ctrl, combo.Modifiers);
        Assert.Equal(GamepadKey.C, combo.Key);
    }

    [Fact]
    public void BuildEntry_Creates_A_Mouse_Button_Target()
    {
        var editor = new MappingEditorViewModel
        {
            TargetKind = MappingTargetKind.MouseButton,
            SelectedMouseButton = MouseButton.Right,
        };

        var entry = editor.BuildEntry();

        Assert.Equal(new MouseButtonTarget(MouseButton.Right), entry.Target);
    }

    [Fact]
    public void BuildEntry_Creates_Stick_Targets_With_Their_Settings()
    {
        var mouseEditor = new MappingEditorViewModel
        {
            SourceIsStick = true,
            SelectedStick = GamepadStick.Right,
            TargetKind = MappingTargetKind.MouseMove,
        };
        mouseEditor.MouseSettings = mouseEditor.MouseSettings with { MaxSpeed = 2200f };

        var mouseEntry = mouseEditor.BuildEntry();

        Assert.IsType<StickBinding>(mouseEntry.Source);
        Assert.Equal(2200f, Assert.IsType<MouseMoveTarget>(mouseEntry.Target).Settings.MaxSpeed);

        var wheelEditor = new MappingEditorViewModel
        {
            SourceIsStick = true,
            TargetKind = MappingTargetKind.WheelScroll,
        };
        wheelEditor.ScrollSettings = wheelEditor.ScrollSettings with { MaxTicksPerSecond = 20f };

        var wheelEntry = wheelEditor.BuildEntry();

        Assert.Equal(20f, Assert.IsType<WheelScrollTarget>(wheelEntry.Target).Settings.MaxTicksPerSecond);
    }

    [Fact]
    public void LoadFrom_Populates_Every_Field_For_Editing()
    {
        var entry = new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Left),
            Target = new MouseMoveTarget(new MouseStickSettings { MaxSpeed = 1800f, InvertY = false }),
        };

        var editor = new MappingEditorViewModel();
        editor.LoadFrom(entry);

        Assert.True(editor.SourceIsStick);
        Assert.Equal(GamepadStick.Left, editor.SelectedStick);
        Assert.Equal(MappingTargetKind.MouseMove, editor.TargetKind);
        Assert.Equal(1800f, editor.MouseSettings.MaxSpeed);
        Assert.False(editor.MouseSettings.InvertY);
    }

    [Fact]
    public void LoadFrom_Then_BuildEntry_Reproduces_The_Original()
    {
        var entry = new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.Y),
            Target = new KeyComboTarget(KeyModifiers.Ctrl | KeyModifiers.Shift, GamepadKey.V),
        };

        var editor = new MappingEditorViewModel();
        editor.LoadFrom(entry);

        Assert.Equal(entry.Target, editor.BuildEntry().Target);
        Assert.Equal(entry.Source, editor.BuildEntry().Source);
    }

    [Fact]
    public void Reset_Returns_To_Defaults()
    {
        var editor = new MappingEditorViewModel
        {
            SourceIsStick = true,
            TargetKind = MappingTargetKind.MouseButton,
            SelectedKey = GamepadKey.Z,
            ModifierCtrl = true,
        };

        editor.Reset();

        Assert.False(editor.SourceIsStick);
        Assert.Equal(MappingTargetKind.KeyPress, editor.TargetKind);
        Assert.False(editor.ModifierCtrl);
    }
}
