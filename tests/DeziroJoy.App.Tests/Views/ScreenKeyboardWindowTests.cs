using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
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
    public void A_Released_Window_No_Longer_Answers_Its_View_Model()
    {
        // The presentation host builds one window per appearance and closes it when the
        // daemon's flag clears, keeping the view model no longer: both binding subscriptions
        // are dropped on close, so a window that is gone cannot be asked back onto the screen.
        // Show on a closed window throws, which is what a stale subscription would run into.
        var keyboard = BuildKeyboard(out _, out _);
        var window = new ScreenKeyboardWindow(keyboard);
        try
        {
            keyboard.IsOpen = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);

            window.Close();
            Dispatcher.UIThread.RunJobs();

            keyboard.IsOpen = false;
            keyboard.IsOpen = true;
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.IsVisible, "a released window must not come back with its view model");
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

    [AvaloniaFact]
    public void The_Caps_Are_Sized_For_A_Gamepad_Rather_Than_A_Mouse()
    {
        var keyboard = BuildKeyboard(out _, out _);
        var window = new ScreenKeyboardWindow(keyboard);
        try
        {
            keyboard.IsOpen = true;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // A gamepad cursor is driven from across the room, so a cap is a target the user
            // can see and separate from its neighbours - not the mouse-sized chip a pointer
            // can afford.
            var cap = KeyCap(window, "a");
            Assert.True(cap.MinWidth >= 56, $"a cap must be a wide target, was {cap.MinWidth}");
            Assert.True(cap.MinHeight >= 44, $"a cap must be a tall target, was {cap.MinHeight}");
            Assert.Equal(16, cap.FontSize);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_Focused_Cap_Carries_An_Accent_Ring_As_Well_As_An_Accent_Surface()
    {
        var keyboard = BuildKeyboard(out _, out _);
        var window = new ScreenKeyboardWindow(keyboard);
        try
        {
            keyboard.IsOpen = true;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var focused = FocusedCap(window);
            var resting = KeyCap(window, "z");

            // The cursor is the one thing a gamepad user must never lose track of, so it is
            // marked three ways at once - the accent surface, an accent ring around the cap
            // and a heavier accent label - and the ring is what makes it read at a distance.
            Assert.Equal(1, Ring(focused).Opacity, precision: 3);
            Assert.Equal(0, Ring(resting).Opacity, precision: 3);
            Assert.Equal(Token("AccentSurfaceBrush"), ColorOf(Frame(focused).Background));
            Assert.Equal(Token("AccentBrush"), ColorOf(Frame(focused).BorderBrush));
            Assert.Equal(FontWeight.Bold, LabelOf(focused).FontWeight);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_Action_Row_Reads_As_Function_Keys()
    {
        var keyboard = BuildKeyboard(out _, out _);
        var window = new ScreenKeyboardWindow(keyboard);
        try
        {
            keyboard.IsOpen = true;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // The four keys that edit and dismiss are the same four caps at the bottom of
            // every page, and they read as function keys - a sunken surface of their own -
            // so they are found by shape rather than by hunting for a label.
            foreach (var label in new[] { "退格", "空格", "清空", "完成" })
            {
                Assert.Contains("action", KeyCap(window, label).Classes);
            }

            Assert.DoesNotContain("action", KeyCap(window, "a").Classes);
            Assert.DoesNotContain("action", KeyCap(window, "z").Classes);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The cap's own frame - the box that carries its surface.</summary>
    private static Border Frame(Button cap)
        => cap.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("textKeyFrame"));

    /// <summary>The cap's focus ring - laid out on every cap, lit only on the focused one.</summary>
    private static Border Ring(Button cap)
        => cap.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("textKeyRing"));

    private static TextBlock LabelOf(Button cap)
        => cap.GetVisualDescendants().OfType<TextBlock>().First();

    private static Color ColorOf(IBrush? brush)
        => Assert.IsType<ISolidColorBrush>(brush, exactMatch: false).Color;

    private static Color Token(string key)
    {
        Application.Current!.TryFindResource(key, ActualThemeVariant(), out var brush);
        return ColorOf(brush as IBrush);
    }

    private static ThemeVariant ActualThemeVariant()
        => Application.Current!.ActualThemeVariant;

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

    [Fact]
    public void A_HiDPI_Screen_Scales_The_Footprint_Before_It_Places_The_Keyboard()
    {
        // A window position and a screen rect are device pixels, so the size that has to fit
        // is the measured size scaled by the screen: 600x320 measured on a 200% screen is
        // 1200x640 pixels, and it is that footprint which is centred and clamped.
        var screen = new PixelRect(0, 0, 1920, 1080);
        var window = new Size(600, 320);

        var position = ScreenKeyboardWindow.ResolvePosition(screen, window, scaling: 2);

        Assert.Equal(new PixelPoint(360, 344), position);
    }

    [Fact]
    public void A_Keyboard_Wider_Or_Taller_Than_The_Screen_Parks_At_Its_Top_Left_Corner()
    {
        // 1000x500 measured at 200% is 2000x1000 pixels on a 1920x1080 screen: neither axis
        // has room, so the clamp collapses onto the screen's own origin. A keyboard that
        // cannot fit parks at the corner - the part the user can still reach - instead of
        // hanging off an edge.
        var screen = new PixelRect(0, 0, 1920, 1080);
        var window = new Size(1000, 500);

        var position = ScreenKeyboardWindow.ResolvePosition(screen, window, scaling: 2);

        Assert.Equal(new PixelPoint(0, 0), position);
    }

    [Fact]
    public void The_Edge_Margin_Scales_With_The_Screen()
    {
        // The margin is a layout-space constant, so it clears a taskbar at 100% and at 200%
        // alike: 48 units above the bottom edge becomes 96 pixels on a 200% screen.
        var screen = new PixelRect(0, 0, 1920, 1080);
        var window = new Size(600, 100);

        var position = ScreenKeyboardWindow.ResolvePosition(screen, window, scaling: 2);

        Assert.Equal(new PixelPoint(360, 1080 - 200 - 96), position);
    }

    [Fact]
    public void A_Second_Monitor_Keeps_The_Keyboard_On_That_Monitor()
    {
        // A monitor to the left of the primary owns negative screen coordinates, so the
        // keyboard is centred inside that monitor rather than clamped back onto the primary.
        var screen = new PixelRect(-1920, 0, 1920, 1080);
        var window = new Size(600, 320);

        var position = ScreenKeyboardWindow.ResolvePosition(screen, window);

        Assert.Equal(new PixelPoint(-1260, 712), position);
    }

    [AvaloniaFact]
    public void The_Keyboard_Opens_Inside_The_Screen_It_Is_On()
    {
        var keyboard = BuildKeyboard(out _, out _);
        var window = new ScreenKeyboardWindow(keyboard);
        try
        {
            keyboard.IsOpen = true;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // The whole keyboard, not its top-left corner: every edge of the window's
            // footprint on the screen sits inside the screen it was placed on.
            Assert.True(IsInsideScreen(window), "the keyboard must open fully on screen");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_Page_Switch_Keeps_The_Keyboard_Inside_The_Screen()
    {
        var keyboard = BuildKeyboard(out _, out _);
        var window = new ScreenKeyboardWindow(keyboard);
        try
        {
            keyboard.IsOpen = true;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var screen = window.Screens.ScreenFromVisual(window)!;

            // Every page is a different width and a different height, and the window sizes
            // itself to the page it shows, so a keyboard placed for one page is not placed
            // for the next: the placement is re-resolved as the footprint changes, or a wider
            // page would grow the keyboard past the edge it was clamped against before. Each
            // page keeps the whole keyboard on screen, bottom-anchored above the screen edge.
            foreach (var page in keyboard.Keyboard.Pages)
            {
                keyboard.Keyboard.ShowPage(page);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();

                Assert.True(IsInsideScreen(window), $"the {page.Name} page must stay on screen");
                Assert.Equal(
                    screen.Bounds.Y + screen.Bounds.Height - 48,
                    window.Position.Y + window.Bounds.Height);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_Keyboard_Left_Off_Screen_Comes_Back_On_The_Next_Pass()
    {
        var keyboard = BuildKeyboard(out _, out _);
        var window = new ScreenKeyboardWindow(keyboard);
        try
        {
            keyboard.IsOpen = true;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.True(IsInsideScreen(window), "the keyboard starts on screen");

            // A window manager, a monitor unplugged from under the keyboard or a screen
            // change can leave it outside every screen. The placement is re-resolved on the
            // next layout pass and falls back to the primary screen when no screen contains
            // the window, so the keyboard finds its way back instead of staying lost.
            window.Position = new PixelPoint(3000, 2000);
            window.InvalidateMeasure();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.True(IsInsideScreen(window), "a keyboard left off screen must come back");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Whether the window's whole footprint - its position plus its size, scaled into the
    /// screen's own pixels - sits inside the screen it is on.
    /// </summary>
    private static bool IsInsideScreen(ScreenKeyboardWindow window)
    {
        if (window.Screens.ScreenFromVisual(window) is not { } screen)
        {
            return false;
        }

        var footprint = new PixelRect(
            window.Position,
            new PixelSize(
                (int)Math.Round(window.Bounds.Width * screen.Scaling),
                (int)Math.Round(window.Bounds.Height * screen.Scaling)));

        return footprint.X >= screen.Bounds.X
            && footprint.Y >= screen.Bounds.Y
            && footprint.X + footprint.Width <= screen.Bounds.X + screen.Bounds.Width
            && footprint.Y + footprint.Height <= screen.Bounds.Y + screen.Bounds.Height;
    }
}
