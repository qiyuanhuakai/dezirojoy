using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using CoreKeyModifiers = DeziroJoy.Core.Mapping.KeyModifiers;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The floating screen keyboard's model: the cursor and the pages it reuses, and the two
/// requests it makes. It types into the focused application rather than into a draft, so what
/// a test asserts is what would have gone out - the strings and the key taps - beside the
/// preview that mirrors them.
/// </summary>
public sealed class ScreenKeyboardViewModelTests
{
    private static ScreenKeyboardViewModel Build(
        out List<string> typed,
        out List<(GamepadKey Key, CoreKeyModifiers Modifiers)> tapped)
    {
        var typedText = new List<string>();
        var tappedKeys = new List<(GamepadKey Key, CoreKeyModifiers Modifiers)>();
        var keyboard = new ScreenKeyboardViewModel(
            text =>
            {
                typedText.Add(text);
                return Task.CompletedTask;
            },
            (key, modifiers) =>
            {
                tappedKeys.Add((key, modifiers));
                return Task.CompletedTask;
            });

        typed = typedText;
        tapped = tappedKeys;
        return keyboard;
    }

    [Fact]
    public void A_Fresh_Keyboard_Starts_Closed_On_The_First_Key_Of_The_First_Page()
    {
        var keyboard = Build(out _, out _);

        Assert.False(keyboard.IsOpen);
        Assert.Equal("小写", keyboard.Keyboard.ActivePage.Name);
        Assert.Equal("a", keyboard.Keyboard.FocusedKey!.Label);
        Assert.Empty(keyboard.TypedText);
    }

    [Fact]
    public void The_Dpad_Moves_The_Cursor_And_A_Presses_The_Key_It_Is_On()
    {
        var keyboard = Build(out var typed, out _);

        keyboard.Navigate(GamepadButtonMask.DPadRight);
        Assert.Equal("b", keyboard.Keyboard.FocusedKey!.Label);

        keyboard.Navigate(GamepadButtonMask.DPadDown);
        Assert.Equal("k", keyboard.Keyboard.FocusedKey!.Label);

        keyboard.Navigate(GamepadButtonMask.A);

        // The keystroke goes out to the focused application, and the preview follows it.
        Assert.Equal(["k"], typed);
        Assert.Equal("k", keyboard.TypedText);
    }

    [Fact]
    public void The_Bumpers_Turn_The_Page_And_The_Cursor_Returns_To_Its_First_Key()
    {
        var keyboard = Build(out var typed, out _);

        // 小写 is the first page, so a left bumper wraps around to the last one.
        keyboard.Navigate(GamepadButtonMask.LeftBumper);
        Assert.Equal("常用", keyboard.Keyboard.ActivePage.Name);
        Assert.Equal("https://", keyboard.Keyboard.FocusedKey!.Label);

        keyboard.Navigate(GamepadButtonMask.A);
        Assert.Equal(["https://"], typed);

        keyboard.Navigate(GamepadButtonMask.RightBumper);
        Assert.Equal("小写", keyboard.Keyboard.ActivePage.Name);
        Assert.Equal("a", keyboard.Keyboard.FocusedKey!.Label);
    }

    [Fact]
    public void A_Push_At_The_Cursors_Edge_Is_Left_Alone()
    {
        var keyboard = Build(out _, out _);

        Assert.True(keyboard.Move(InputDirection.Right));
        Assert.False(keyboard.Move(InputDirection.Up), "the cursor is on the first row already");
        Assert.Equal("b", keyboard.Keyboard.FocusedKey!.Label);
    }

    [Fact]
    public void The_Action_Keys_Reach_The_Focused_Application()
    {
        var keyboard = Build(out var typed, out var tapped);

        // The action row ends every page: first column, last row.
        keyboard.Keyboard.CursorRow = keyboard.Keyboard.ActivePage.Rows.Count - 1;
        keyboard.Keyboard.CursorColumn = 0;
        Assert.Equal("退格", keyboard.Keyboard.FocusedKey!.Label);

        keyboard.Navigate(GamepadButtonMask.A);
        Assert.Empty(typed);
        Assert.Equal([(GamepadKey.Backspace, CoreKeyModifiers.None)], tapped);
        Assert.Empty(keyboard.TypedText);

        // 清空 selects everything and deletes it: two taps, in that order.
        keyboard.Navigate(GamepadButtonMask.DPadRight);
        keyboard.Navigate(GamepadButtonMask.DPadRight);
        Assert.Equal("清空", keyboard.Keyboard.FocusedKey!.Label);

        keyboard.Navigate(GamepadButtonMask.A);
        Assert.Equal(
            [
                (GamepadKey.Backspace, CoreKeyModifiers.None),
                (GamepadKey.A, CoreKeyModifiers.Ctrl),
                (GamepadKey.Delete, CoreKeyModifiers.None),
            ],
            tapped);
    }

    [Fact]
    public void 完成_Asks_To_Be_Dismissed_Without_Typing_Anything()
    {
        var keyboard = Build(out var typed, out _);
        var dismissed = 0;
        keyboard.CloseRequested += () => dismissed++;

        keyboard.Keyboard.CursorRow = keyboard.Keyboard.ActivePage.Rows.Count - 1;
        keyboard.Keyboard.CursorColumn = 3;
        Assert.Equal("完成", keyboard.Keyboard.FocusedKey!.Label);

        keyboard.Navigate(GamepadButtonMask.A);

        Assert.Equal(1, dismissed);
        Assert.Empty(typed);
    }

    [Fact]
    public void Closing_The_Keyboard_Resets_It_For_The_Next_Appearance()
    {
        var keyboard = Build(out _, out _);

        // The cursor starts on the first key of the first page, so this types an "a".
        keyboard.Navigate(GamepadButtonMask.A);
        keyboard.Navigate(GamepadButtonMask.RightBumper);
        Assert.Equal("a", keyboard.TypedText);

        keyboard.IsOpen = true;
        keyboard.IsOpen = false;

        Assert.Equal("小写", keyboard.Keyboard.ActivePage.Name);
        Assert.Equal("a", keyboard.Keyboard.FocusedKey!.Label);
        Assert.Empty(keyboard.TypedText);
        Assert.Empty(keyboard.StatusMessage);
    }

    [Fact]
    public void The_Preview_Stops_At_The_Cores_Own_Bound()
    {
        var keyboard = Build(out _, out _);

        var fragment = new TextKeyboardKey("长文本", new string('y', TextTarget.MaxLength));
        for (var i = 0; i < 3; i++)
        {
            keyboard.Keyboard.ShowPage(new TextKeyboardPage("测试", [[fragment]]));
            keyboard.Navigate(GamepadButtonMask.A);
        }

        Assert.Equal(TextTarget.MaxLength, keyboard.TypedText.Length);
    }
}
