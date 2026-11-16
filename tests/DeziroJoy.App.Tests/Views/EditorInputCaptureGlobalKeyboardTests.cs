using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Windows.Capture;
using CoreKeyModifiers = DeziroJoy.Core.Mapping.KeyModifiers;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// How <see cref="EditorInputCapture"/> drives a global keyboard capture while the editor waits
/// for a key. The capture itself is a double - no real hook, no process-global keyboard - so these
/// cover the wiring only: arming installs it, a captured combo reaches the editor, an ended capture
/// disarms it, and every way the capture can be abandoned (settings opening, focus loss, the
/// control detaching) tears it down rather than leaving a hook swallowing keys. The final case
/// pins the fallback: with no global capture the window's own key events still configure a key, so
/// a platform without the capability is not left pretending.
/// </summary>
public sealed class EditorInputCaptureGlobalKeyboardTests : IDisposable
{
    private readonly Func<IGlobalKeyboardCapture?> _previousFactory;

    public EditorInputCaptureGlobalKeyboardTests()
    {
        _previousFactory = EditorInputCapture.GlobalKeyboardCaptureFactory;
    }

    public void Dispose() => EditorInputCapture.GlobalKeyboardCaptureFactory = _previousFactory;

    [AvaloniaFact]
    public void Arming_The_Editor_Installs_The_Global_Capture()
    {
        var capture = new FakeGlobalKeyboardCapture();
        EditorInputCapture.GlobalKeyboardCaptureFactory = () => capture;
        var (_, viewModel) = BuildWindow();

        viewModel.Editor.IsCapturingKey = true;

        Assert.Equal(1, capture.ArmCalls);
        Assert.Equal(0, capture.DisarmCalls);
    }

    [AvaloniaFact]
    public void A_Global_Captured_Combo_Reaches_The_Editor()
    {
        var capture = new FakeGlobalKeyboardCapture();
        EditorInputCapture.GlobalKeyboardCaptureFactory = () => capture;
        var (_, viewModel) = BuildWindow();

        viewModel.Editor.IsCapturingKey = true;
        capture.RaiseKeyCaptured(GamepadKey.C, CoreKeyModifiers.Ctrl);

        Assert.Equal(GamepadKey.C, viewModel.Editor.SelectedKey);
        Assert.True(viewModel.Editor.ModifierCtrl);
    }

    [AvaloniaFact]
    public void A_Global_Capture_Ending_Disarms_The_Editor_And_The_Hook()
    {
        var capture = new FakeGlobalKeyboardCapture();
        EditorInputCapture.GlobalKeyboardCaptureFactory = () => capture;
        var (_, viewModel) = BuildWindow();

        viewModel.Editor.IsCapturingKey = true;
        capture.RaiseCaptureEnded();

        Assert.False(viewModel.Editor.IsCapturingKey);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, capture.DisarmCalls);
    }

    [AvaloniaFact]
    public void Opening_Settings_Abandons_An_Armed_Capture()
    {
        var capture = new FakeGlobalKeyboardCapture();
        EditorInputCapture.GlobalKeyboardCaptureFactory = () => capture;
        var (_, viewModel) = BuildWindow();

        viewModel.Editor.IsCapturingKey = true;
        viewModel.IsSettingsOpen = true;

        Assert.False(viewModel.Editor.IsCapturingKey);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, capture.DisarmCalls);
    }

    [AvaloniaFact]
    public void Losing_Focus_Abandons_An_Armed_Capture()
    {
        var capture = new FakeGlobalKeyboardCapture();
        EditorInputCapture.GlobalKeyboardCaptureFactory = () => capture;
        var (window, viewModel) = BuildWindow();

        viewModel.Editor.IsCapturingKey = true;
        RaiseDeactivated(window);

        Assert.False(viewModel.Editor.IsCapturingKey);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, capture.DisarmCalls);
    }

    [AvaloniaFact]
    public void Detaching_The_Control_Unhooks_Immediately()
    {
        var capture = new FakeGlobalKeyboardCapture();
        EditorInputCapture.GlobalKeyboardCaptureFactory = () => capture;
        var (window, viewModel) = BuildWindow();

        viewModel.Editor.IsCapturingKey = true;
        window.Close();

        // A closing window may never run a posted job, so the hook is removed on the spot.
        Assert.Equal(1, capture.DisarmCalls);
        Assert.False(viewModel.Editor.IsCapturingKey);
    }

    [AvaloniaFact]
    public void Without_A_Global_Capture_The_Window_Events_Still_Configure_A_Key()
    {
        EditorInputCapture.GlobalKeyboardCaptureFactory = () => null;
        var (window, viewModel) = BuildWindow();

        viewModel.Editor.IsCapturingKey = true;
        Press(window, Key.C, RawInputModifiers.Control, PhysicalKey.C);

        // With no global capture the window's own key events still configure the target, so a
        // platform without the capability is not left pretending it has one.
        Assert.Equal(GamepadKey.C, viewModel.Editor.SelectedKey);
        Assert.True(viewModel.Editor.ModifierCtrl);
    }

    private static (MainWindow Window, MainWindowViewModel ViewModel) BuildWindow()
    {
        var provider = new FakeGamepadProvider();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, new RecordingOutputSink(), store);
        var viewModel = new MainWindowViewModel(host, store);
        var window = new MainWindow(viewModel, host);
        window.Show();
        return (window, viewModel);
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers, PhysicalKey physicalKey)
        => window.KeyPress(key, modifiers, physicalKey, null);

    /// <summary>
    /// Raises the window's Deactivated event the way the platform exporters do. Headless has no
    /// focus model, so the backing delegate is invoked directly - the wiring under test is the
    /// control's own subscription, not the platform's.
    /// </summary>
    private static void RaiseDeactivated(Window window)
    {
        var field = typeof(WindowBase).GetField("Deactivated", BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (EventHandler?)field?.GetValue(window);
        handler?.Invoke(window, EventArgs.Empty);
    }

    private sealed class FakeGlobalKeyboardCapture : IGlobalKeyboardCapture
    {
        public int ArmCalls { get; private set; }

        public int DisarmCalls { get; private set; }

        public event Action<GamepadKey, CoreKeyModifiers>? KeyCaptured;

        public event Action? CaptureEnded;

        public void Arm() => ArmCalls++;

        public void Disarm() => DisarmCalls++;

        public void Dispose()
        {
        }

        public void RaiseKeyCaptured(GamepadKey key, CoreKeyModifiers modifiers)
            => KeyCaptured?.Invoke(key, modifiers);

        public void RaiseCaptureEnded() => CaptureEnded?.Invoke();
    }
}
