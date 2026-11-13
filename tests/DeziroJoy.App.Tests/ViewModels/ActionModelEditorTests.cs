using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The editor side of the shared action model: the text target, the strategy fields the GUI
/// and the DSL share, the press/release preview, and the two bug-fix follow-ups - a target
/// this build does not know must survive an edit round trip, and the on-screen keyboard must
/// type with the gamepad alone.
/// </summary>
public class ActionModelEditorTests
{
    private static MappingEntry Entry(MappingTarget target, InputBinding? source = null)
        => new() { Source = source ?? new ButtonBinding(GamepadButton.A), Target = target };

    // ---- 输入文本 ----------------------------------------------------------------

    [Fact]
    public void A_Text_Target_Is_Offered_For_Both_Source_Kinds()
    {
        var button = new MappingEditorViewModel();
        Assert.Contains(MappingTargetKind.Text, button.AvailableTargetKinds);

        var stick = new MappingEditorViewModel { SourceKind = MappingSourceKind.Stick };
        Assert.Contains(MappingTargetKind.Text, stick.AvailableTargetKinds);
    }

    [Fact]
    public void The_Text_Draft_Round_Trips_Through_The_Slot()
    {
        var editor = new MappingEditorViewModel
        {
            TargetKind = MappingTargetKind.Text,
            ActiveText = "open folder",
        };

        Assert.Equal(MappingTargetKind.Text, editor.TargetKind);
        Assert.Equal("open folder", editor.ActiveText);
        Assert.Equal(11, editor.ActiveTextLength);
        Assert.Equal(new TextTarget("open folder"), editor.BuildTarget());
    }

    [Fact]
    public void A_Loaded_Text_Target_Fills_The_Draft()
    {
        var editor = new MappingEditorViewModel();

        editor.LoadFrom(Entry(new TextTarget("search term")));

        Assert.Equal(MappingTargetKind.Text, editor.TargetKind);
        Assert.Equal("search term", editor.ActiveText);
        Assert.Equal(new TextTarget("search term"), editor.BuildTarget());
    }

