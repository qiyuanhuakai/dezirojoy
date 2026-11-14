using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using AvaloniaMouseButton = Avalonia.Input.MouseButton;
using CoreKeyModifiers = DeziroJoy.Core.Mapping.KeyModifiers;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The floating screen keyboard floats above every window on screen - the target application
/// above all - so the one thing it must never do is take that application's keyboard focus:
/// the characters it types are meant for the search box, the rename or the URL bar behind it.
/// It shows itself while the view model's open flag is set and hides the moment it clears, it
/// marks the cap the gamepad cursor sits on, and every cap it shows forwards its activation to
/// the view model, which is the only thing that asks the daemon to inject.
/// </summary>
public sealed class ScreenKeyboardWindowTests
{
    private static ScreenKeyboardViewModel BuildKeyboard(
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

    private static Button KeyCap(ScreenKeyboardWindow window, string label)
        => window.FindControl<ItemsControl>("TextKeyboardGrid")!
            .GetLogicalDescendants().OfType<Button>()
            .First(button => (button.DataContext as TextKeyboardKey)?.Label == label);

    private static Button FocusedCap(ScreenKeyboardWindow window)
        => window.FindControl<ItemsControl>("TextKeyboardGrid")!
            .GetLogicalDescendants().OfType<Button>()
            .First(button => button.Classes.Contains("focused"));

    private static RadioButton[] PageTabs(ScreenKeyboardWindow window)
        => window.FindControl<ItemsControl>("TextKeyboardPageNav")!
            .GetLogicalDescendants().OfType<RadioButton>().ToArray();

    /// <summary>Presses a control with the pointer, the way the other GUI tests drive a button.</summary>
    private static void Click(Control target, Window window)
    {
        window.UpdateLayout();
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        window.MouseDown(point, AvaloniaMouseButton.Left);
        window.MouseUp(point, AvaloniaMouseButton.Left);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_Keyboard_Never_Takes_The_Target_Applications_Focus()
    {
        var window = new ScreenKeyboardWindow(BuildKeyboard(out _, out _));
        try
        {
            // The target application keeps the keyboard: the window is shown without
            // activation and cannot be focused itself.
            Assert.False(window.ShowActivated, "showing the keyboard must not activate it");
            Assert.False(window.Focusable, "the keyboard must never hold keyboard focus");
            Assert.True(window.Topmost, "the keyboard must float above the target application");
            Assert.Equal(WindowDecorations.None, window.WindowDecorations);
            Assert.False(window.CanResize, "a keyboard is not something the user resizes");
            Assert.False(window.ShowInTaskbar, "a keyboard is not a window of its own in the taskbar");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_Keyboard_Shows_With_The_Open_Flag_And_Hides_When_It_Clears()
    {
        var keyboard = BuildKeyboard(out _, out _);
        var window = new ScreenKeyboardWindow(keyboard);
        try
        {
            Assert.False(window.IsVisible, "the keyboard starts hidden");

            keyboard.IsOpen = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible, "the special key must put the keyboard on screen");

            keyboard.IsOpen = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsVisible, "dismissing it must take it away again");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_Cursor_Is_Marked_On_The_Cap_It_Sits_On()
    {
        var keyboard = BuildKeyboard(out _, out _);
        var window = new ScreenKeyboardWindow(keyboard);
        try
        {
            keyboard.IsOpen = true;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // The cursor is view-model state, not real focus - the window is not focusable -
            // so the cap it sits on wears the accent surface and its label weight rises.
            Assert.Equal("a", FocusedCap(window).DataContext is TextKeyboardKey key ? key.Label : null);
            Assert.Contains("focused", KeyCap(window, "a").Classes);

            keyboard.Navigate(GamepadButtonMask.DPadRight);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("b", FocusedCap(window).DataContext is TextKeyboardKey moved ? moved.Label : null);
            Assert.DoesNotContain("focused", KeyCap(window, "a").Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_Cap_Press_Types_Into_The_Focused_Application()
    {
        var keyboard = BuildKeyboard(out var typed, out _);
        var window = new ScreenKeyboardWindow(keyboard);
        try
        {
            keyboard.IsOpen = true;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // A pointer press moves the cursor onto the cap and presses it, so the next
            // D-pad push continues from where the user last reached.
            Click(KeyCap(window, "b"), window);

            Assert.Equal(["b"], typed);
            Assert.Equal("b", keyboard.TypedText);
            Assert.Equal("b", FocusedCap(window).DataContext is TextKeyboardKey key ? key.Label : null);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_Page_Tabs_Switch_The_Grid_And_Reset_The_Cursor()
    {
        var keyboard = BuildKeyboard(out _, out _);
        var window = new ScreenKeyboardWindow(keyboard);
        try
        {
            keyboard.IsOpen = true;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var tabs = PageTabs(window);
            Assert.Equal(
                ["小写", "大写", "数字", "符号", "常用"],
                tabs.Select(tab => tab.Content as string));
            Assert.Equal(["小写"], tabs.Where(tab => tab.IsChecked == true).Select(tab => tab.Content as string));

            Click(tabs.First(tab => tab.Content as string == "常用"), window);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("常用", keyboard.Keyboard.ActivePage.Name);
            Assert.Equal(["常用"], tabs.Where(tab => tab.IsChecked == true).Select(tab => tab.Content as string));
            Assert.Equal("https://", FocusedCap(window).DataContext is TextKeyboardKey key ? key.Label : null);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_Return_Button_Asks_To_Be_Dismissed()
    {
        var keyboard = BuildKeyboard(out _, out _);
        var window = new ScreenKeyboardWindow(keyboard);
        var dismissed = 0;
        keyboard.CloseRequested += () => dismissed++;
        try
        {
            keyboard.IsOpen = true;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // The dismissal is a request rather than a close: the daemon owns the flag, and
            // the next engine toggle has to agree with what the user sees.
            Click(window.FindControl<Button>("CloseButton")!, window);

            Assert.Equal(1, dismissed);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_Failed_Request_Is_Said_Beside_The_Preview()
    {
        var keyboard = new ScreenKeyboardViewModel(
            _ => Task.FromException(new InvalidOperationException("守护进程不可用")),
            (_, _) => Task.CompletedTask);
        var window = new ScreenKeyboardWindow(keyboard);
        try
        {
            keyboard.IsOpen = true;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            keyboard.Navigate(GamepadButtonMask.A);
            Dispatcher.UIThread.RunJobs();

            var status = window.FindControl<TextBlock>("StatusText")!;
            Assert.True(status.IsVisible, "a keyboard that typed nothing must say why");
            Assert.Contains("守护进程不可用", status.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void The_Keyboard_Parks_Along_The_Bottom_Of_The_Screen()
    {
        var screen = new PixelRect(0, 0, 1920, 1080);
        var window = new Size(600, 320);

        var position = ScreenKeyboardWindow.ResolvePosition(screen, window);

        // Horizontally centred, one margin above the bottom edge: the target application's own
        // content - the field the keyboard is filling - lives above the bottom strip.
        Assert.Equal(new PixelPoint(660, 712), position);
    }

    [Fact]
    public void A_Screen_Shorter_Than_The_Keyboard_Keeps_It_On_Screen()
    {
        var screen = new PixelRect(0, 0, 800, 300);
        var window = new Size(600, 320);

        var position = ScreenKeyboardWindow.ResolvePosition(screen, window);

        // The bottom edge would push the whole keyboard off the top, so it parks at the top
        // rather than disappearing.
        Assert.Equal(new PixelPoint(100, 0), position);
    }
}
