using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The screen keyboard's above-state lifecycle, the same contract the layer hint keeps:
/// Topmost=True is applied before the window is mapped, where no window manager honours it, so
/// the keyboard asks again once the window exists - per appearance, and on the pass that
/// follows the window manager's map. None of it takes the target application's keyboard focus:
/// the characters still go to the search box, the rename or the URL bar behind the keyboard.
/// </summary>
public sealed class ScreenKeyboardWindowTopmostTests
{
    private static ScreenKeyboardViewModel BuildKeyboard()
        => new(
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask);

    [AvaloniaFact]
    public void The_Keyboard_Asks_To_Be_Above_Every_Time_It_Opens()
    {
        var keyboard = BuildKeyboard();
        var recorder = new RecordingOverlayTopmost();
        var window = new ScreenKeyboardWindow(keyboard, recorder);
        try
        {
            // The 屏幕键盘 special key puts the keyboard on screen, and showing it asks the
            // window manager for the above-state - once from Opened, where the X11 window
            // exists, and once from the show path itself.
            keyboard.IsOpen = true;

            Assert.Equal(2, recorder.Applied.Count);
            Assert.All(recorder.Applied, applied => Assert.Same(window, applied));
            Assert.True(window.IsVisible, "the special key must put the keyboard on screen");

            // Dismissing it takes it away and asks for nothing.
            keyboard.IsOpen = false;
            Assert.Equal(2, recorder.Applied.Count);
            Assert.False(window.IsVisible, "dismissing it must take it away");

            // Opening it again is a fresh mapping, so it asks again.
            keyboard.IsOpen = true;

            Assert.Equal(4, recorder.Applied.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_Posted_Pass_Re_Asks_After_The_Window_Manager_Sees_The_Map()
    {
        var keyboard = BuildKeyboard();
        var recorder = new RecordingOverlayTopmost();
        var window = new ScreenKeyboardWindow(keyboard, recorder);
        try
        {
            keyboard.IsOpen = true;

            Assert.Equal(2, recorder.Applied.Count);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, recorder.Applied.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_Keyboard_That_Never_Opens_Asks_For_Nothing()
    {
        var keyboard = BuildKeyboard();
        var recorder = new RecordingOverlayTopmost();
        var window = new ScreenKeyboardWindow(keyboard, recorder);
        try
        {
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.IsVisible);
            Assert.Empty(recorder.Applied);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Asking_To_Be_Above_Changes_Nothing_About_The_Target_Applications_Focus()
    {
        var keyboard = BuildKeyboard();
        var recorder = new RecordingOverlayTopmost();
        var window = new ScreenKeyboardWindow(keyboard, recorder);
        try
        {
            keyboard.IsOpen = true;
            Dispatcher.UIThread.RunJobs();

            // The above-state is a stacking request and nothing else: the keyboard still shows
            // without activation and still cannot hold keyboard focus, so the characters it
            // types go to the application behind it.
            Assert.False(window.ShowActivated, "showing the keyboard must not activate it");
            Assert.False(window.Focusable, "the keyboard must never hold keyboard focus");
            Assert.True(window.Topmost, "the keyboard must float above the target application");
            Assert.All(recorder.Applied, applied => Assert.Same(window, applied));
        }
        finally
        {
            window.Close();
        }
    }
}