    [Fact]
    public void A_Text_Draft_Is_Clamped_To_The_Cores_Own_Bound()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.Text };

        editor.ActiveText = new string('x', GestureSlotEditor.TextMaxLength * 2);

        Assert.Equal(GestureSlotEditor.TextMaxLength, editor.ActiveText.Length);
        Assert.Equal(GestureSlotEditor.TextMaxLength, ((TextTarget)editor.BuildTarget()).Text.Length);
    }

    [Fact]
    public void A_Stick_Driving_A_Text_Target_Asks_For_A_Direction()
    {
        // The engine refuses a directionless stick driving a digital target, text included,
        // so the picker has to appear - while a stick-native target needs none.
        var editor = new MappingEditorViewModel { SourceKind = MappingSourceKind.Stick };

        editor.TargetKind = MappingTargetKind.MouseMove;
        Assert.False(editor.NeedsDirection);

        editor.TargetKind = MappingTargetKind.Text;

        Assert.True(editor.NeedsDirection);
        Assert.True(editor.ShowDirectionPicker);
    }

    [Fact]
    public void The_Key_Picker_Names_The_Media_Keys_In_Chinese()
    {
        // The shared description table was written before the media keys existed, so the
        // picker names them itself rather than showing an enum name.
        var editor = new MappingEditorViewModel();
        editor.SelectedKey = GamepadKey.VolumeUp;

        Assert.Equal("音量加", Assert.Single(editor.KeyOptions, option => option.Key == GamepadKey.VolumeUp).Label);

        editor.SelectedKey = GamepadKey.BrowserRefresh;

        Assert.Equal("刷新", Assert.Single(editor.KeyOptions, option => option.Key == GamepadKey.BrowserRefresh).Label);
    }

    // ---- 触发方式 / 执行方式 / 重复方式 ------------------------------------------

    [Fact]
    public void The_Strategy_Pickers_Read_The_Slot_The_Switcher_Has_Selected()
    {
        var editor = new MappingEditorViewModel();

        Assert.Equal(ActionTrigger.Press, editor.ActiveActionTrigger);
        Assert.Equal(ActionExecution.WhileHeld, editor.ActiveActionExecution);
        Assert.Equal(ActionRepeat.Restart, editor.ActiveActionRepeat);

        editor.SelectedGestureKind = GestureKind.LongPress;

        Assert.Equal(ActionTrigger.LongPress, editor.ActiveActionTrigger);
        Assert.Single(editor.ActiveActionTriggerOptions);

        editor.SelectedGestureKind = GestureKind.Click;

        // The click position is the one that answers two triggers.
        Assert.Equal(2, editor.ActiveActionTriggerOptions.Count);
        Assert.True(editor.ActionTriggerEditable);
    }

    [Fact]
    public void A_Release_Trigger_Moves_The_Execution_To_Once_By_Itself()
    {
        var editor = new MappingEditorViewModel();
        editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;
        editor.ActiveActionExecution = ActionExecution.WhileHeld;

        editor.ActiveActionTrigger = ActionTrigger.Release;

        Assert.Equal(ActionTrigger.Release, editor.ActiveActionTrigger);
        Assert.Equal(ActionExecution.Once, editor.ActiveActionExecution);
    }

    [Fact]
    public void A_Held_Run_Refuses_A_Queued_Repeat_In_The_Picker()
    {
        var editor = new MappingEditorViewModel();
        editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;
        editor.ActiveActionExecution = ActionExecution.Once;
        editor.ActiveActionRepeat = ActionRepeat.Queue;

        editor.ActiveActionExecution = ActionExecution.WhileHeld;

        Assert.All(
            editor.AvailableActionRepeatOptions,
            option => Assert.NotEqual(ActionRepeat.Queue, option.Value));
        Assert.Equal(ActionRepeat.Restart, editor.ActiveActionRepeat);
    }

    [Fact]
    public void A_Complex_Shortcut_Kind_Is_Offered_On_Every_Gesture_Of_A_Button()
    {
        var editor = new MappingEditorViewModel();

        foreach (var gesture in new[] { GestureKind.Click, GestureKind.DoubleClick, GestureKind.LongPress })
        {
            editor.SelectedGestureKind = gesture;
            Assert.Contains(MappingTargetKind.ComplexShortcut, editor.AvailableTargetKinds);
        }

        // A stick has no gestures, so nothing can start a program there.
        editor.SourceKind = MappingSourceKind.Stick;
        Assert.DoesNotContain(MappingTargetKind.ComplexShortcut, editor.AvailableTargetKinds);
    }

    [Fact]
    public void The_Strategy_Fields_Round_Trip_Through_The_Profile()
    {
        var editor = new MappingEditorViewModel();
        editor.SelectedGestureKind = GestureKind.LongPress;
        editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;
        editor.ActiveComplexShortcutScript = "tap A";
        editor.ActiveActionExecution = ActionExecution.Toggle;
        editor.ActiveActionRepeat = ActionRepeat.Ignore;

        // The program is the long press's own action, so it rides in the gesture rather
        // than in the mapping's own target.
        var action = Assert.IsType<ComplexShortcutTarget>(editor.LongPressAction);
        Assert.Equal(ActionTrigger.LongPress, action.Trigger);
        Assert.Equal(ActionExecution.Toggle, action.Execution);
        Assert.Equal(ActionRepeat.Ignore, action.Repeat);
    }

    [Fact]
    public void A_Loaded_Program_Fills_The_Strategy_Fields()
    {
        var editor = new MappingEditorViewModel();

        editor.LoadFrom(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.A)
            {
                Gesture = new ButtonGesture
                {
                    LongPressThreshold = 0.5f,
                    LongPressAction = new ComplexShortcutTarget
                    {
                        Script = "tap A",
                        Execution = ActionExecution.Once,
                        Repeat = ActionRepeat.Queue,
                    },
                },
            },
            Target = new NoneTarget(),
        });

        editor.SelectedGestureKind = GestureKind.LongPress;

        Assert.Equal(ActionExecution.Once, editor.ActiveActionExecution);
        Assert.Equal(ActionRepeat.Queue, editor.ActiveActionRepeat);
    }

    [Fact]
    public void A_Press_Triggered_Program_Stays_On_The_Click_Tab()
    {
        // A program whose trigger is the click is the mapping's own target, so it loads
        // into the click draft rather than being moved to the long press.
        var editor = new MappingEditorViewModel();

        editor.LoadFrom(Entry(new ComplexShortcutTarget
        {
            Script = "tap A",
            Trigger = ActionTrigger.Press,
            Execution = ActionExecution.Once,
        }));

        Assert.Equal(MappingTargetKind.ComplexShortcut, editor.TargetKind);
        Assert.Equal(ActionTrigger.Press, editor.ActiveActionTrigger);
        Assert.Equal(new ComplexShortcutTarget
        {
            Script = "tap A",
            Trigger = ActionTrigger.Press,
            Execution = ActionExecution.Once,
        }, editor.BuildTarget());
    }

    [Fact]
    public void The_Preview_Names_The_Press_And_Release_Order()
    {
        var editor = new MappingEditorViewModel();
        editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;
        editor.ActiveComplexShortcutScript = "hold Alt\ntap Tab\nclick right\ntext hi";

        Assert.Equal(
            [
                "按下 Alt",
                "按下 Tab",
                "松开 Tab",
                "按住鼠标右键",
                "松开鼠标右键",
                "输入「hi」",
                "松开 Alt",
            ],
            editor.ActiveActionPreview);
    }

    [Fact]
    public void The_Preview_Is_Empty_While_The_Script_Does_Not_Parse()
    {
        var editor = new MappingEditorViewModel();
        editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;
        editor.ActiveComplexShortcutScript = "hold Alt\nfrobnicate";

        Assert.True(editor.ComplexShortcutScriptInvalid);
        Assert.Empty(editor.ActiveActionPreview);
    }

    // ---- 不支持的映射 ------------------------------------------------------------

    [Fact]
    public void An_Unknown_Target_Is_Shown_As_Unsupported_And_Kept_On_Commit()
    {
        var unknown = new UnknownTarget("wand", """{"$type":"wand","Wave":3}""");
        var editor = new MappingEditorViewModel();

        editor.LoadFrom(Entry(unknown));

        Assert.True(editor.ActiveTargetUnsupported);
        Assert.Contains("wand", editor.ActiveTargetUnsupportedDescription!);
        Assert.Contains("不支持", editor.ActiveTargetUnsupportedDescription!);

        // The commit writes the unknown target back out untouched, so nothing the other
        // build put there is lost.
        Assert.Equal(unknown, editor.BuildTarget());
        Assert.Equal(unknown, editor.BuildEntry().Target);
    }

    [Fact]
    public void An_Unknown_Gesture_Action_Is_Kept_Too()
    {
        var unknown = new UnknownTarget("spell", """{"$type":"spell"}""");
        var editor = new MappingEditorViewModel();

        editor.LoadFrom(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.A)
            {
                Gesture = new ButtonGesture
                {
                    LongPressThreshold = 0.5f,
                    LongPressAction = unknown,
                },
            },
            Target = new NoneTarget(),
        });

        editor.SelectedGestureKind = GestureKind.LongPress;

        Assert.True(editor.ActiveTargetUnsupported);
        Assert.Equal(unknown, editor.LongPressAction);
    }

    [Fact]
    public void Choosing_A_Target_Replaces_The_Unknown_One()
    {
        var editor = new MappingEditorViewModel();

        editor.LoadFrom(Entry(new UnknownTarget("wand", """{"$type":"wand"}""")));
        Assert.True(editor.ActiveTargetUnsupported);

        editor.TargetKind = MappingTargetKind.KeyPress;
        editor.SelectedKey = GamepadKey.F4;

        Assert.False(editor.ActiveTargetUnsupported);
        Assert.Equal(new KeyPressTarget(GamepadKey.F4), editor.BuildTarget());
    }

    [Fact]
    public void An_Unknown_Target_Reads_As_Unsupported_In_The_Header()
    {
        var editor = new MappingEditorViewModel();

        editor.LoadFrom(Entry(new UnknownTarget("wand", """{"$type":"wand"}""")));

        Assert.Contains("不支持", editor.TargetDescription);
        Assert.Contains("不支持", editor.ActiveTargetDescription);
        Assert.Contains("原样保留", editor.UnsupportedTargetHint);
    }

    // ---- 屏幕键盘 ----------------------------------------------------------------

    [Fact]
    public void The_Keyboard_Types_A_Character_Where_The_Cursor_Sits()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.Text };

        Assert.True(editor.ShowTextKeyboard);

        editor.ActivateTextKeyboardKey();

        // A fresh keyboard opens on the first key of the first page.
        Assert.Equal("a", editor.ActiveText);
    }

    [Fact]
    public void The_Cursor_Moves_And_Stops_At_The_Edges()
    {
        var keyboard = new TextKeyboard();

        Assert.False(keyboard.Move(InputDirection.Up));
        Assert.False(keyboard.Move(InputDirection.Left));
        Assert.Equal("a", keyboard.FocusedKey!.Label);

        Assert.True(keyboard.Move(InputDirection.Right));
        Assert.Equal("b", keyboard.FocusedKey!.Label);

        Assert.True(keyboard.Move(InputDirection.Down));
        Assert.Equal("k", keyboard.FocusedKey!.Label);
    }

    [Fact]
    public void The_Cursor_Clamps_To_A_Shorter_Row()
    {
        var keyboard = new TextKeyboard();

        keyboard.Move(InputDirection.Right);
        keyboard.Move(InputDirection.Right);
        Assert.Equal("c", keyboard.FocusedKey!.Label);

        // The action row is shorter than the letter rows, so the column is pulled back to
        // a key the row actually has - 清空 at column 2, not one past its end.
        keyboard.Move(InputDirection.Down);
        keyboard.Move(InputDirection.Down);
        keyboard.Move(InputDirection.Down);

        Assert.Equal("清空", keyboard.FocusedKey!.Label);
        Assert.Equal(TextKeyboardAction.Clear, keyboard.FocusedKey!.Action);
    }

    [Fact]
    public void The_Action_Keys_Edit_The_Draft()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.Text };

        editor.ActivateTextKeyboardKey();
        editor.ActivateTextKeyboardKey();
        Assert.Equal("aa", editor.ActiveText);

        editor.ApplyTextKeyboardKey(new TextKeyboardKey("退格", null, TextKeyboardAction.Backspace));
        Assert.Equal("a", editor.ActiveText);

        editor.ApplyTextKeyboardKey(new TextKeyboardKey("空格", " ", TextKeyboardAction.Space));
        Assert.Equal("a ", editor.ActiveText);

        editor.ApplyTextKeyboardKey(new TextKeyboardKey("清空", null, TextKeyboardAction.Clear));
        Assert.Empty(editor.ActiveText);
    }

    [Fact]
    public void The_Common_Page_Types_Whole_Fragments()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.Text };
        var common = editor.TextKeyboard.Pages.First(page => page.Name == "常用");

        editor.TextKeyboard.ShowPage(common);
        editor.ActivateTextKeyboardKey();

        Assert.Equal("https://", editor.ActiveText);
    }

    [Fact]
    public void The_Keyboard_Stops_Typing_At_The_Cores_Bound()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.Text };
        var fragment = new TextKeyboardKey("长文本", new string('y', GestureSlotEditor.TextMaxLength));

        editor.ApplyTextKeyboardKey(fragment);

        Assert.Equal(GestureSlotEditor.TextMaxLength, editor.ActiveText.Length);

        editor.ApplyTextKeyboardKey(fragment);

        Assert.Equal(GestureSlotEditor.TextMaxLength, editor.ActiveText.Length);
    }

    [Fact]
    public void The_Keyboard_Is_Only_Offered_For_A_Text_Target()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.KeyPress };

        Assert.False(editor.ShowTextKeyboard);

        editor.TargetKind = MappingTargetKind.Text;

        Assert.True(editor.ShowTextKeyboard);
    }

    // ---- 屏幕键盘的手柄输入 ------------------------------------------------------

    /// <summary>One sample of a pad holding <paramref name="buttons"/> and nothing else.</summary>
    private static GamepadState Pad(GamepadButtonMask buttons)
        => GamepadState.Create(buttons, 0f, 0f, 0f, 0f);

    [Fact]
    public void The_Gamepad_Moves_The_Cursor_And_Presses_The_Focused_Key()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.Text };
        editor.TextKeyboardGamepadInScope = true;

        Assert.True(editor.CaptureGamepadSample(Pad(GamepadButtonMask.DPadRight)));
        Assert.Equal("b", editor.TextKeyboard.FocusedKey!.Label);

        Assert.True(editor.CaptureGamepadSample(Pad(GamepadButtonMask.DPadDown)));
        Assert.Equal("k", editor.TextKeyboard.FocusedKey!.Label);

        // A press is an edge, not a hold: the same button held through the next sample moves
        // nothing, so a held D-pad walks the cursor one key per press rather than per sample.
        Assert.False(editor.CaptureGamepadSample(Pad(GamepadButtonMask.DPadDown)));
        Assert.Equal("k", editor.TextKeyboard.FocusedKey!.Label);

        Assert.True(editor.CaptureGamepadSample(Pad(GamepadButtonMask.A)));
        Assert.Equal("k", editor.ActiveText);
    }

    [Fact]
    public void The_Gamepad_Bumpers_Turn_The_Page()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.Text };
        editor.TextKeyboardGamepadInScope = true;

        // 小写 is the first page, so a left bumper wraps around to the last one - and the page
        // a bumper shows opens on its own first key, exactly as a press on its tab does.
        Assert.True(editor.CaptureGamepadSample(Pad(GamepadButtonMask.LeftBumper)));
        Assert.Equal("常用", editor.TextKeyboard.ActivePage.Name);
        Assert.Equal("https://", editor.TextKeyboard.FocusedKey!.Label);

        Assert.True(editor.CaptureGamepadSample(Pad(GamepadButtonMask.RightBumper)));
        Assert.Equal("小写", editor.TextKeyboard.ActivePage.Name);
        Assert.Equal("a", editor.TextKeyboard.FocusedKey!.Label);
    }

    [Fact]
    public void A_Push_At_The_Cursors_Edge_Is_Left_To_Navigation()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.Text };
        editor.TextKeyboardGamepadInScope = true;

        // The cursor sits on the first key of the first page, so neither an up nor a left push
        // can move it: the keyboard spends nothing and reports the press unused, which is how
        // the window's own gamepad navigation still answers it.
        Assert.False(editor.CaptureGamepadSample(Pad(GamepadButtonMask.DPadUp)));
        Assert.False(editor.CaptureGamepadSample(Pad(GamepadButtonMask.DPadLeft)));

        Assert.Equal("a", editor.TextKeyboard.FocusedKey!.Label);
        Assert.Empty(editor.ActiveText);
    }

    [Fact]
    public void The_Keyboard_Answers_The_Pad_Only_While_It_Is_In_Scope()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.Text };

        // Out of scope - the editor closed, the window left the mapping tab or its navigation
        // toggle is off - the draft keeps its keyboard and the pad keeps its own business.
        Assert.False(editor.CaptureGamepadSample(Pad(GamepadButtonMask.DPadRight)));
        Assert.Equal("a", editor.TextKeyboard.FocusedKey!.Label);

        // The press is released and the editor comes into scope: the same push, now answered.
        editor.CaptureGamepadSample(Pad(GamepadButtonMask.None));
        editor.TextKeyboardGamepadInScope = true;

        Assert.True(editor.CaptureGamepadSample(Pad(GamepadButtonMask.DPadRight)));
        Assert.Equal("b", editor.TextKeyboard.FocusedKey!.Label);
    }

    [Fact]
    public void A_Target_That_Is_Not_Text_Leaves_The_Pad_Alone()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.KeyPress };
        editor.TextKeyboardGamepadInScope = true;

        Assert.False(editor.ShowTextKeyboard);
        Assert.False(editor.CaptureGamepadSample(Pad(GamepadButtonMask.DPadRight)));

        Assert.Equal("a", editor.TextKeyboard.FocusedKey!.Label);
        Assert.Empty(editor.ActiveText);
    }

    [Fact]
    public void An_Armed_Capture_Names_The_Source_Before_The_Keyboard_Answers()
    {
        var editor = new MappingEditorViewModel { TargetKind = MappingTargetKind.Text };
        editor.TextKeyboardGamepadInScope = true;
        editor.IsCapturingButton = true;

        // A capture is the explicit "this press is the source" request, so it is answered
        // first: the D-pad becomes the mapping's own source and the keyboard never sees the
        // press, which is what keeps a D-pad source assignable on a text target.
        Assert.True(editor.CaptureGamepadSample(Pad(GamepadButtonMask.DPadRight)));

        Assert.Equal(GamepadButton.DPadRight, editor.SelectedButton);
        Assert.False(editor.IsCapturingButton);
        Assert.Equal("a", editor.TextKeyboard.FocusedKey!.Label);
        Assert.Empty(editor.ActiveText);
    }
}
