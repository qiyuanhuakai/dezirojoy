using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using KeyDownCommand = DeziroJoy.Core.Engine.KeyDownCommand;
using KeyUpCommand = DeziroJoy.Core.Engine.KeyUpCommand;
using MappingEngine = DeziroJoy.Core.Engine.MappingEngine;
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
    public void A_Button_Source_Offers_Every_Target_Kind()
    {
        var editor = new MappingEditorViewModel();

        Assert.Equal(
            [
                MappingTargetKind.KeyPress, MappingTargetKind.KeyCombo, MappingTargetKind.MouseButton,
                MappingTargetKind.MouseMove, MappingTargetKind.WheelScroll, MappingTargetKind.SpecialKey,
                MappingTargetKind.Text, MappingTargetKind.ComplexShortcut,
            ],
            editor.AvailableTargetKinds);
    }

    [Fact]
    public void A_Stick_Source_Offers_Every_Target_Kind()
    {
        var editor = new MappingEditorViewModel();

        editor.SourceKind = MappingSourceKind.Stick;

        Assert.Equal(
            [
                MappingTargetKind.KeyPress, MappingTargetKind.KeyCombo, MappingTargetKind.MouseButton,
                MappingTargetKind.MouseMove, MappingTargetKind.WheelScroll, MappingTargetKind.SpecialKey,
                MappingTargetKind.Text,
            ],
            editor.AvailableTargetKinds);
    }

    [Fact]
    public void Switching_Source_Kind_Keeps_The_Chosen_Target_Kind()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.KeyCombo };

        editor.SourceKind = MappingSourceKind.Stick;

        Assert.Equal(MappingTargetKind.KeyCombo, editor.TargetKind);
        Assert.True(editor.ShowDirectionPicker, "a stick driving a key needs a direction");
    }

    [Fact]
    public void A_Stick_Bound_To_A_Digital_Target_Needs_A_Direction()
    {
        var editor = new MappingEditorViewModel
        {
            SourceKind = MappingSourceKind.Stick,
            TargetKind = MappingTargetKind.KeyPress,
        };

        Assert.True(editor.ShowDirectionPicker);

        editor.SelectedDirection = InputDirection.Up;

        Assert.Equal(InputDirection.Up, Assert.IsType<StickBinding>(editor.BuildSource()).Direction);
    }

    [Fact]
    public void A_Button_Bound_To_A_Continuous_Target_Needs_A_Direction()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.MouseMove };

        Assert.True(editor.ShowDirectionPicker);

        editor.SelectedDirection = InputDirection.Right;

        Assert.Equal(InputDirection.Right, Assert.IsType<ButtonBinding>(editor.BuildSource()).Direction);
    }

    [Fact]
    public void The_Native_Combinations_Need_No_Direction()
    {
        var stickToMouse = new MappingEditorViewModel
        {
            SourceKind = MappingSourceKind.Stick,
            TargetKind = MappingTargetKind.MouseMove,
        };
        var buttonToKey = new MappingEditorViewModel { TargetKind = MappingTargetKind.KeyPress };

        Assert.False(stickToMouse.ShowDirectionPicker);
        Assert.False(buttonToKey.ShowDirectionPicker);
        Assert.Null(Assert.IsType<StickBinding>(stickToMouse.BuildSource()).Direction);
        Assert.Null(Assert.IsType<ButtonBinding>(buttonToKey.BuildSource()).Direction);
    }

    [Fact]
    public void A_Stick_Bound_To_A_Special_Key_Needs_A_Direction()
    {
        // A special key is a digital target like any other: the engine rejects a
        // directionless stick driving one, so the editor has to offer the picker and
        // write the choice into the mapping.
        var editor = new MappingEditorViewModel
        {
            SourceKind = MappingSourceKind.Stick,
            TargetKind = MappingTargetKind.SpecialKey,
        };

        Assert.True(editor.ShowDirectionPicker, "a stick driving a special key needs a direction");

        editor.SelectedDirection = InputDirection.Left;

        var source = Assert.IsType<StickBinding>(editor.BuildSource());
        Assert.Equal(InputDirection.Left, source.Direction);

        // The direction survives a round trip through a saved entry.
        editor.LoadFrom(new MappingEntry
        {
            Source = source,
            Target = new SpecialKeyTarget(SpecialKeyKind.Precision),
        });

        Assert.Equal(MappingTargetKind.SpecialKey, editor.TargetKind);
        Assert.Equal(InputDirection.Left, editor.SelectedDirection);
        Assert.Equal(InputDirection.Left, Assert.IsType<StickBinding>(editor.BuildSource()).Direction);
    }

    [Fact]
    public void Changing_The_Direction_Notifies_The_Source_Description()
    {
        var editor = new MappingEditorViewModel
        {
            SourceKind = MappingSourceKind.Stick,
            TargetKind = MappingTargetKind.KeyPress,
        };

        var raised = new List<string>();
        editor.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        editor.SelectedDirection = InputDirection.Right;

        // The header binds SourceDescription, so the choice has to announce it - not just
        // the picker row - or the visible title keeps the old direction.
        Assert.Contains(nameof(MappingEditorViewModel.SourceDescription), raised);
        Assert.Contains(nameof(MappingEditorViewModel.SelectedDirectionOption), raised);
        Assert.Contains("右", editor.SourceDescription);
    }

    [Fact]
    public void LoadFrom_Restores_A_Stick_Direction()
    {
        var entry = new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Left) { Direction = InputDirection.Up },
            Target = new KeyPressTarget(GamepadKey.W),
        };

        var editor = new MappingEditorViewModel();
        editor.LoadFrom(entry);

        Assert.Equal(InputDirection.Up, editor.SelectedDirection);
        Assert.Equal(entry.Source, editor.BuildSource());
    }

    [Fact]
    public void LoadFrom_Restores_The_Gesture_Settings()
    {
        var entry = new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.B)
            {
                Gesture = new ButtonGesture
                {
                    DoubleClickWindow = 0.25f,
                    LongPressThreshold = 0.4f,
                    LongPressRepeatInterval = 0.1f,
                },
            },
            Target = new KeyPressTarget(GamepadKey.Enter),
        };

        var editor = new MappingEditorViewModel();
        editor.LoadFrom(entry);

        // The timings are pinned now: a loaded gesture is enabled, not restored verbatim.
        Assert.True(editor.HasDoubleClick);
        Assert.True(editor.HasLongPress);
        Assert.True(editor.HasAnyGesture);

        var gesture = Assert.IsType<ButtonBinding>(editor.BuildSource()).Gesture;

        Assert.NotNull(gesture);
        Assert.Equal(ButtonGesture.DefaultDoubleClickWindow, gesture.DoubleClickWindow);
        Assert.Equal(ButtonGesture.DefaultLongPressThreshold, gesture.LongPressThreshold);
        Assert.Equal(0.1f, gesture.LongPressRepeatInterval);
    }

    [Fact]
    public void BuildEntry_Omits_The_Gesture_When_Nothing_Is_Configured()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };

        Assert.False(editor.HasAnyGesture);
        Assert.Null(Assert.IsType<ButtonBinding>(editor.BuildSource()).Gesture);
    }

    [Fact]
    public void Changing_The_Gesture_Settings_Writes_Them_Back_On_Commit()
    {
        var editor = new MappingEditorViewModel
        {
            SelectedButton = GamepadButton.B,
            TargetKind = MappingTargetKind.KeyPress,
        };

        editor.SelectedDoubleClickOption = editor.GestureActionOptions.First(
            o => o.Target is KeyPressTarget { Key: GamepadKey.Space });
        editor.SelectedLongPressOption = editor.GestureActionOptions.First(
            o => o.Target is MouseButtonTarget { Button: MouseButton.Right });

        var gesture = Assert.IsType<ButtonBinding>(editor.BuildEntry().Source).Gesture;

        Assert.NotNull(gesture);
        Assert.Equal(ButtonGesture.DefaultDoubleClickWindow, gesture.DoubleClickWindow);
        Assert.Equal(ButtonGesture.DefaultLongPressThreshold, gesture.LongPressThreshold);
        Assert.Equal(new KeyPressTarget(GamepadKey.Space), gesture.DoubleClickAction);
        Assert.Equal(new MouseButtonTarget(MouseButton.Right), gesture.LongPressAction);
    }

    [Fact]
    public void Reset_Clears_The_Gesture_Settings()
    {
        var editor = new MappingEditorViewModel { SelectedGestureKind = GestureKind.LongPress };
        editor.SelectedDoubleClickOption = editor.GestureActionOptions.First(
            o => o.Target is KeyPressTarget { Key: GamepadKey.Space });
        editor.SelectedLongPressOption = editor.GestureActionOptions.First(
            o => o.Target is MouseButtonTarget { Button: MouseButton.Right });
        editor.ClickRepeatEnabled = true;
        editor.ClickIsNone = true;

        editor.Reset();

        Assert.Equal(GestureKind.Click, editor.SelectedGestureKind);
        Assert.False(editor.HasDoubleClick);
        Assert.False(editor.HasLongPress);
        Assert.False(editor.HasClickRepeat);
        Assert.False(editor.ClickIsNone);
        Assert.False(editor.HasAnyGesture);
        Assert.Null(editor.DoubleClickAction);
        Assert.Null(editor.LongPressAction);
    }

    [Fact]
    public void LoadFrom_Restores_The_Gesture_Actions()
    {
        var entry = new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.B)
            {
                Gesture = new ButtonGesture
                {
                    DoubleClickWindow = 0.25f,
                    LongPressThreshold = 0.4f,
                    LongPressRepeatInterval = 0.1f,
                    DoubleClickAction = new KeyPressTarget(GamepadKey.Space),
                    LongPressAction = new MouseButtonTarget(MouseButton.Right),
                },
            },
            Target = new KeyPressTarget(GamepadKey.Enter),
        };

        var editor = new MappingEditorViewModel();
        editor.LoadFrom(entry);

        Assert.Equal(new KeyPressTarget(GamepadKey.Space), editor.DoubleClickAction);
        Assert.Equal(new MouseButtonTarget(MouseButton.Right), editor.LongPressAction);
        Assert.True(editor.HasDoubleClickAction);
        Assert.True(editor.HasLongPressAction);

        var gesture = Assert.IsType<ButtonBinding>(editor.BuildSource()).Gesture;

        Assert.NotNull(gesture);
        Assert.Equal(new KeyPressTarget(GamepadKey.Space), gesture.DoubleClickAction);
        Assert.Equal(new MouseButtonTarget(MouseButton.Right), gesture.LongPressAction);
    }

    [Fact]
    public void BuildEntry_Writes_The_Gesture_Actions_Back_On_Commit()
    {
        var editor = new MappingEditorViewModel
        {
            SelectedButton = GamepadButton.B,
            TargetKind = MappingTargetKind.KeyPress,
        };

        editor.DoubleClickAction = new KeyPressTarget(GamepadKey.Space);
        editor.LongPressAction = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.W);

        var gesture = Assert.IsType<ButtonBinding>(editor.BuildEntry().Source).Gesture;

        Assert.NotNull(gesture);
        Assert.Equal(new KeyPressTarget(GamepadKey.Space), gesture.DoubleClickAction);
        Assert.Equal(new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.W), gesture.LongPressAction);
    }

    [Fact]
    public void Reset_Clears_The_Gesture_Actions()
    {
        var editor = new MappingEditorViewModel();
        editor.DoubleClickAction = new KeyPressTarget(GamepadKey.Space);
        editor.LongPressAction = new KeyPressTarget(GamepadKey.F13);

        editor.Reset();

        Assert.Null(editor.DoubleClickAction);
        Assert.Null(editor.LongPressAction);
        Assert.False(editor.HasDoubleClickAction);
        Assert.False(editor.HasLongPressAction);
    }

    [Fact]
    public void Choosing_A_Gesture_Action_Enables_Its_Gesture()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };

        Assert.False(editor.HasAnyGesture);

        editor.SelectedDoubleClickOption = editor.GestureActionOptions.First(
            o => o.Target is KeyPressTarget { Key: GamepadKey.Space });

        Assert.True(editor.HasDoubleClick, "a double-click action cannot fire without the gesture");
        Assert.True(editor.HasAnyGesture, "configuring an action introduces the wait");
        Assert.Equal(
            ButtonGesture.DefaultDoubleClickWindow,
            Assert.IsType<ButtonBinding>(editor.BuildSource()).Gesture!.DoubleClickWindow);

        editor.SelectedLongPressOption = editor.GestureActionOptions.First(
            o => o.Target is KeyPressTarget { Key: GamepadKey.F13 });

        Assert.True(editor.HasLongPress);
        Assert.Equal(
            ButtonGesture.DefaultLongPressThreshold,
            Assert.IsType<ButtonBinding>(editor.BuildSource()).Gesture!.LongPressThreshold);
    }

    [Fact]
    public void Gesture_Action_Options_Are_Offered_And_Selecting_One_Sets_The_Action()
    {
        var editor = new MappingEditorViewModel();

        Assert.NotEmpty(editor.GestureActionOptions);
        Assert.Contains(
            editor.GestureActionOptions,
            option => option.Target is KeyPressTarget { Key: GamepadKey.Space });

        var option = editor.GestureActionOptions.First(
            o => o.Target is KeyPressTarget { Key: GamepadKey.Space });

        editor.SelectedDoubleClickOption = option;

        Assert.Equal(new KeyPressTarget(GamepadKey.Space), editor.DoubleClickAction);
        Assert.Equal(option, editor.SelectedDoubleClickOption);
    }

    [Fact]
    public void Gesture_Action_Descriptions_Name_The_Chosen_Action()
    {
        var editor = new MappingEditorViewModel();

        Assert.Equal("已关闭", editor.DoubleClickActionDescription);
        Assert.Equal("已关闭", editor.LongPressActionDescription);

        editor.SelectedDoubleClickOption = editor.GestureActionOptions.First(
            o => o.Label == "重复单击的功能");
        editor.SelectedLongPressOption = editor.GestureActionOptions.First(
            o => o.Target is MouseButtonTarget { Button: MouseButton.Right });

        Assert.Equal("重复单击的功能", editor.DoubleClickActionDescription);
        Assert.Equal("鼠标右键", editor.LongPressActionDescription);

        editor.SelectedDoubleClickOption = editor.GestureActionOptions.First(
            o => o.Target is NoneTarget);

        Assert.Equal("无功能", editor.DoubleClickActionDescription);
    }

    [Fact]
    public void Clearing_A_Gesture_Action_Drops_It_From_The_Built_Source()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };
        editor.SelectedDoubleClickOption = editor.GestureActionOptions.First(
            o => o.Target is KeyPressTarget { Key: GamepadKey.Space });

        editor.ClearDoubleClickActionCommand.Execute(null);

        Assert.Null(editor.DoubleClickAction);
        Assert.False(editor.HasDoubleClick);
        Assert.Null(Assert.IsType<ButtonBinding>(editor.BuildSource()).Gesture?.DoubleClickAction);
    }

    // ---- The three-way gesture switcher -------------------------------------

    [Fact]
    public void The_Gesture_Switcher_Defaults_To_The_Click()
    {
        var editor = new MappingEditorViewModel();

        Assert.Equal(GestureKind.Click, editor.SelectedGestureKind);
        Assert.True(editor.IsClickSelected);
        Assert.False(editor.IsDoubleClickSelected);
        Assert.False(editor.IsLongPressSelected);
    }

    [Fact]
    public void The_Gesture_Switcher_Selects_Each_Of_The_Three_Gestures()
    {
        var editor = new MappingEditorViewModel();

        editor.SelectedGestureKind = GestureKind.DoubleClick;

        Assert.True(editor.IsDoubleClickSelected);
        Assert.False(editor.IsClickSelected);
        Assert.False(editor.IsLongPressSelected);

        editor.SelectedGestureKind = GestureKind.LongPress;

        Assert.True(editor.IsLongPressSelected);
        Assert.False(editor.IsClickSelected);
        Assert.False(editor.IsDoubleClickSelected);

        editor.SelectedGestureKind = GestureKind.Click;

        Assert.True(editor.IsClickSelected);
        Assert.False(editor.IsDoubleClickSelected);
        Assert.False(editor.IsLongPressSelected);
    }

    [Fact]
    public void The_Switcher_Flags_Follow_The_Selection_Back()
    {
        var editor = new MappingEditorViewModel { SelectedGestureKind = GestureKind.LongPress };

        editor.IsClickSelected = true;

        Assert.Equal(GestureKind.Click, editor.SelectedGestureKind);
        Assert.True(editor.IsClickSelected);
        Assert.False(editor.IsLongPressSelected);
    }

    [Fact]
    public void Gesture_Action_Options_Offer_None_Off_And_Repeat_Rows()
    {
        var editor = new MappingEditorViewModel();

        Assert.Contains(editor.GestureActionOptions, o => o.Target is NoneTarget);
        Assert.Contains(editor.GestureActionOptions, o => o.Label == "已关闭");
        Assert.Contains(editor.GestureActionOptions, o => o.Label == "重复单击的功能");
        Assert.Contains(
            editor.GestureActionOptions,
            o => o.Target is KeyPressTarget { Key: GamepadKey.Space });
    }

    [Fact]
    public void The_Picker_Shows_The_Repeat_Row_For_A_Re_Firing_Gesture_And_Off_For_A_Disabled_One()
    {
        var editor = new MappingEditorViewModel();

        Assert.Equal("已关闭", editor.SelectedDoubleClickOption!.Label);

        editor.SelectedDoubleClickOption = editor.GestureActionOptions.First(
            o => o.Label == "重复单击的功能");

        Assert.Equal("重复单击的功能", editor.SelectedDoubleClickOption!.Label);
        Assert.True(editor.HasDoubleClick);
        Assert.Null(editor.DoubleClickAction);
    }

    // ---- 无功能 for every gesture -------------------------------------------

    [Fact]
    public void Every_Gesture_Action_Can_Be_Set_To_No_Function()
    {
        var doubleClick = new MappingEditorViewModel { SelectedButton = GamepadButton.B };
        doubleClick.SelectedDoubleClickOption = doubleClick.GestureActionOptions.First(
            o => o.Target is NoneTarget);

        Assert.IsType<NoneTarget>(doubleClick.DoubleClickAction);
        Assert.True(doubleClick.HasDoubleClick);
        Assert.Equal(
            new NoneTarget(),
            Assert.IsType<ButtonBinding>(doubleClick.BuildSource()).Gesture!.DoubleClickAction);

        var longPress = new MappingEditorViewModel { SelectedButton = GamepadButton.B };
        longPress.SelectedLongPressOption = longPress.GestureActionOptions.First(
            o => o.Target is NoneTarget);

        Assert.IsType<NoneTarget>(longPress.LongPressAction);
        Assert.True(longPress.HasLongPress);
        Assert.Equal(
            new NoneTarget(),
            Assert.IsType<ButtonBinding>(longPress.BuildSource()).Gesture!.LongPressAction);
    }

    [Fact]
    public void The_Click_Action_Can_Be_Set_To_No_Function()
    {
        var editor = new MappingEditorViewModel
        {
            SelectedButton = GamepadButton.B,
            TargetKind = MappingTargetKind.KeyPress,
        };

        editor.ClickIsNone = true;

        Assert.IsType<NoneTarget>(editor.BuildTarget());
        Assert.IsType<NoneTarget>(editor.BuildEntry().Target);

        var reloaded = new MappingEditorViewModel();
        reloaded.LoadFrom(editor.BuildEntry());

        Assert.True(reloaded.ClickIsNone);
        Assert.IsType<NoneTarget>(reloaded.BuildTarget());
    }

    [Fact]
    public void TargetDescription_Names_The_No_Function_Target()
    {
        var editor = new MappingEditorViewModel { ClickIsNone = true };

        Assert.Equal("无功能", editor.TargetDescription);
    }

    // ---- Auto-repeat and the long-press are mutually exclusive --------------

    [Fact]
    public void Enabling_Click_Repeat_Disables_The_Long_Press()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };
        editor.SelectedLongPressOption = editor.GestureActionOptions.First(
            o => o.Target is KeyPressTarget { Key: GamepadKey.F13 });
        Assert.True(editor.HasLongPress);

        editor.ClickRepeatEnabled = true;

        Assert.False(editor.LongPressAvailable);
        Assert.False(editor.HasLongPress);
        Assert.Null(editor.LongPressAction);

        var gesture = Assert.IsType<ButtonBinding>(editor.BuildSource()).Gesture;

        Assert.NotNull(gesture);
        Assert.Equal(ButtonGesture.DefaultClickRepeatInterval, gesture.ClickRepeatInterval);
        Assert.Equal(0f, gesture.LongPressThreshold);
        Assert.Null(gesture.LongPressAction);
    }

    [Fact]
    public void Configuring_The_Long_Press_Disables_Click_Repeat()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };
        editor.ClickRepeatEnabled = true;
        Assert.True(editor.HasClickRepeat);

        editor.SelectedLongPressOption = editor.GestureActionOptions.First(
            o => o.Target is KeyPressTarget { Key: GamepadKey.F13 });

        Assert.False(editor.ClickRepeatEnabled);
        Assert.True(editor.HasLongPress);

        var gesture = Assert.IsType<ButtonBinding>(editor.BuildSource()).Gesture;

        Assert.NotNull(gesture);
        Assert.Equal(0f, gesture.ClickRepeatInterval);
        Assert.Equal(ButtonGesture.DefaultLongPressThreshold, gesture.LongPressThreshold);
    }

    [Fact]
    public void Selecting_The_Long_Press_Switches_Click_Repeat_Off()
    {
        var editor = new MappingEditorViewModel { SelectedGestureKind = GestureKind.LongPress };
        editor.ClickRepeatEnabled = true;

        Assert.Equal(GestureKind.Click, editor.SelectedGestureKind);

        editor.SelectedGestureKind = GestureKind.LongPress;

        Assert.False(editor.ClickRepeatEnabled);
        Assert.True(editor.LongPressAvailable);
    }

    [Fact]
    public void A_Loaded_Inert_Long_Press_Is_Dropped_When_Click_Repeat_Is_On()
    {
        // The Core makes the long-press inert while click auto-repeat runs, so the editor
        // must not load a long-press that would silently do nothing.
        var entry = new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.B)
            {
                Gesture = new ButtonGesture
                {
                    LongPressThreshold = 0.4f,
                    ClickRepeatInterval = 0.1f,
                    LongPressAction = new KeyPressTarget(GamepadKey.F13),
                },
            },
            Target = new KeyPressTarget(GamepadKey.Enter),
        };

        var editor = new MappingEditorViewModel();
        editor.LoadFrom(entry);

        Assert.True(editor.ClickRepeatEnabled);
        Assert.False(editor.LongPressAvailable);
        Assert.False(editor.HasLongPress);
        Assert.Null(editor.LongPressAction);

        var gesture = Assert.IsType<ButtonBinding>(editor.BuildSource()).Gesture;

        Assert.NotNull(gesture);
        Assert.Equal(ButtonGesture.DefaultClickRepeatInterval, gesture.ClickRepeatInterval);
        Assert.Equal(0f, gesture.LongPressThreshold);
    }

    [Fact]
    public void LoadFrom_Restores_Click_Repeat_And_The_Click_None_Target()
    {
        var entry = new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.B)
            {
                Gesture = new ButtonGesture { ClickRepeatInterval = 0.1f },
            },
            Target = new NoneTarget(),
        };

        var editor = new MappingEditorViewModel();
        editor.LoadFrom(entry);

        Assert.True(editor.ClickRepeatEnabled);
        Assert.True(editor.ClickIsNone);
        Assert.IsType<NoneTarget>(editor.BuildTarget());
    }

    [Fact]
    public void LoadFrom_Then_BuildEntry_Preserves_A_Custom_Click_Repeat_Interval()
    {
        // A profile that carries its own click auto-repeat interval must keep it: the
        // editor reads the enabled flag, but the interval itself has to survive the
        // round trip instead of collapsing onto the pinned default.
        var entry = new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.B)
            {
                Gesture = new ButtonGesture { ClickRepeatInterval = 0.15f },
            },
            Target = new KeyPressTarget(GamepadKey.Enter),
        };

        var editor = new MappingEditorViewModel();
        editor.LoadFrom(entry);

        var gesture = Assert.IsType<ButtonBinding>(editor.BuildEntry().Source).Gesture;

        Assert.NotNull(gesture);
        Assert.Equal(0.15f, gesture.ClickRepeatInterval);
    }

    [Fact]
    public void Custom_Repeat_Intervals_Survive_The_Store_And_Editor_Chain()
    {
        // The whole chain a profile travels: store save, a fresh store instance reading
        // the file back (evidence of deserialization, not of a process restart), the
        // editor committing every mapping the way MainWindowViewModel does, save, reload.
        var directory = Path.Combine(Path.GetTempPath(), "dezirojoy-editor-chain", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var profile = new GamepadProfile
            {
                Name = "手势链",
                DeviceId = "gesture-chain",
                Layout = GamepadLayout.Xbox,
                Mappings =
                [
                    new MappingEntry
                    {
                        Source = new ButtonBinding(GamepadButton.B)
                        {
                            Gesture = new ButtonGesture
                            {
                                DoubleClickWindow = ButtonGesture.DefaultDoubleClickWindow,
                                DoubleClickAction = new KeyPressTarget(GamepadKey.Space),
                                ClickRepeatInterval = 0.15f,
                            },
                        },
                        Target = new KeyPressTarget(GamepadKey.Enter),
                    },
                    new MappingEntry
                    {
                        Source = new ButtonBinding(GamepadButton.X)
                        {
                            Gesture = new ButtonGesture
                            {
                                LongPressThreshold = ButtonGesture.DefaultLongPressThreshold,
                                LongPressRepeatInterval = 0.15f,
                                LongPressAction = new MouseButtonTarget(MouseButton.Right),
                            },
                        },
                        Target = new KeyPressTarget(GamepadKey.F13),
                    },
                ],
            };

            new FileProfileStore(directory).Save(profile);

            var stored = new FileProfileStore(directory).Load("gesture-chain");

            Assert.NotNull(stored);
            Assert.Equal(0.15f, GestureOf(stored, GamepadButton.B).ClickRepeatInterval);
            Assert.Equal(0.15f, GestureOf(stored, GamepadButton.X).LongPressRepeatInterval);

            var committed = stored.Mappings
                .Select(entry =>
                {
                    var editor = new MappingEditorViewModel();
                    editor.LoadFrom(entry);
                    return editor.BuildEntry();
                })
                .ToList();

            new FileProfileStore(directory).Save(stored with { Mappings = committed });

            var reloaded = new FileProfileStore(directory).Load("gesture-chain");

            Assert.NotNull(reloaded);

            var repeat = GestureOf(reloaded, GamepadButton.B);

            Assert.Equal(0.15f, repeat.ClickRepeatInterval);
            Assert.Equal(new KeyPressTarget(GamepadKey.Space), repeat.DoubleClickAction);
            Assert.Equal(0f, repeat.LongPressThreshold);
            Assert.Null(repeat.LongPressAction);

            var longPress = GestureOf(reloaded, GamepadButton.X);

            Assert.Equal(0.15f, longPress.LongPressRepeatInterval);
            Assert.Equal(new MouseButtonTarget(MouseButton.Right), longPress.LongPressAction);
            Assert.Equal(0f, longPress.ClickRepeatInterval);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static ButtonGesture GestureOf(GamepadProfile profile, GamepadButton button)
        {
            var entry = profile.Mappings.First(
                mapping => mapping.Source is ButtonBinding binding && binding.Button == button);
            var gesture = Assert.IsType<ButtonBinding>(entry.Source).Gesture;

            Assert.NotNull(gesture);
            return gesture;
        }
    }

    // ---- Pinned defaults, no slider input -----------------------------------

    [Fact]
    public void The_Built_Gesture_Carries_The_Pinned_Defaults_With_No_Slider_Input()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };
        editor.SelectedDoubleClickOption = editor.GestureActionOptions.First(
            o => o.Target is KeyPressTarget { Key: GamepadKey.Space });
        editor.SelectedLongPressOption = editor.GestureActionOptions.First(
            o => o.Target is MouseButtonTarget { Button: MouseButton.Right });

        var gesture = Assert.IsType<ButtonBinding>(editor.BuildSource()).Gesture;

        Assert.NotNull(gesture);
        Assert.Equal(ButtonGesture.DefaultDoubleClickWindow, gesture.DoubleClickWindow);
        Assert.Equal(ButtonGesture.DefaultLongPressThreshold, gesture.LongPressThreshold);
        Assert.Equal(0f, gesture.ClickRepeatInterval);
    }

    [Fact]
    public void Click_Repeat_Uses_The_Pinned_Default_Interval()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };
        editor.ClickRepeatEnabled = true;

        var gesture = Assert.IsType<ButtonBinding>(editor.BuildSource()).Gesture;

        Assert.NotNull(gesture);
        Assert.Equal(ButtonGesture.DefaultClickRepeatInterval, gesture.ClickRepeatInterval);
    }

    [Fact]
    public void Every_Previously_Reachable_Configuration_Is_Still_Reachable()
    {
        // The click keeps every target kind it always had, and gains 无功能.
        foreach (var kind in new[]
        {
            MappingTargetKind.KeyPress, MappingTargetKind.KeyCombo, MappingTargetKind.MouseButton,
            MappingTargetKind.MouseMove, MappingTargetKind.WheelScroll, MappingTargetKind.SpecialKey,
        })
        {
            var editor = new MappingEditorViewModel { TargetKind = kind, SelectedDirection = InputDirection.Up };
            Assert.IsNotType<NoneTarget>(editor.BuildTarget());
        }

        Assert.IsType<NoneTarget>(new MappingEditorViewModel { ClickIsNone = true }.BuildTarget());

        // 双击 / 长按 each keep their four reachable states: off, re-fire, 无功能, custom.
        var doubleClick = new MappingEditorViewModel { SelectedButton = GamepadButton.B };
        Assert.False(doubleClick.HasDoubleClick, "a gesture nothing was chosen for stays off");
        doubleClick.SelectedDoubleClickOption = Row(doubleClick, "重复单击的功能");
        Assert.True(doubleClick.HasDoubleClick);
        Assert.Null(doubleClick.DoubleClickAction);
        doubleClick.SelectedDoubleClickOption = Row(doubleClick, "无功能");
        Assert.IsType<NoneTarget>(doubleClick.DoubleClickAction);
        doubleClick.SelectedDoubleClickOption = Row(doubleClick, "已关闭");
        Assert.False(doubleClick.HasDoubleClick);
        doubleClick.SelectedDoubleClickOption = doubleClick.GestureActionOptions.First(
            o => o.Target is KeyPressTarget { Key: GamepadKey.Space });
        Assert.Equal(new KeyPressTarget(GamepadKey.Space), doubleClick.DoubleClickAction);

        var longPress = new MappingEditorViewModel { SelectedButton = GamepadButton.B };
        Assert.False(longPress.HasLongPress);
        longPress.SelectedLongPressOption = Row(longPress, "重复单击的功能");
        Assert.True(longPress.HasLongPress);
        Assert.Null(longPress.LongPressAction);
        longPress.SelectedLongPressOption = Row(longPress, "无功能");
        Assert.IsType<NoneTarget>(longPress.LongPressAction);
        longPress.SelectedLongPressOption = Row(longPress, "已关闭");
        Assert.False(longPress.HasLongPress);
        longPress.SelectedLongPressOption = longPress.GestureActionOptions.First(
            o => o.Target is MouseButtonTarget { Button: MouseButton.Right });
        Assert.Equal(new MouseButtonTarget(MouseButton.Right), longPress.LongPressAction);

        // 长按重复触发 on and off, and a loaded long-press repeat interval survives.
        var repeat = new MappingEditorViewModel { SelectedButton = GamepadButton.B };
        repeat.ClickRepeatEnabled = true;
        Assert.True(repeat.HasClickRepeat);
        repeat.ClickRepeatEnabled = false;
        Assert.False(repeat.HasClickRepeat);

        var carried = new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.B)
            {
                Gesture = new ButtonGesture { LongPressThreshold = 0.4f, LongPressRepeatInterval = 0.15f },
            },
            Target = new KeyPressTarget(GamepadKey.Enter),
        };
        var loaded = new MappingEditorViewModel();
        loaded.LoadFrom(carried);
        Assert.Equal(
            0.15f,
            Assert.IsType<ButtonBinding>(loaded.BuildSource()).Gesture!.LongPressRepeatInterval);

        static GestureActionOption Row(MappingEditorViewModel editor, string label)
            => editor.GestureActionOptions.First(o => o.Label == label);
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
    public void Every_Button_Option_Stays_Selectable_Including_The_Raw_Code_Pills()
    {
        var editor = new MappingEditorViewModel();
        editor.RegisterExtraButton(305);

        // The picker's presentation was redesigned; the data model was not. Every option the
        // old flat list offered - the raw-code 按键 N pills included - must still select.
        foreach (var option in editor.ButtonOptions.Where(o => !o.IsHeader).ToArray())
        {
            editor.SelectedButtonOption = option;

            Assert.Equal(option.Button, editor.SelectedButton);
            Assert.Equal(option.Code, editor.SelectedExtraCode);
            Assert.Equal(option, editor.SelectedButtonOption);
        }
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
    public void Every_Key_Option_Stays_Selectable()
    {
        var editor = new MappingEditorViewModel();

        // The picker's presentation was redesigned; the data model was not. Every key the
        // old flat list offered must still select, headers excluded.
        foreach (var option in editor.KeyOptions.Where(o => !o.IsHeader).ToArray())
        {
            editor.SelectedKeyOption = option;

            Assert.Equal(option.Key, editor.SelectedKey);
            Assert.Equal(option, editor.SelectedKeyOption);
        }
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

        // A stick driving a key needs a direction, so the description names the one the
        // mapping would commit - the default 上 rather than a silently hidden value.
        Assert.Equal("右摇杆（上）", editor.SourceDescription);
    }

    [Fact]
    public void A_Reopened_Mapping_Shows_The_Direction_Row_It_Saved()
    {
        var editor = new MappingEditorViewModel();

        // Reopen a saved stick -> key mapping whose direction is Right: the picker row
        // follows the loaded direction, so the editor never shows an empty selection.
        editor.LoadFrom(new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Left) { Direction = InputDirection.Right },
            Target = new KeyPressTarget(GamepadKey.W),
        });

        Assert.Equal(InputDirection.Right, editor.SelectedDirection);
        Assert.Equal(InputDirection.Right, editor.SelectedDirectionOption?.Direction);

        // Pick a different direction, then reopen another mapping saved with Right: the
        // row must show the loaded direction, not the one the previous edit left behind.
        editor.SelectedDirectionOption = editor.DirectionOptions.First(
            option => option.Direction == InputDirection.Left);

        editor.LoadFrom(new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Left) { Direction = InputDirection.Right },
            Target = new KeyPressTarget(GamepadKey.W),
        });

        Assert.Equal(InputDirection.Right, editor.SelectedDirection);
        Assert.Equal(InputDirection.Right, editor.SelectedDirectionOption?.Direction);

        // A mapping that carries no direction opens on the default, row in step with it.
        editor.LoadFrom(new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Left),
            Target = new WheelScrollTarget(new ScrollStickSettings()),
        });

        Assert.Equal(InputDirection.Up, editor.SelectedDirection);
        Assert.Equal(InputDirection.Up, editor.SelectedDirectionOption?.Direction);
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

    // ---- T17: three equal gesture slots -----------------------------------

    [Fact]
    public void The_Active_Slot_Follows_The_Selected_Gesture()
    {
        var editor = new MappingEditorViewModel();

        Assert.Same(editor.ClickSlot, editor.ActiveSlot);

        editor.SelectedGestureKind = GestureKind.DoubleClick;

        Assert.Same(editor.DoubleClickSlot, editor.ActiveSlot);

        editor.SelectedGestureKind = GestureKind.LongPress;

        Assert.Same(editor.LongPressSlot, editor.ActiveSlot);
    }

    [Fact]
    public void Selecting_Each_Gesture_Keeps_Distinct_Drafts()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };

        editor.SelectedGestureKind = GestureKind.Click;
        editor.ActiveSlot.TargetKind = MappingTargetKind.KeyCombo;
        editor.ActiveSlot.SelectedKey = GamepadKey.C;
        editor.ActiveSlot.ModifierCtrl = true;

        editor.SelectedGestureKind = GestureKind.DoubleClick;
        editor.ActiveSlot.SelectedKey = GamepadKey.Space;

        editor.SelectedGestureKind = GestureKind.LongPress;
        editor.ActiveSlot.TargetKind = MappingTargetKind.MouseButton;
        editor.ActiveSlot.SelectedMouseButton = MouseButton.Right;

        Assert.Equal(MappingTargetKind.KeyCombo, editor.ClickSlot.TargetKind);
        Assert.Equal(GamepadKey.C, editor.ClickSlot.SelectedKey);
        Assert.True(editor.ClickSlot.ModifierCtrl);

        Assert.Equal(MappingTargetKind.KeyPress, editor.DoubleClickSlot.TargetKind);
        Assert.Equal(GamepadKey.Space, editor.DoubleClickSlot.SelectedKey);

        Assert.Equal(MappingTargetKind.MouseButton, editor.LongPressSlot.TargetKind);
        Assert.Equal(MouseButton.Right, editor.LongPressSlot.SelectedMouseButton);
    }

    [Fact]
    public void Every_Slot_Can_Choose_A_Combo_A_Single_Key_Or_No_Function()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };

        foreach (var slot in new[] { editor.ClickSlot, editor.DoubleClickSlot, editor.LongPressSlot })
        {
            slot.TargetKind = MappingTargetKind.KeyCombo;
            slot.SelectedKey = GamepadKey.V;
            slot.ModifierCtrl = true;
            slot.ModifierShift = true;

            Assert.Equal(
                new KeyComboTarget(KeyModifiers.Ctrl | KeyModifiers.Shift, GamepadKey.V),
                slot.BuildTarget());

            slot.TargetKind = MappingTargetKind.KeyPress;
            slot.SelectedKey = GamepadKey.F13;

            Assert.Equal(new KeyPressTarget(GamepadKey.F13), slot.BuildTarget());

            slot.IsNone = true;

            Assert.IsType<NoneTarget>(slot.BuildTarget());
            Assert.IsType<NoneTarget>(slot.BuildAction());
        }
    }

    [Fact]
    public void Switching_Slots_Restores_Each_Drafts_Values()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };

        editor.SelectedGestureKind = GestureKind.DoubleClick;
        editor.ActiveSlot.SelectedKey = GamepadKey.Space;

        editor.SelectedGestureKind = GestureKind.Click;
        editor.ActiveSlot.SelectedKey = GamepadKey.Z;

        editor.SelectedGestureKind = GestureKind.DoubleClick;

        Assert.Same(editor.DoubleClickSlot, editor.ActiveSlot);
        Assert.Equal(GamepadKey.Space, editor.ActiveSlot.SelectedKey);
        Assert.Equal(GamepadKey.Space, editor.DoubleClickSlot.SelectedKey);
        Assert.Equal(GamepadKey.Z, editor.ClickSlot.SelectedKey);
    }

    [Fact]
    public void The_Click_Surface_And_Its_Draft_Stay_In_Step()
    {
        var editor = new MappingEditorViewModel();

        editor.ClickSlot.TargetKind = MappingTargetKind.KeyCombo;
        editor.ClickSlot.SelectedKey = GamepadKey.C;
        editor.ClickSlot.ModifierCtrl = true;

        Assert.Equal(MappingTargetKind.KeyCombo, editor.TargetKind);
        Assert.Equal(GamepadKey.C, editor.SelectedKey);
        Assert.True(editor.ModifierCtrl);
        Assert.Equal(new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C), editor.BuildTarget());

        editor.TargetKind = MappingTargetKind.KeyPress;

        Assert.Equal(MappingTargetKind.KeyPress, editor.ClickSlot.TargetKind);
        Assert.False(editor.ClickSlot.ModifierCtrl);
    }

    [Fact]
    public void The_Picker_And_The_Slot_Draft_Agree()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };

        editor.SelectedDoubleClickOption = editor.GestureActionOptions.First(
            o => o.Target is KeyPressTarget { Key: GamepadKey.Space });

        Assert.Equal(new KeyPressTarget(GamepadKey.Space), editor.DoubleClickSlot.BuildAction());
        Assert.Equal(GamepadKey.Space, editor.DoubleClickSlot.SelectedKey);

        editor.SelectedLongPressOption = editor.GestureActionOptions.First(o => o.Label == "重复单击的功能");

        Assert.True(editor.LongPressSlot.RepeatsClick);
        Assert.Null(editor.LongPressSlot.BuildAction());
    }

    [Fact]
    public void Committing_A_NoneTarget_Gesture_Action_Never_Silently_Changes_The_Slot()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };

        editor.DoubleClickSlot.IsNone = true;
        editor.LongPressSlot.IsNone = true;

        var gesture = Assert.IsType<ButtonBinding>(editor.BuildEntry().Source).Gesture;

        Assert.NotNull(gesture);
        Assert.IsType<NoneTarget>(gesture.DoubleClickAction);
        Assert.IsType<NoneTarget>(gesture.LongPressAction);

        var reloaded = new MappingEditorViewModel();
        reloaded.LoadFrom(editor.BuildEntry());

        Assert.IsType<NoneTarget>(reloaded.DoubleClickAction);
        Assert.IsType<NoneTarget>(reloaded.LongPressAction);
        Assert.True(reloaded.DoubleClickSlot.IsNone);
        Assert.True(reloaded.LongPressSlot.IsNone);
    }

    [Fact]
    public void Click_Repeat_And_The_Long_Press_Stay_Mutually_Exclusive_Through_The_Slots()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };
        editor.ClickRepeatEnabled = true;

        // Editing the long-press draft switches auto-repeat back off.
        editor.LongPressSlot.SelectedKey = GamepadKey.F13;

        Assert.False(editor.ClickRepeatEnabled);
        Assert.True(editor.HasLongPress);
        Assert.Equal(new KeyPressTarget(GamepadKey.F13), editor.LongPressAction);

        // And switching auto-repeat back on drops the long-press rather than leaving it
        // configured into silence.
        editor.ClickRepeatEnabled = true;

        Assert.False(editor.LongPressAvailable);
        Assert.False(editor.HasLongPress);
        Assert.Null(editor.LongPressAction);

        var gesture = Assert.IsType<ButtonBinding>(editor.BuildSource()).Gesture;

        Assert.NotNull(gesture);
        Assert.Equal(ButtonGesture.DefaultClickRepeatInterval, gesture.ClickRepeatInterval);
        Assert.Equal(0f, gesture.LongPressThreshold);
        Assert.Null(gesture.LongPressAction);
    }

    [Fact]
    public void A_Loaded_Legacy_Gesture_With_Null_Actions_Still_Re_Fires_The_Click()
    {
        var entry = new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.B)
            {
                Gesture = new ButtonGesture
                {
                    DoubleClickWindow = ButtonGesture.DefaultDoubleClickWindow,
                    LongPressThreshold = ButtonGesture.DefaultLongPressThreshold,
                },
            },
            Target = new KeyPressTarget(GamepadKey.Enter),
        };

        var editor = new MappingEditorViewModel();
        editor.LoadFrom(entry);

        Assert.True(editor.HasDoubleClick);
        Assert.True(editor.HasLongPress);
        Assert.Null(editor.DoubleClickAction);
        Assert.Null(editor.LongPressAction);
        Assert.True(editor.DoubleClickSlot.RepeatsClick);
        Assert.True(editor.LongPressSlot.RepeatsClick);
        Assert.Equal("重复单击的功能", editor.DoubleClickActionDescription);
        Assert.Equal("重复单击的功能", editor.LongPressActionDescription);

        var gesture = Assert.IsType<ButtonBinding>(editor.BuildEntry().Source).Gesture;

        Assert.NotNull(gesture);
        Assert.Null(gesture.DoubleClickAction);
        Assert.Null(gesture.LongPressAction);
    }

    [Fact]
    public void BuildEntry_Commits_All_Three_Slots_Into_One_Entry()
    {
        var editor = new MappingEditorViewModel
        {
            SelectedButton = GamepadButton.X,
            TargetKind = MappingTargetKind.KeyCombo,
            SelectedKey = GamepadKey.C,
            ModifierCtrl = true,
        };

        editor.DoubleClickSlot.SelectedKey = GamepadKey.Space;
        editor.LongPressSlot.TargetKind = MappingTargetKind.MouseButton;
        editor.LongPressSlot.SelectedMouseButton = MouseButton.Right;

        var entry = editor.BuildEntry();

        Assert.Equal(new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C), entry.Target);

        var gesture = Assert.IsType<ButtonBinding>(entry.Source).Gesture;

        Assert.NotNull(gesture);
        Assert.Equal(ButtonGesture.DefaultDoubleClickWindow, gesture.DoubleClickWindow);
        Assert.Equal(ButtonGesture.DefaultLongPressThreshold, gesture.LongPressThreshold);
        Assert.Equal(new KeyPressTarget(GamepadKey.Space), gesture.DoubleClickAction);
        Assert.Equal(new MouseButtonTarget(MouseButton.Right), gesture.LongPressAction);

        var reloaded = new MappingEditorViewModel();
        reloaded.LoadFrom(entry);

        Assert.Equal(new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C), reloaded.BuildEntry().Target);
        Assert.Equal(new KeyPressTarget(GamepadKey.Space), reloaded.DoubleClickAction);
        Assert.Equal(new MouseButtonTarget(MouseButton.Right), reloaded.LongPressAction);
    }

    // ---- The categorized key grid -------------------------------------------

    [Fact]
    public void Key_Category_Groups_Cover_Every_Key_In_Category_Order()
    {
        var editor = new MappingEditorViewModel();

        var groups = editor.KeyCategoryGroups.ToArray();

        Assert.Equal(
            ["面部按键", "肩键与扳机", "摇杆", "十字键", "系统键", "扩展按键"],
            groups.Select(group => group.Name));

        // The five fixed categories carry exactly their own keys, in the category's order.
        for (var i = 0; i < KeyCategories.Groups.Length; i++)
        {
            Assert.Equal(KeyCategories.Groups[i].Keys, groups[i].Keys.Select(item => item.Key));
        }

        // The extension group carries every key no fixed category claims.
        var extension = Enum.GetValues<GamepadKey>().Where(key => !KeyCategories.Grouped.Contains(key));

        Assert.Equal(extension, groups[KeyCategories.Groups.Length].Keys.Select(item => item.Key));

        // Across the groups every key appears exactly once, so the grid is a complete
        // picker rather than a sample of one.
        var allKeys = groups.SelectMany(group => group.Keys).Select(item => item.Key).ToArray();

        Assert.Equal(Enum.GetValues<GamepadKey>().OrderBy(key => key), allKeys.OrderBy(key => key));
        Assert.Equal(allKeys.Length, allKeys.Distinct().Count());

        // The grid and the flat lookup share one item per key, so they can never disagree.
        foreach (var group in groups)
        {
            foreach (var item in group.Keys)
            {
                Assert.Contains(editor.KeyOptions, option => ReferenceEquals(option, item));
            }
        }
    }

    [Fact]
    public void Selecting_A_Key_Through_A_Category_Group_Item_Sets_The_Target_Key()
    {
        var editor = new MappingEditorViewModel();
        var item = editor.KeyCategoryGroups.First(group => group.Name == "系统键").Keys
            .First(key => key.Key == GamepadKey.Space);

        editor.ActiveSelectedKeyOption = item;

        Assert.Equal(GamepadKey.Space, editor.ActiveSlot.SelectedKey);
        Assert.Equal(GamepadKey.Space, editor.ActiveSelectedKeyOption!.Key);
    }

    // ---- The key picker's category navigator --------------------------------

    [Fact]
    public void A_Fresh_Editor_Opens_The_Category_Its_Selected_Key_Lives_In()
    {
        var editor = new MappingEditorViewModel();

        // The slot's default key is Enter, a 系统键 key, so the picker opens there rather
        // than on the first category.
        Assert.Equal("系统键", editor.ActiveKeyCategory!.Name);
        Assert.True(editor.ActiveKeyCategory.IsActive);
        Assert.Equal(editor.ActiveKeyCategory, editor.KeyCategoryGroups.Single(group => group.IsActive));
    }

    [Fact]
    public void The_Active_Category_Follows_The_Selected_Key()
    {
        var editor = new MappingEditorViewModel();

        editor.ActiveSelectedKeyOption = editor.KeyOptions.First(
            option => !option.IsHeader && option.Key == GamepadKey.Space);
        Assert.Equal("系统键", editor.ActiveKeyCategory!.Name);

        editor.ActiveSelectedKeyOption = editor.KeyOptions.First(
            option => !option.IsHeader && option.Key == GamepadKey.Y);
        Assert.Equal("面部按键", editor.ActiveKeyCategory.Name);

        // A key no fixed category claims lands in the extension group, however far down it is.
        editor.ActiveSelectedKeyOption = editor.KeyOptions.First(
            option => !option.IsHeader && option.Key == GamepadKey.F13);
        Assert.Equal(KeyCategories.ExtensionGroupName, editor.ActiveKeyCategory.Name);
    }

    [Fact]
    public void A_Category_Segment_Checked_From_The_View_Moves_The_Grid()
    {
        var editor = new MappingEditorViewModel();

        // The navigator is an ItemsControl over the groups, and a segment writes its check
        // straight onto its group - the same two-way idiom the gesture switcher uses.
        var extension = editor.KeyCategoryGroups.First(group => group.Name == KeyCategories.ExtensionGroupName);
        extension.IsActive = true;

        Assert.Equal(extension, editor.ActiveKeyCategory);
        Assert.Equal(extension, editor.KeyCategoryGroups.Single(group => group.IsActive));
        Assert.Equal(6, editor.ActiveKeyCategoryColumns);
    }

    [Fact]
    public void The_Grid_Columns_Fit_The_Category_It_Shows()
    {
        var editor = new MappingEditorViewModel();

        // A short category shares one row instead of floating in a half-empty grid; a long
        // one is capped, so the card's width buys a shorter grid rather than wider caps.
        foreach (var (name, expected) in new[]
                 {
                     ("面部按键", 4), ("肩键与扳机", 4), ("摇杆", 4),
                     ("十字键", 6), ("系统键", 6), (KeyCategories.ExtensionGroupName, 6),
                 })
        {
            var group = editor.KeyCategoryGroups.First(candidate => candidate.Name == name);
            group.IsActive = true;

            Assert.Equal(expected, editor.ActiveKeyCategoryColumns);
        }
    }

    [Fact]
    public void Switching_Gestures_Moves_The_Active_Category_With_The_Selection()
    {
        var editor = new MappingEditorViewModel();

        editor.ActiveSelectedKeyOption = editor.KeyOptions.First(
            option => !option.IsHeader && option.Key == GamepadKey.Y);
        Assert.Equal("面部按键", editor.ActiveKeyCategory!.Name);

        // The double-click's own key lives in another category: the switcher move carries the
        // category with it, so the key that is selected is the key that is in view.
        editor.SelectedGestureKind = GestureKind.DoubleClick;
        editor.ActiveSelectedKeyOption = editor.KeyOptions.First(
            option => !option.IsHeader && option.Key == GamepadKey.Escape);

        Assert.Equal("系统键", editor.ActiveKeyCategory.Name);

        editor.SelectedGestureKind = GestureKind.Click;
        Assert.Equal("面部按键", editor.ActiveKeyCategory.Name);
    }

    [Fact]
    public void Reset_Reopens_The_Category_Of_The_Selected_Key()
    {
        var editor = new MappingEditorViewModel { SelectedKey = GamepadKey.Enter };
        Assert.Equal("系统键", editor.ActiveKeyCategory!.Name);

        // A browse left on another category must not survive the reset. The default key is
        // unchanged by a reset, so only an explicit follow puts the grid back on it.
        editor.KeyCategoryGroups.First(group => group.Name == "面部按键").IsActive = true;
        Assert.Equal("面部按键", editor.ActiveKeyCategory.Name);

        editor.Reset();

        Assert.Equal("系统键", editor.ActiveKeyCategory.Name);
    }

    [Fact]
    public void LoadFrom_Opens_The_Category_Of_The_Loaded_Key()
    {
        var editor = new MappingEditorViewModel();
        var entry = new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.Y),
            Target = new KeyPressTarget(GamepadKey.Numpad7),
        };

        editor.LoadFrom(entry);

        Assert.Equal("十字键", editor.ActiveKeyCategory!.Name);
    }

    // ---- The click-repeat interval editor ------------------------------------

    [Fact]
    public void The_Repeat_Interval_Editor_Shows_The_Default_While_Nothing_Custom_Is_Carried()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };

        Assert.Equal(ButtonGesture.DefaultClickRepeatInterval, editor.ClickRepeatIntervalSeconds, precision: 5);

        editor.ClickRepeatEnabled = true;

        Assert.Equal(ButtonGesture.DefaultClickRepeatInterval, editor.ClickRepeatIntervalSeconds, precision: 5);
        Assert.Equal(
            ButtonGesture.DefaultClickRepeatInterval,
            Assert.IsType<ButtonBinding>(editor.BuildSource()).Gesture!.ClickRepeatInterval);
    }

    [Fact]
    public void The_Repeat_Interval_Clamps_To_Its_Range_And_Snaps_To_Its_Step()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };
        editor.ClickRepeatEnabled = true;

        editor.ClickRepeatIntervalSeconds = 0.03;

        Assert.Equal(0.05, editor.ClickRepeatIntervalSeconds, precision: 5);

        editor.ClickRepeatIntervalSeconds = 0.07;

        Assert.Equal(0.05, editor.ClickRepeatIntervalSeconds, precision: 5);

        editor.ClickRepeatIntervalSeconds = 0.13;

        Assert.Equal(0.15, editor.ClickRepeatIntervalSeconds, precision: 5);

        editor.ClickRepeatIntervalSeconds = 2.50;

        Assert.Equal(2.00, editor.ClickRepeatIntervalSeconds, precision: 5);

        editor.ClickRepeatIntervalSeconds = 0.15;

        Assert.Equal(0.15, editor.ClickRepeatIntervalSeconds, precision: 5);
        Assert.Equal(0.15f, editor.ClickRepeatInterval);
    }

    [Fact]
    public void A_Custom_Repeat_Interval_Round_Trips_Through_Build_And_Load()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };
        editor.ClickRepeatEnabled = true;
        editor.ClickRepeatIntervalSeconds = 0.15;

        var built = editor.BuildEntry();

        Assert.Equal(0.15f, Assert.IsType<ButtonBinding>(built.Source).Gesture!.ClickRepeatInterval);

        var reloaded = new MappingEditorViewModel();
        reloaded.LoadFrom(built);

        Assert.True(reloaded.ClickRepeatEnabled);
        Assert.Equal(0.15, reloaded.ClickRepeatIntervalSeconds, precision: 5);
        Assert.Equal(0.15f, Assert.IsType<ButtonBinding>(reloaded.BuildEntry().Source).Gesture!.ClickRepeatInterval);
    }

    [Fact]
    public void The_Repeat_Interval_Editor_Is_Offered_Only_On_The_Click_Tab_With_Repeat_On()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };

        Assert.False(editor.ShowClickRepeatInterval, "repeat starts off");

        editor.ClickRepeatEnabled = true;

        Assert.True(editor.ShowClickRepeatInterval, "the click tab offers the interval while repeat runs");

        editor.SelectedGestureKind = GestureKind.DoubleClick;

        Assert.False(editor.ShowClickRepeatInterval, "the interval belongs to the click tab alone");

        editor.SelectedGestureKind = GestureKind.Click;
        editor.ClickRepeatEnabled = false;

        Assert.False(editor.ShowClickRepeatInterval, "no repeat, no interval");
    }

    [Fact]
    public void Reset_Returns_The_Repeat_Interval_To_The_Default()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.B };
        editor.ClickRepeatEnabled = true;
        editor.ClickRepeatIntervalSeconds = 0.15;

        editor.Reset();

        Assert.False(editor.ClickRepeatEnabled);
        Assert.Equal(ButtonGesture.DefaultClickRepeatInterval, editor.ClickRepeatIntervalSeconds, precision: 5);
    }

    [Fact]
    public void Loading_A_MouseMove_Mapping_Notifies_ActivePrecisionButton_And_The_Switcher_Refreshes_It()
    {
        var entry = new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Left),
            Target = new MouseMoveTarget(
                new MouseStickSettings { PrecisionButton = GamepadButton.RightStick }),
        };

        var editor = new MappingEditorViewModel();
        var notified = new List<string>();
        editor.PropertyChanged += (_, e) => notified.Add(e.PropertyName!);

        editor.LoadFrom(entry);

        Assert.Equal(GamepadButton.RightStick, editor.ActivePrecisionButton);
        Assert.Contains(nameof(MappingEditorViewModel.ActivePrecisionButton), notified);

        notified.Clear();
        editor.SelectedGestureKind = GestureKind.DoubleClick;

        Assert.Contains(nameof(MappingEditorViewModel.ActivePrecisionButton), notified);

        notified.Clear();
        editor.SelectedGestureKind = GestureKind.LongPress;

        Assert.Contains(nameof(MappingEditorViewModel.ActivePrecisionButton), notified);
    }

    // ---- The special-key target ----------------------------------------------

    [Fact]
    public void The_Target_Kind_List_Offers_The_Special_Key()
    {
        var editor = new MappingEditorViewModel();

        Assert.Contains(MappingTargetKind.SpecialKey, editor.AvailableTargetKinds);
    }

    [Fact]
    public void The_Special_Key_Options_Are_Labelled_In_Chinese()
    {
        var editor = new MappingEditorViewModel();

        Assert.Equal(["精准键", "Leader 键"], editor.SpecialKindOptions.Select(option => option.Label));
        Assert.Equal(["切换预设", "切换临时层"], editor.SpecialLeaderActionOptions.Select(option => option.Label));
        Assert.Equal(["按下切换", "按住切换"], editor.SpecialSwitchModeOptions.Select(option => option.Label));
        Assert.Equal([1, 2, 3, 4], editor.SpecialSlotOptions);
    }

    [Fact]
    public void A_Leader_Special_Key_Round_Trips_Through_The_Editor()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Back };
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        editor.ActiveSpecialLeaderAction = LeaderAction.Preset;
        editor.ActiveSpecialPrecisionMode = PrecisionMode.Hold;
        editor.ActiveSpecialSwitchMode = LeaderSwitchMode.Hold;
        editor.ActiveSpecialSlotA = 3;
        editor.ActiveSpecialSlotB = 4;

        var target = Assert.IsType<SpecialKeyTarget>(editor.BuildEntry().Target);

        Assert.Equal(SpecialKeyKind.Leader, target.Kind);
        Assert.Equal(LeaderAction.Preset, target.Action);
        Assert.Equal(PrecisionMode.Hold, target.PrecisionMode);
        Assert.Equal(LeaderSwitchMode.Hold, target.SwitchMode);
        Assert.Equal(3, target.SlotA);
        Assert.Equal(4, target.SlotB);

        var reloaded = new MappingEditorViewModel();
        reloaded.LoadFrom(editor.BuildEntry());

        Assert.Equal(MappingTargetKind.SpecialKey, reloaded.ActiveTargetKind);
        Assert.Equal(SpecialKeyKind.Leader, reloaded.ActiveSpecialKind);
        Assert.Equal(LeaderAction.Preset, reloaded.ActiveSpecialLeaderAction);
        Assert.Equal(PrecisionMode.Hold, reloaded.ActiveSpecialPrecisionMode);
        Assert.Equal(LeaderSwitchMode.Hold, reloaded.ActiveSpecialSwitchMode);
        Assert.Equal(3, reloaded.ActiveSpecialSlotA);
        Assert.Equal(4, reloaded.ActiveSpecialSlotB);
    }

    [Fact]
    public void A_Precision_Special_Key_Round_Trips_Its_Mode()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.LeftStick };
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Precision;
        editor.ActiveSpecialPrecisionMode = PrecisionMode.Toggle;

        Assert.Equal(
            new SpecialKeyTarget(SpecialKeyKind.Precision, PrecisionMode.Toggle),
            editor.BuildEntry().Target);

        var reloaded = new MappingEditorViewModel();
        reloaded.LoadFrom(editor.BuildEntry());

        Assert.Equal(MappingTargetKind.SpecialKey, reloaded.ActiveTargetKind);
        Assert.Equal(SpecialKeyKind.Precision, reloaded.ActiveSpecialKind);
        Assert.Equal(PrecisionMode.Toggle, reloaded.ActiveSpecialPrecisionMode);
    }

    // ---- The Leader temporary layer ------------------------------------------

    [Fact]
    public void The_Layer_Options_Name_The_Base_Layer_And_The_Temporary_One()
    {
        var editor = new MappingEditorViewModel();

        Assert.Equal([0, 1], editor.LayerOptions.Select(option => option.Value));
        Assert.Equal(
            ["基础层", "临时层"],
            editor.LayerOptions.Select(option => option.Label));

        Assert.Equal(0, editor.SelectedLayer);
        Assert.Equal("基础层", editor.LayerDescription);
        Assert.Equal("所属层：基础层", editor.LayerSummary);
    }

    [Fact]
    public void A_Layer_Leader_Round_Trips_Through_The_Editor()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Back };
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        editor.ActiveSpecialLeaderAction = LeaderAction.Layer;

        var target = Assert.IsType<SpecialKeyTarget>(editor.BuildEntry().Target);

        Assert.Equal(SpecialKeyKind.Leader, target.Kind);
        Assert.Equal(LeaderAction.Layer, target.Action);

        // The leader itself sits on the base layer: a temporary layer is entered from there.
        Assert.Equal(0, editor.BuildEntry().Layer);

        var reloaded = new MappingEditorViewModel();
        reloaded.LoadFrom(editor.BuildEntry());

        Assert.Equal(MappingTargetKind.SpecialKey, reloaded.ActiveTargetKind);
        Assert.Equal(SpecialKeyKind.Leader, reloaded.ActiveSpecialKind);
        Assert.Equal(LeaderAction.Layer, reloaded.ActiveSpecialLeaderAction);
        Assert.Equal(0, reloaded.SelectedLayer);
    }

    [Fact]
    public void A_Layer_Leader_Chooses_Its_Switch_Mode_And_The_Picker_Says_What_It_Does()
    {
        var editor = new MappingEditorViewModel();
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialSwitchMode = LeaderSwitchMode.Press;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        editor.ActiveSpecialLeaderAction = LeaderAction.Layer;

        // Both leader actions choose the mode freely: the engine runs a layer leader under
        // either one, so the row stays enabled and the hint spells out what each does.
        Assert.True(editor.ShowSpecialSwitchControls);
        Assert.True(editor.SpecialSwitchModeEditable);
        Assert.True(editor.ShowSpecialLayerControls);
        Assert.Equal(LeaderSwitchMode.Press, editor.ActiveSpecialSwitchMode);
        Assert.Contains("唯一的临时层", editor.SpecialLayerHint);
        Assert.Contains("每次按下翻转", editor.SpecialLayerHint);

        // A leader that switches presets keeps its choice of mode, and a precision key has
        // none at all.
        editor.ActiveSpecialLeaderAction = LeaderAction.Preset;

        Assert.True(editor.ShowSpecialSwitchControls);
        Assert.True(editor.SpecialSwitchModeEditable);
        Assert.False(editor.ShowSpecialLayerControls);
        Assert.Empty(editor.SpecialLayerHint);

        editor.ActiveSpecialKind = SpecialKeyKind.Precision;

        Assert.False(editor.ShowSpecialSwitchControls);
        Assert.False(editor.ShowSpecialLayerControls);
    }

    [Fact]
    public void A_Layer_Leader_Always_Sits_On_The_Base_Layer()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Back };
        editor.SelectedLayer = 1;

        // Choosing the layer-switching action moves the mapping onto the base layer: a
        // temporary layer is entered from there, and the engine refuses the other shape.
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        editor.ActiveSpecialLeaderAction = LeaderAction.Layer;

        Assert.True(editor.LayerLeaderRequiresBaseLayer);
        Assert.Equal(0, editor.SelectedLayer);
        Assert.Contains("必须放在基础层", editor.LayerHint);

        // Asking for the temporary layer while the leader switches it is refused the same way.
        editor.SelectedLayer = 1;

        Assert.Equal(0, editor.SelectedLayer);
        Assert.Equal(0, editor.BuildEntry().Layer);

        // A preset leader is an ordinary mapping: it may sit on the temporary layer.
        editor.ActiveSpecialLeaderAction = LeaderAction.Preset;
        editor.SelectedLayer = 1;

        Assert.False(editor.LayerLeaderRequiresBaseLayer);
        Assert.Equal(1, editor.SelectedLayer);
        Assert.Equal(1, editor.BuildEntry().Layer);

        // Switching what it switches to the temporary layer snaps the mapping back onto the
        // base layer: the engine refuses a profile that switches the layer from inside it.
        editor.ActiveSpecialLeaderAction = LeaderAction.Layer;

        Assert.True(editor.LayerLeaderRequiresBaseLayer);
        Assert.Equal(0, editor.SelectedLayer);
        Assert.Equal(0, editor.BuildEntry().Layer);
    }

    [Fact]
    public void BuildEntry_Carries_The_Layer_And_LoadFrom_Restores_It()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.A };
        editor.SelectedLayer = 1;

        var entry = editor.BuildEntry();

        Assert.Equal(1, entry.Layer);

        var reloaded = new MappingEditorViewModel();
        reloaded.LoadFrom(entry);

        Assert.Equal(1, reloaded.SelectedLayer);
        Assert.Equal("临时层", reloaded.LayerDescription);
        Assert.Equal(1, reloaded.BuildEntry().Layer);
    }

    [Fact]
    public void Reset_Returns_The_Layer_To_The_Base_Layer()
    {
        var editor = new MappingEditorViewModel();
        editor.SelectedLayer = 3;

        editor.Reset();

        Assert.Equal(0, editor.SelectedLayer);
        Assert.Equal(0, editor.BuildEntry().Layer);
        Assert.Equal("基础层", editor.LayerDescription);
    }

    [Fact]
    public void A_Temporary_Layer_Survives_The_Store_And_Editor_Chain()
    {
        // The whole flow a user drives: a Hold layer leader, a few mappings placed on the
        // one temporary layer, saved, reloaded and committed through the editor the way
        // MainWindowViewModel does. The engine is the judge at the end: it builds the
        // round-tripped profile and runs the layer while the leader is held.
        var directory = Path.Combine(Path.GetTempPath(), "dezirojoy-layer-chain", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var profile = new GamepadProfile
            {
                Name = "层",
                DeviceId = "layer-chain",
                Layout = GamepadLayout.Xbox,
                Mappings =
                [
                    new MappingEntry
                    {
                        Source = new ButtonBinding(GamepadButton.Back),
                        Target = new SpecialKeyTarget(
                            SpecialKeyKind.Leader, SwitchMode: LeaderSwitchMode.Hold, Action: LeaderAction.Layer),
                    },
                    new MappingEntry
                    {
                        Source = new ButtonBinding(GamepadButton.A),
                        Target = new KeyPressTarget(GamepadKey.Space),
                        Layer = 1,
                    },
                    new MappingEntry
                    {
                        Source = new ButtonBinding(GamepadButton.B),
                        Target = new KeyPressTarget(GamepadKey.Enter),
                    },
                    new MappingEntry
                    {
                        Source = new ButtonBinding(GamepadButton.B),
                        Target = new KeyPressTarget(GamepadKey.Space),
                        Layer = 1,
                    },
                ],
            };

            new FileProfileStore(directory).Save(profile);

            var stored = new FileProfileStore(directory).Load("layer-chain");

            Assert.NotNull(stored);
            Assert.Equal([0], LayersOf(stored.Mappings, GamepadButton.Back));
            Assert.Equal([1], LayersOf(stored.Mappings, GamepadButton.A));
            Assert.Equal([0, 1], LayersOf(stored.Mappings, GamepadButton.B));

            var committed = stored.Mappings
                .Select(entry =>
                {
                    var editor = new MappingEditorViewModel();
                    editor.LoadFrom(entry);

                    // The way MainWindowViewModel commits an edit: the built entry, with the
                    // edited mapping's own id put back.
                    return editor.BuildEntry() with { Id = entry.Id };
                })
                .ToList();

            Assert.Equal([0], LayersOf(committed, GamepadButton.Back));
            Assert.Equal([1], LayersOf(committed, GamepadButton.A));
            Assert.Equal([0, 1], LayersOf(committed, GamepadButton.B));

            new FileProfileStore(directory).Save(stored with { Mappings = committed });

            var reloaded = new FileProfileStore(directory).Load("layer-chain");

            Assert.NotNull(reloaded);
            Assert.Equal([0, 1], LayersOf(reloaded.Mappings, GamepadButton.B));

            // The engine builds the round-tripped profile: the layer is named, the leader
            // sits on the base layer and holds, and the layer's own mappings run while it is
            // held - B taps Space, overriding the Enter the base layer carries.
            var engine = new MappingEngine(reloaded);

            Assert.Equal(
                [new KeyDownCommand(GamepadKey.Space, KeyModifiers.None)],
                engine.Tick(Held(GamepadButton.Back, GamepadButton.B), 1f / 64f));
            Assert.Equal(1, engine.ActiveLayerId);

            // Releasing the leader hands the button back to the base mapping and leaves the
            // layer behind.
            Assert.Equal(
                [
                    new KeyUpCommand(GamepadKey.Space, KeyModifiers.None),
                    new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None),
                ],
                engine.Tick(Held(GamepadButton.B), 1f / 64f));
            Assert.Equal(0, engine.ActiveLayerId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static int[] LayersOf(IReadOnlyList<MappingEntry> mappings, GamepadButton button)
            => [.. mappings
                .Where(mapping => mapping.Source is ButtonBinding binding && binding.Button == button)
                .Select(mapping => mapping.Layer)];

        static GamepadState Held(params GamepadButton[] buttons)
        {
            var mask = GamepadButtonMask.None;
            foreach (var button in buttons)
            {
                mask |= button.ToMask();
            }

            return GamepadState.Create(mask, 0f, 0f, 0f, 0f);
        }
    }

    [Fact]
    public void Equal_Leader_Slots_Are_A_Conflict_And_Different_Ones_Are_Not()
    {
        var editor = new MappingEditorViewModel();
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        editor.ActiveSpecialSlotA = 2;
        editor.ActiveSpecialSlotB = 2;

        Assert.True(editor.SpecialKeySlotConflict);

        editor.ActiveSpecialSlotB = 3;

        Assert.False(editor.SpecialKeySlotConflict);

        // A precision key names no slots, so equal ones are not a conflict there.
        editor.ActiveSpecialKind = SpecialKeyKind.Precision;
        editor.ActiveSpecialSlotB = 2;

        Assert.False(editor.SpecialKeySlotConflict);

        // A layer leader names no slots either, so equal ones are not a conflict there.
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        editor.ActiveSpecialLeaderAction = LeaderAction.Layer;
        editor.ActiveSpecialSlotA = 2;
        editor.ActiveSpecialSlotB = 2;

        Assert.False(editor.SpecialKeySlotConflict);
    }

    [Fact]
    public void The_Precision_Button_Readout_Names_The_Button_Semantically()
    {
        var editor = new MappingEditorViewModel();

        editor.ActivePrecisionButton = GamepadButton.B;

        Assert.Equal("B 键", editor.ActivePrecisionButtonLabel);

        editor.ActivePrecisionButton = GamepadButton.LeftStick;

        Assert.Equal("左摇杆按下", editor.ActivePrecisionButtonLabel);
    }

    // ---- 复杂快捷键: a DSL program the long press runs ------------------------

    [Fact]
    public void The_Complex_Shortcut_Kind_Is_Offered_On_Every_Gesture_Of_A_Button()
    {
        // Any of the three positions can carry a program - the position is the program's
        // trigger - so the kind is offered on every tab. A stick has no gestures at all, so
        // nothing can start a program there.
        var editor = new MappingEditorViewModel();

        Assert.Contains(MappingTargetKind.ComplexShortcut, editor.AvailableTargetKinds);

        editor.SelectedGestureKind = GestureKind.LongPress;

        Assert.Contains(MappingTargetKind.ComplexShortcut, editor.AvailableTargetKinds);

        editor.SelectedGestureKind = GestureKind.DoubleClick;

        Assert.Contains(MappingTargetKind.ComplexShortcut, editor.AvailableTargetKinds);

        editor.SourceIsStick = true;
        editor.SelectedGestureKind = GestureKind.LongPress;

        Assert.DoesNotContain(MappingTargetKind.ComplexShortcut, editor.AvailableTargetKinds);
    }

    [Fact]
    public void Choosing_The_Complex_Shortcut_Builds_The_Shape_The_Engine_Accepts()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.LeftBumper };
        editor.SelectedGestureKind = GestureKind.LongPress;
        editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;

        var entry = editor.BuildEntry();

        // The program rides in the gesture's long-press action; the click keeps a target
        // of its own rather than being swallowed by the program.
        Assert.Equal(new KeyPressTarget(GamepadKey.Enter), entry.Target);

        var gesture = Assert.IsType<ButtonGesture>(Assert.IsType<ButtonBinding>(entry.Source).Gesture);
        Assert.True(gesture.LongPressThreshold > 0f);
        Assert.Equal(
            new ComplexShortcutTarget { Script = "hold Alt\nevery 1s tap Tab" },
            gesture.LongPressAction);

        // The engine itself is the judge: building it refuses anything but that shape.
        _ = new MappingEngine(new GamepadProfile { Name = "complex", Mappings = [entry] });
    }

    [Fact]
    public void The_Complex_Shortcut_Round_Trips_Through_Build_And_Load()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Back };
        editor.SelectedGestureKind = GestureKind.LongPress;
        editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;
        editor.ActiveComplexShortcutScript = "hold Shift\ntap F5";

        var entry = editor.BuildEntry();

        var reloaded = new MappingEditorViewModel();
        reloaded.LoadFrom(entry);

        Assert.Equal(entry.Target, reloaded.BuildEntry().Target);
        Assert.Equal(entry.Source, reloaded.BuildEntry().Source);
        Assert.True(reloaded.LongPressRunsComplexShortcut);

        // The shape persists through the profile serializer unchanged: the program rides in
        // the gesture's action and the click keeps its own target, and the JSON is stable.
        var json = ProfileSerializer.Serialize(new GamepadProfile { Name = "complex", Mappings = [entry] });
        var back = ProfileSerializer.Deserialize(json);

        Assert.Contains("\"$type\": \"complexShortcut\"", json);
        Assert.Equal(
            new ComplexShortcutTarget { Script = "hold Shift\ntap F5" },
            Assert.IsType<ButtonGesture>(Assert.IsType<ButtonBinding>(back.Mappings[0].Source).Gesture).LongPressAction);
        Assert.Equal(entry.Target, back.Mappings[0].Target);
        Assert.Equal(json, ProfileSerializer.Serialize(back));

        // The program is authored on the long-press tab, which a reopened mapping opens on
        // only once the switcher says so - the same way a long-press action is reached.
        reloaded.SelectedGestureKind = GestureKind.LongPress;

        Assert.Equal(MappingTargetKind.ComplexShortcut, reloaded.ActiveTargetKind);
        Assert.Equal("hold Shift\ntap F5", reloaded.ActiveComplexShortcutScript);
    }

    [Fact]
    public void The_Script_Validates_Live_With_Chinese_Line_And_Column()
    {
        var editor = new MappingEditorViewModel();
        editor.SelectedGestureKind = GestureKind.LongPress;
        editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;

        editor.ActiveComplexShortcutScript = "hold Alt\nfrobnicate";

        Assert.True(editor.ComplexShortcutScriptInvalid);
        Assert.Contains("第 2 行", editor.ComplexShortcutScriptError);
        Assert.Contains("未知指令", editor.ComplexShortcutScriptError);
        Assert.Null(editor.ComplexShortcutScriptSummary);
        Assert.True(editor.HasComplexShortcutScriptError);
        Assert.False(editor.HasComplexShortcutScriptSummary);

        editor.ActiveComplexShortcutScript = "hold Alt\nevery 1s tap Tab";

        Assert.False(editor.ComplexShortcutScriptInvalid);
        Assert.Null(editor.ComplexShortcutScriptError);
        Assert.Equal("持续按住 Alt；每 1 秒点按 Tab", editor.ComplexShortcutScriptSummary);
        Assert.True(editor.HasComplexShortcutScriptSummary);
    }

    [Fact]
    public void The_Script_Draft_Survives_Switching_Between_The_Three_Gestures()
    {
        var editor = new MappingEditorViewModel();
        editor.SelectedGestureKind = GestureKind.LongPress;
        editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;
        editor.ActiveComplexShortcutScript = "hold Alt\ntap Tab";

        editor.SelectedGestureKind = GestureKind.Click;

        // The program keeps running while another tab is in view, and the draft is waiting
        // when the long press comes back.
        Assert.True(editor.LongPressRunsComplexShortcut);

        editor.SelectedGestureKind = GestureKind.LongPress;

        Assert.Equal("hold Alt\ntap Tab", editor.ActiveComplexShortcutScript);
        Assert.Equal(MappingTargetKind.ComplexShortcut, editor.ActiveTargetKind);
    }

    [Fact]
    public void The_Click_Keeps_Its_Own_Target_While_The_Program_Runs()
    {
        // The click is independent of the program: it is edited on its own draft, reads its
        // own target, and its parameters stay live - a quick press fires the click, not an
        // aborted program.
        var editor = new MappingEditorViewModel();
        editor.SelectedGestureKind = GestureKind.Click;
        editor.TargetKind = MappingTargetKind.KeyPress;
        editor.SelectedKey = GamepadKey.Space;

        editor.SelectedGestureKind = GestureKind.LongPress;
        editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;
        editor.ActiveComplexShortcutScript = "hold Alt\ntap Tab";

        editor.SelectedGestureKind = GestureKind.Click;

        Assert.False(editor.ClickIsNone);
        Assert.True(editor.ActiveTargetEditable);
        Assert.Equal("Space", editor.TargetDescription);
        Assert.Equal("Space", editor.ActiveTargetDescription);
        Assert.Equal(new KeyPressTarget(GamepadKey.Space), editor.BuildTarget());

        var entry = editor.BuildEntry();
        Assert.Equal(new KeyPressTarget(GamepadKey.Space), entry.Target);
        var gesture = Assert.IsType<ButtonGesture>(Assert.IsType<ButtonBinding>(entry.Source).Gesture);
        Assert.Equal(new ComplexShortcutTarget { Script = "hold Alt\ntap Tab" }, gesture.LongPressAction);

        _ = new MappingEngine(new GamepadProfile { Name = "complex", Mappings = [entry] });
    }

    [Fact]
    public void The_Long_Press_Tab_Names_The_Program_And_Refuses_To_Switch_It_Off()
    {
        var editor = new MappingEditorViewModel();
        editor.SelectedGestureKind = GestureKind.LongPress;
        editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;

        Assert.Equal("长按：复杂快捷键", editor.ActiveTargetDescription);
        Assert.False(editor.ActiveGestureToggleEnabled);

        // Switching the long press off would leave the program unreachable, so the request
        // is refused rather than written into a mapping the engine rejects.
        editor.ActiveGestureEnabled = false;

        Assert.True(editor.LongPressEnabled);
        Assert.True(editor.LongPressRunsComplexShortcut);
    }

    [Fact]
    public void The_Double_Click_Coexists_With_The_Program_But_The_Repeat_Does_Not()
    {
        var editor = new MappingEditorViewModel();
        editor.SelectedGestureKind = GestureKind.LongPress;
        editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;

        // The double-click is independent of the program: it can be enabled and carry an
        // action of its own.
        editor.SelectedGestureKind = GestureKind.DoubleClick;
        editor.ActiveGestureEnabled = true;
        editor.ActiveSelectedKey = GamepadKey.Space;

        var entry = editor.BuildEntry();
        var gesture = Assert.IsType<ButtonGesture>(Assert.IsType<ButtonBinding>(entry.Source).Gesture);

        Assert.True(gesture.HasDoubleClick);
        Assert.Equal(new KeyPressTarget(GamepadKey.Space), gesture.DoubleClickAction);
        Assert.Equal(
            new ComplexShortcutTarget { Script = "hold Alt\nevery 1s tap Tab" },
            gesture.LongPressAction);

        // Click auto-repeat makes the long press inert, so the program could never start:
        // the request is refused rather than paired with the program.
        editor.SelectedGestureKind = GestureKind.Click;
        editor.ClickRepeatEnabled = true;

        Assert.False(editor.ClickRepeatEnabled);

        _ = new MappingEngine(new GamepadProfile { Name = "complex", Mappings = [entry] });
    }

    [Fact]
    public void A_v1_Program_Loads_As_The_Long_Press_Action_With_A_No_Function_Click()
    {
        // A profile written before the program became the long press's own action carries
        // it as the mapping's target; loading it translates the shape - the program becomes
        // the long press's action and the click reads 无功能, which is what a quick press
        // did when the program owned the target. Gestures the engine would refuse (a
        // long-press repeat here) are repaired away.
        var editor = new MappingEditorViewModel();
        editor.LoadFrom(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.X)
            {
                Gesture = new ButtonGesture
                {
                    ClickRepeatInterval = 0.10f,
                    LongPressThreshold = 0.50f,
                    LongPressRepeatInterval = 0.10f,
                },
            },
            Target = new ComplexShortcutTarget { Script = "hold Alt" },
        });

        Assert.True(editor.LongPressRunsComplexShortcut);
        Assert.False(editor.ClickRepeatEnabled);
        Assert.True(editor.LongPressEnabled);
        Assert.True(editor.ClickIsNone);

        var entry = editor.BuildEntry();
        Assert.Equal(new NoneTarget(), entry.Target);
        var gesture = Assert.IsType<ButtonGesture>(Assert.IsType<ButtonBinding>(entry.Source).Gesture);

        Assert.Equal(0f, gesture.LongPressRepeatInterval);
        Assert.Equal(new ComplexShortcutTarget { Script = "hold Alt" }, gesture.LongPressAction);

        _ = new MappingEngine(new GamepadProfile { Name = "complex", Mappings = [entry] });
    }

    [Fact]
    public void Cancelling_A_Complex_Shortcut_Leaves_Nothing_Behind()
    {
        // The editor is reused, so a cancelled program must not bleed into the next edit:
        // the draft is pristine again and the kind is off the click's list.
        var editor = new MappingEditorViewModel();
        editor.SelectedGestureKind = GestureKind.LongPress;
        editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;
        editor.ActiveComplexShortcutScript = "hold Alt\ntap Tab";

        editor.Reset();

        Assert.False(editor.LongPressRunsComplexShortcut);
        Assert.Null(editor.ComplexShortcutScriptError);
        Assert.Null(editor.ComplexShortcutScriptSummary);
        Assert.Equal(MappingTargetKind.KeyPress, editor.TargetKind);

        // The kind stays on the list - every gesture of a button can carry a program - but
        // the draft it left behind is gone.
        Assert.Contains(MappingTargetKind.ComplexShortcut, editor.AvailableTargetKinds);
        Assert.False(editor.ShowActionStrategy);
    }

    // ---- Special-key conflict hints while editing ------------------------------

    [Fact]
    public void A_Fresh_Draft_Collides_With_Nothing()
    {
        var editor = new MappingEditorViewModel();

        Assert.False(editor.HasSpecialKeyConflict);
        Assert.Null(editor.SpecialKeyConflictHint);
        Assert.Empty(editor.DraftSpecialKeyConflicts);
    }

    [Fact]
    public void A_Leader_Draft_With_A_Double_Click_Gesture_Says_So_Live()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Back };
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;

        Assert.False(editor.HasSpecialKeyConflict);

        editor.DoubleClickEnabled = true;

        Assert.True(editor.HasSpecialKeyConflict);
        Assert.Contains("Leader 键", editor.SpecialKeyConflictHint!);
        Assert.Contains("双击窗口", editor.SpecialKeyConflictHint!);

        // Turning the gesture back off clears the hint: the report follows the draft.
        editor.DoubleClickEnabled = false;

        Assert.False(editor.HasSpecialKeyConflict);
        Assert.Null(editor.SpecialKeyConflictHint);
    }

    [Fact]
    public void A_Leader_Draft_With_Click_Repeat_Says_So_Live()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Back };
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;

        editor.ClickRepeatEnabled = true;

        Assert.True(editor.HasSpecialKeyConflict);
        Assert.Contains("长按重复触发", editor.SpecialKeyConflictHint!);
    }

    [Fact]
    public void Turning_Click_Repeat_Off_Clears_The_Hint_It_Raised()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Back };
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;

        editor.ClickRepeatEnabled = true;
        Assert.True(editor.HasSpecialKeyConflict);

        // The user resolved the collision by switching the repeat off: the hint must not
        // outlive the edit that removed it.
        editor.ClickRepeatEnabled = false;

        Assert.False(editor.HasSpecialKeyConflict);
        Assert.Null(editor.SpecialKeyConflictHint);
        Assert.Empty(editor.DraftSpecialKeyConflicts);
    }

    [Fact]
    public void Editing_The_Click_Repeat_Interval_Updates_The_Hint()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Back };
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        editor.ClickRepeatEnabled = true;

        Assert.Contains("0.1 秒", editor.SpecialKeyConflictHint!);

        editor.ClickRepeatIntervalSeconds = 0.5;

        Assert.Contains("0.5 秒", editor.SpecialKeyConflictHint!);
        Assert.DoesNotContain("0.1 秒", editor.SpecialKeyConflictHint!);
    }

    [Fact]
    public void A_Leader_Draft_With_A_Bare_Long_Press_Repeat_Says_So_Live()
    {
        var editor = new MappingEditorViewModel();

        // The repeat interval is not editor-editable, but a profile that carries one
        // round-trips it through the draft, and the hint must name it as it will behave.
        editor.LoadFrom(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.Back)
            {
                Gesture = new ButtonGesture
                {
                    LongPressThreshold = ButtonGesture.DefaultLongPressThreshold,
                    LongPressRepeatInterval = 0.2f,
                },
            },
            Target = new SpecialKeyTarget(SpecialKeyKind.Leader),
        });

        Assert.True(editor.HasSpecialKeyConflict);
        Assert.Contains("重复间隔", editor.SpecialKeyConflictHint!);
        Assert.Contains("0.2 秒", editor.SpecialKeyConflictHint!);
    }

    [Fact]
    public void The_Draft_Is_Measured_Against_The_Profiles_Other_Mappings()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Back };
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;

        // The window view model hands the other mappings in when the edit opens; the entry
        // being edited is excluded, so a draft is never measured against itself.
        editor.OtherMappings =
        [
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Back),
                Target = new KeyPressTarget(GamepadKey.Enter),
            },
        ];

        Assert.True(editor.HasSpecialKeyConflict);
        Assert.Contains("另一条映射", editor.SpecialKeyConflictHint!);
        Assert.Contains("Enter", editor.SpecialKeyConflictHint!);
    }

    [Fact]
    public void Moving_The_Draft_To_Another_Button_Re_Measures_Against_That_Button()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Back };
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        editor.OtherMappings =
        [
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Back),
                Target = new KeyPressTarget(GamepadKey.Enter),
            },
        ];

        Assert.True(editor.HasSpecialKeyConflict);

        // The conflicting mapping sits on Back, so arming the draft for A clears the hint.
        editor.SelectedButton = GamepadButton.A;

        Assert.False(editor.HasSpecialKeyConflict);
    }

    [Fact]
    public void A_Precision_Draft_On_A_Gesture_Position_Says_It_Will_Not_Engage()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.LeftStick };
        editor.ActiveTargetKind = MappingTargetKind.KeyPress;
        editor.SelectedGestureKind = GestureKind.DoubleClick;
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Precision;

        Assert.True(editor.HasSpecialKeyConflict);
        Assert.Contains("不会生效", editor.SpecialKeyConflictHint!);
    }

    [Fact]
    public void A_Double_Click_Leader_Draft_Is_Measured_Against_The_Same_Button()
    {
        // The normal editor flow reaches a gesture-position leader: switch to the 双击 tab,
        // pick 特殊键, pick Leader 键. A sibling on the same button fires beside it.
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Back };
        editor.OtherMappings =
        [
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Back),
                Target = new KeyPressTarget(GamepadKey.Enter),
            },
        ];
        editor.SelectedGestureKind = GestureKind.DoubleClick;
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;

        Assert.True(editor.HasSpecialKeyConflict);
        Assert.Contains("双击触发「Leader 键」", editor.SpecialKeyConflictHint!);
        Assert.Contains("两次按下都会触发Enter", editor.SpecialKeyConflictHint!);
    }

    [Fact]
    public void A_Long_Press_Leader_Draft_Is_Measured_Against_The_Same_Button()
    {
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Back };
        editor.OtherMappings =
        [
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Back),
                Target = new KeyPressTarget(GamepadKey.Enter),
            },
        ];
        editor.SelectedGestureKind = GestureKind.LongPress;
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;

        Assert.True(editor.HasSpecialKeyConflict);
        Assert.Contains("长按触发「Leader 键」", editor.SpecialKeyConflictHint!);
        Assert.Contains("Enter一直被按住", editor.SpecialKeyConflictHint!);
    }

    [Fact]
    public void A_Leader_Draft_Sees_A_Siblings_Gesture_Action_Too()
    {
        // The sibling's own click is 无功能, but its double-click is a real key: the second
        // press fires the switch and the key together.
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Back };
        editor.OtherMappings =
        [
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Back)
                {
                    Gesture = new ButtonGesture
                    {
                        DoubleClickWindow = ButtonGesture.DefaultDoubleClickWindow,
                        DoubleClickAction = new KeyPressTarget(GamepadKey.A),
                    },
                },
                Target = new NoneTarget(),
            },
        ];
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;

        Assert.True(editor.HasSpecialKeyConflict);
        Assert.Contains("双击：A", editor.SpecialKeyConflictHint!);
    }

    [Fact]
    public void A_Long_Press_Leader_Draft_Meets_A_Siblings_Double_Click_In_A_Tap_Then_Hold()
    {
        // The default editor timings reach the compound interaction: a tap, a release, then a
        // second press held past the long-press threshold fires the sibling's double-click on
        // the press edge and the leader switch at the threshold.
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Back };
        editor.OtherMappings =
        [
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Back)
                {
                    Gesture = new ButtonGesture
                    {
                        DoubleClickWindow = ButtonGesture.DefaultDoubleClickWindow,
                        DoubleClickAction = new KeyPressTarget(GamepadKey.A),
                    },
                },
                Target = new NoneTarget(),
            },
        ];
        editor.SelectedGestureKind = GestureKind.LongPress;
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;

        Assert.True(editor.HasSpecialKeyConflict);
        Assert.Contains("双击触发A后", editor.SpecialKeyConflictHint!);
        Assert.Contains("还会触发「Leader 键」切换预设", editor.SpecialKeyConflictHint!);
    }

    [Fact]
    public void A_Long_Press_Leader_Draft_With_Its_Own_Double_Click_Reports_Nothing()
    {
        // The M46 coexistence shape through the normal editor flow: the long-press slot
        // carries the Leader and the double-click slot is enabled too, so the draft's own
        // double-click resolves on the second press edge and the long-press cycle the
        // compound needs never starts - no interaction fires the pair beside the sibling.
        var editor = new MappingEditorViewModel { SelectedButton = GamepadButton.Back };
        editor.OtherMappings =
        [
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Back)
                {
                    Gesture = new ButtonGesture
                    {
                        DoubleClickWindow = ButtonGesture.DefaultDoubleClickWindow,
                        DoubleClickAction = new KeyPressTarget(GamepadKey.A),
                    },
                },
                Target = new NoneTarget(),
            },
        ];
        editor.SelectedGestureKind = GestureKind.LongPress;
        editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        editor.SelectedGestureKind = GestureKind.DoubleClick;
        editor.ActiveGestureEnabled = true;

        Assert.False(editor.HasSpecialKeyConflict);
        Assert.Null(editor.SpecialKeyConflictHint);
    }
}
