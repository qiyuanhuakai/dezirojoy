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

    [Fact]
    public void Defaults_To_The_Button_Source_Kind()
    {
        var editor = new MappingEditorViewModel();

        Assert.Equal(MappingSourceKind.Button, editor.SourceKind);
        Assert.True(editor.ShowButtonPicker);
        Assert.False(editor.ShowStickPicker);
    }

    [Fact]
    public void Choosing_The_Stick_Source_Kind_Shows_Only_The_Stick_Picker()
    {
        var editor = new MappingEditorViewModel();

        editor.SourceKind = MappingSourceKind.Stick;

        Assert.True(editor.SourceIsStick);
        Assert.False(editor.ShowButtonPicker);
        Assert.True(editor.ShowStickPicker);
    }

    [Fact]
    public void Choosing_The_Button_Source_Kind_Shows_Only_The_Button_Picker()
    {
        var editor = new MappingEditorViewModel { SourceIsStick = true };

        editor.SourceKind = MappingSourceKind.Button;

        Assert.False(editor.SourceIsStick);
        Assert.True(editor.ShowButtonPicker);
        Assert.False(editor.ShowStickPicker);
    }

    [Fact]
    public void A_Button_Source_Only_Offers_Targets_The_Engine_Honors()
    {
        var editor = new MappingEditorViewModel();

        Assert.Equal(
            [MappingTargetKind.KeyPress, MappingTargetKind.KeyCombo, MappingTargetKind.MouseButton],
            editor.AvailableTargetKinds);
    }

    [Fact]
    public void A_Stick_Source_Only_Offers_Targets_The_Engine_Honors()
    {
        var editor = new MappingEditorViewModel();

        editor.SourceKind = MappingSourceKind.Stick;

        Assert.Equal([MappingTargetKind.MouseMove, MappingTargetKind.WheelScroll], editor.AvailableTargetKinds);
    }

    [Fact]
    public void Switching_To_A_Stick_Source_Moves_The_Target_To_A_Stick_Action()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.KeyCombo };

        editor.SourceKind = MappingSourceKind.Stick;

        Assert.Equal(MappingTargetKind.MouseMove, editor.TargetKind);
    }

    [Fact]
    public void Switching_Back_To_A_Button_Source_Moves_The_Target_To_A_Key()
    {
        var editor = new MappingEditorViewModel { SourceKind = MappingSourceKind.Stick };

        editor.SourceKind = MappingSourceKind.Button;

        Assert.Equal(MappingTargetKind.KeyPress, editor.TargetKind);
    }

    [Fact]
    public void Button_Options_Are_Grouped_By_Category()
    {
        var editor = new MappingEditorViewModel();

        var headers = editor.ButtonOptions.Where(o => o.IsHeader).Select(o => o.Label).ToArray();

        Assert.Equal(
            ["面部按键", "肩键与扳机", "摇杆", "十字键", "系统键"],
            headers);
    }

    [Fact]
    public void Button_Options_List_Every_Standard_Button_Exactly_Once()
    {
        var editor = new MappingEditorViewModel();

        var buttons = editor.ButtonOptions.Where(o => !o.IsHeader).Select(o => o.Button).ToArray();

        Assert.Equal(17, buttons.Length);
        Assert.Equal(17, buttons.Distinct().Count());
        Assert.DoesNotContain(GamepadButton.Extra, buttons);
    }

    [Fact]
    public void Selecting_A_Category_Header_Does_Not_Change_The_Source()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Y };
        var header = editor.ButtonOptions.First(o => o.IsHeader);

        editor.SelectedButtonOption = header;

        Assert.Equal(GamepadButton.Y, editor.SelectedButton);
        Assert.False(editor.SelectedButtonOption!.IsHeader);
        Assert.Equal(GamepadButton.Y, editor.SelectedButtonOption.Button);
    }

    [Fact]
    public void Selecting_An_Option_Updates_The_Source_Button()
    {
        var editor = new MappingEditorViewModel();
        var option = editor.ButtonOptions.First(o => !o.IsHeader && o.Button == GamepadButton.LeftBumper);

        editor.SelectedButtonOption = option;

        Assert.Equal(GamepadButton.LeftBumper, editor.SelectedButton);
        Assert.Equal(option, editor.SelectedButtonOption);
    }

    [Fact]
    public void Extra_Buttons_Appear_In_The_Extension_Group()
    {
        var editor = new MappingEditorViewModel();

        editor.RegisterExtraButton(305);

        var headers = editor.ButtonOptions.Where(o => o.IsHeader).Select(o => o.Label).ToArray();
        var extra = editor.ButtonOptions.Where(o => !o.IsHeader && o.Button == GamepadButton.Extra).ToArray();

        Assert.Contains("扩展按键", headers);
        var option = Assert.Single(extra);
        Assert.Equal(305, option.Code);
        Assert.Equal("按键 305", option.Label);
    }

    [Fact]
    public void Registering_The_Same_Extra_Button_Twice_Keeps_One_Entry()
    {
        var editor = new MappingEditorViewModel();

        editor.RegisterExtraButton(305);
        editor.RegisterExtraButton(305);

        Assert.Single(
            editor.ButtonOptions,
            option => !option.IsHeader && option.Button == GamepadButton.Extra);
    }

    [Fact]
    public void BuildEntry_Creates_An_Extra_Button_Source_With_Its_Raw_Code()
    {
        var editor = new MappingEditorViewModel();
        editor.RegisterExtraButton(305);
        editor.SelectedButtonOption = editor.ButtonOptions.First(o => !o.IsHeader && o.Button == GamepadButton.Extra);

        var binding = Assert.IsType<ButtonBinding>(editor.BuildSource());

        Assert.Equal(GamepadButton.Extra, binding.Button);
        Assert.Equal(305, binding.Code);
    }

    [Fact]
    public void LoadFrom_Restores_An_Extra_Button_Source_And_Lists_It()
    {
        var editor = new MappingEditorViewModel();
        var entry = new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.Extra, 305),
            Target = new KeyPressTarget(GamepadKey.F13),
        };

        editor.LoadFrom(entry);

        Assert.Equal(GamepadButton.Extra, editor.SelectedButton);
        Assert.Equal(305, editor.SelectedExtraCode);
        Assert.Equal(entry.Source, editor.BuildSource());
        Assert.Contains(editor.ButtonOptions, o => !o.IsHeader && o.Button == GamepadButton.Extra && o.Code == 305);
    }

    [Fact]
    public void Gamepad_Capture_Selects_The_Newly_Pressed_Button()
    {
        var editor = new MappingEditorViewModel { IsCapturingButton = true };

        editor.CaptureGamepadSample(GamepadState.Create(GamepadButtonMask.None, 0, 0, 0, 0));
        editor.CaptureGamepadSample(GamepadState.Create(GamepadButtonMask.LeftBumper, 0, 0, 0, 0));

        Assert.Equal(GamepadButton.LeftBumper, editor.SelectedButton);
        Assert.False(editor.IsCapturingButton, "one button is enough, capture must stand down");
    }

    [Fact]
    public void Gamepad_Capture_Ignores_Buttons_That_Were_Already_Held()
    {
        var editor = new MappingEditorViewModel();

        // Held before capture was armed: the first sample only establishes the baseline.
        editor.CaptureGamepadSample(GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));
        editor.IsCapturingButton = true;
        editor.CaptureGamepadSample(GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        Assert.Equal(GamepadButton.A, editor.SelectedButton);
        Assert.True(editor.IsCapturingButton, "a held button is not a press edge");
    }

    [Fact]
    public void Gamepad_Capture_Selects_An_Extra_Button_And_Lists_It()
    {
        var editor = new MappingEditorViewModel { IsCapturingButton = true };

        editor.CaptureGamepadSample(GamepadState.Create(GamepadButtonMask.None, 0, 0, 0, 0, [305]));

        Assert.Equal(GamepadButton.Extra, editor.SelectedButton);
        Assert.Equal(305, editor.SelectedExtraCode);
        Assert.Contains(editor.ButtonOptions, o => !o.IsHeader && o.Button == GamepadButton.Extra && o.Code == 305);
    }

    [Fact]
    public void Gamepad_Capture_Does_Nothing_While_Disarmed()
    {
        var editor = new MappingEditorViewModel();

        editor.CaptureGamepadSample(GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        Assert.Equal(GamepadButton.A, editor.SelectedButton);
    }

    [Fact]
    public void Keyboard_Capture_Configures_A_Lone_Key()
    {
        var editor = new MappingEditorViewModel { IsCapturingKey = true };

        editor.CaptureKeyboardKey(GamepadKey.Space, KeyModifiers.None);

        Assert.Equal(MappingTargetKind.KeyPress, editor.TargetKind);
        Assert.Equal(GamepadKey.Space, editor.SelectedKey);
        Assert.Equal(KeyModifiers.None, editor.Modifiers);
    }

    [Fact]
    public void Keyboard_Capture_Configures_A_Modifier_Combo()
    {
        var editor = new MappingEditorViewModel { IsCapturingKey = true };

        editor.CaptureKeyboardKey(GamepadKey.C, KeyModifiers.Ctrl);

        Assert.Equal(MappingTargetKind.KeyCombo, editor.TargetKind);
        Assert.Equal(GamepadKey.C, editor.SelectedKey);
        Assert.True(editor.ModifierCtrl);
        Assert.False(editor.ModifierShift);
        Assert.Equal(KeyModifiers.Ctrl, editor.Modifiers);
    }

    [Fact]
    public void Keyboard_Capture_Configures_A_Multi_Modifier_Combo()
    {
        var editor = new MappingEditorViewModel { IsCapturingKey = true };

        editor.CaptureKeyboardKey(GamepadKey.V, KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Win);

        Assert.Equal(KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Win, editor.Modifiers);
        Assert.Equal(GamepadKey.V, editor.SelectedKey);
    }

    [Fact]
    public void Keyboard_Capture_Ends_When_All_Keys_Are_Released()
    {
        var editor = new MappingEditorViewModel { IsCapturingKey = true };

        editor.CaptureKeyboardKey(GamepadKey.C, KeyModifiers.Ctrl);
        editor.EndKeyboardCapture();

        Assert.False(editor.IsCapturingKey);
        Assert.Equal(GamepadKey.C, editor.SelectedKey);
    }

    [Fact]
    public void Keyboard_Capture_Does_Nothing_While_Disarmed()
    {
        var editor = new MappingEditorViewModel();

        editor.CaptureKeyboardKey(GamepadKey.C, KeyModifiers.Ctrl);

        Assert.Equal(GamepadKey.Enter, editor.SelectedKey);
        Assert.Equal(MappingTargetKind.KeyPress, editor.TargetKind);
    }

    [Fact]
    public void Capturing_A_Key_While_A_Stick_Is_Selected_Switches_The_Source_To_A_Button()
    {
        var editor = new MappingEditorViewModel
        {
            SourceKind = MappingSourceKind.Stick,
            IsCapturingKey = true,
        };

        editor.CaptureKeyboardKey(GamepadKey.C, KeyModifiers.Ctrl);

        Assert.Equal(MappingSourceKind.Button, editor.SourceKind);
        Assert.Equal(MappingTargetKind.KeyCombo, editor.TargetKind);
    }

    [Fact]
    public void Capture_Labels_Follow_The_Capture_State()
    {
        var editor = new MappingEditorViewModel();

        Assert.Equal("捕获按键", editor.CaptureButtonLabel);
        Assert.Equal("捕获键盘", editor.CaptureKeyLabel);

        editor.IsCapturingButton = true;
        editor.IsCapturingKey = true;

        Assert.Equal("停止捕获", editor.CaptureButtonLabel);
        Assert.Equal("停止捕获", editor.CaptureKeyLabel);
    }

    [Fact]
    public void Reset_Disarms_Both_Capture_Modes()
    {
        var editor = new MappingEditorViewModel
        {
            IsCapturingButton = true,
            IsCapturingKey = true,
        };

        editor.Reset();

        Assert.False(editor.IsCapturingButton);
        Assert.False(editor.IsCapturingKey);
    }

    [Fact]
    public void Key_Options_Are_Grouped_By_Category()
    {
        var editor = new MappingEditorViewModel();

        var headers = editor.KeyOptions.Where(o => o.IsHeader).Select(o => o.Label).ToArray();

        Assert.Equal(
            ["面部按键", "肩键与扳机", "摇杆", "十字键", "系统键", "扩展按键"],
            headers);
    }

    [Fact]
    public void Key_Options_List_Every_Gamepad_Key_Exactly_Once()
    {
        var editor = new MappingEditorViewModel();

        var keys = editor.KeyOptions.Where(o => !o.IsHeader).Select(o => o.Key).ToArray();

        Assert.Equal(Enum.GetValues<GamepadKey>().OrderBy(key => key), keys.OrderBy(key => key));
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void Selecting_A_Key_Option_Sets_The_Target_Key()
    {
        var editor = new MappingEditorViewModel();
        var option = editor.KeyOptions.First(o => !o.IsHeader && o.Key == GamepadKey.F13);

        editor.SelectedKeyOption = option;

        Assert.Equal(GamepadKey.F13, editor.SelectedKey);
        Assert.Equal(option, editor.SelectedKeyOption);
        Assert.Equal(MappingTargetKind.KeyPress, editor.TargetKind);
    }

    [Fact]
    public void Selecting_A_Key_Header_Does_Not_Change_The_Target()
    {
        var editor = new MappingEditorViewModel { SelectedKey = GamepadKey.Z };
        var header = editor.KeyOptions.First(o => o.IsHeader);

        editor.SelectedKeyOption = header;

        Assert.Equal(GamepadKey.Z, editor.SelectedKey);
        Assert.False(editor.SelectedKeyOption!.IsHeader);
        Assert.Equal(GamepadKey.Z, editor.SelectedKeyOption.Key);
    }

    [Fact]
    public void Selecting_A_Key_While_A_Mouse_Button_Is_Targeted_Switches_To_A_Key_Target()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.MouseButton };

        editor.SelectedKeyOption = editor.KeyOptions.First(o => !o.IsHeader && o.Key == GamepadKey.Space);

        Assert.Equal(MappingTargetKind.KeyPress, editor.TargetKind);
        Assert.Equal(GamepadKey.Space, editor.SelectedKey);
    }

    [Fact]
    public void LoadFrom_Syncs_The_Selected_Key_Option()
    {
        var editor = new MappingEditorViewModel();
        var entry = new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.Y),
            Target = new KeyPressTarget(GamepadKey.F13),
        };

        editor.LoadFrom(entry);

        Assert.Equal(GamepadKey.F13, editor.SelectedKeyOption!.Key);
        Assert.False(editor.SelectedKeyOption.IsHeader);
    }

    [Fact]
    public void LoadSource_Arms_The_Editor_For_The_Clicked_Button()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Guide };

        editor.LoadSource(new ButtonBinding(GamepadButton.LeftBumper));

        Assert.False(editor.SourceIsStick);
        Assert.Equal(GamepadButton.LeftBumper, editor.SelectedButton);
        Assert.Equal(MappingTargetKind.KeyPress, editor.TargetKind);
    }

    [Fact]
    public void LoadSource_Arms_The_Editor_For_The_Clicked_Stick()
    {
        var editor = new MappingEditorViewModel();

        editor.LoadSource(new StickBinding(GamepadStick.Left));

        Assert.True(editor.SourceIsStick);
        Assert.Equal(GamepadStick.Left, editor.SelectedStick);
        Assert.Equal(MappingTargetKind.MouseMove, editor.TargetKind);
    }

    [Fact]
    public void LoadSource_Registers_A_Clicked_Extra_Button()
    {
        var editor = new MappingEditorViewModel();

        editor.LoadSource(new ButtonBinding(GamepadButton.Extra, 305));

        Assert.Equal(GamepadButton.Extra, editor.SelectedButton);
        Assert.Equal(305, editor.SelectedExtraCode);
        Assert.Contains(editor.ButtonOptions, o => !o.IsHeader && o.Button == GamepadButton.Extra && o.Code == 305);
    }

    [Fact]
    public void SourceDescription_Names_The_Element_Being_Edited()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.LeftBumper };

        Assert.Equal("左肩键 LB", editor.SourceDescription);

        editor.SourceKind = MappingSourceKind.Stick;

        Assert.Equal("右摇杆", editor.SourceDescription);
    }

    [Fact]
    public void TargetDescription_Names_The_Target_Being_Edited()
    {
        var editor = new MappingEditorViewModel
        {
            SelectedButton = GamepadButton.X,
            TargetKind = MappingTargetKind.KeyCombo,
            SelectedKey = GamepadKey.C,
            ModifierCtrl = true,
        };

        Assert.Equal("Ctrl + C", editor.TargetDescription);
    }

    [Fact]
    public void Switching_The_Target_Kind_Away_From_A_Combo_Clears_The_Modifiers()
    {
        var editor = new MappingEditorViewModel
        {
            TargetKind = MappingTargetKind.KeyCombo,
            ModifierCtrl = true,
            ModifierShift = true,
        };

        editor.TargetKind = MappingTargetKind.MouseButton;

        Assert.Equal(KeyModifiers.None, editor.Modifiers);
    }
}
