using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// Physical-gamepad navigation through the real window, in two levels. A press on the pad
/// SELECTS that element and nothing else - the mapping keeps injecting, because browsing the
/// pad is not a request to configure. Entering the editor (a click) is the second level, and
/// that is the only thing that hijacks the pad: while a mapping is open, presses switch which
/// element is being configured instead of being injected.
/// </summary>
public sealed class PhysicalInputNavigationTests
{
    private const string DeviceId = "pad-01";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 64.0);

    private static (MainWindow Window, MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider, RecordingOutputSink Sink) BuildWindow()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var viewModel = new MainWindowViewModel(host, store);
        var window = new MainWindow(viewModel, host);
        window.Show();
        return (window, viewModel, host, provider, sink);
    }

    private static void ConnectPad(FakeGamepadProvider provider, GamepadButtonMask buttons = default)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(buttons, 0f, 0f, 0f, 0f));
    }

    private static void SetPadState(
        FakeGamepadProvider provider,
        GamepadButtonMask buttons = default,
        float leftX = 0f,
        float leftY = 0f,
        float rightX = 0f,
        float rightY = 0f)
        => provider.SetState(DeviceId, GamepadState.Create(buttons, leftX, leftY, rightX, rightY));

    /// <summary>One engine tick through the window's own Sampled wiring.</summary>
    private static void EngineTick(EngineHost host, Window window)
    {
        host.TickOnce(Tick);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>One presence sample through the window's own PresenceSampled wiring.</summary>
    private static void PresenceSample(EngineHost host, Window window)
    {
        host.SamplePresence();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public void Pressing_A_Physical_Button_Selects_Without_Opening_The_Editor()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();

        ConnectPad(provider);
        EngineTick(host, window);

        SetPadState(provider, GamepadButtonMask.B);
        EngineTick(host, window);

        Assert.Equal(new ButtonBinding(GamepadButton.B), viewModel.SelectedSource);
        Assert.False(viewModel.IsEditing, "a press selects; only a click enters the editor");
        Assert.False(host.IsInjectionSuspended);
        Assert.False(window.FindControl<Border>("EditorPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public void Clicking_The_Selected_Element_Opens_The_Editor_And_Hijacks_The_Pad()
    {
        var (window, viewModel, host, provider, sink) = BuildWindow();

        ConnectPad(provider);
        EngineTick(host, window);

        SetPadState(provider, GamepadButtonMask.B);
        EngineTick(host, window);
        Assert.False(viewModel.IsEditing);

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.B));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.True(viewModel.IsEditing);
        Assert.True(host.IsInjectionSuspended, "entering the editor is what hijacks the pad");
        Assert.Equal(GamepadButton.B, viewModel.Editor.SelectedButton);
        Assert.True(window.FindControl<Border>("EditorPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public void Pressing_A_Physical_Button_Selects_With_The_Engine_Stopped()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();

        ConnectPad(provider);
        viewModel.SyncControllers();
        PresenceSample(host, window);

        SetPadState(provider, GamepadButtonMask.B);
        PresenceSample(host, window);

        Assert.False(host.IsRunning);
        Assert.Equal(new ButtonBinding(GamepadButton.B), viewModel.SelectedSource);
        Assert.False(viewModel.IsEditing);
        Assert.False(window.FindControl<Border>("EditorPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public void A_Button_Pressed_While_Editing_Navigates_Instead_Of_Injecting()
    {
        var (window, viewModel, host, provider, sink) = BuildWindow();

        ConnectPad(provider);
        EngineTick(host, window);

        SetPadState(provider, GamepadButtonMask.B);
        EngineTick(host, window);
        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.B));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.True(viewModel.IsEditing);

        sink.Calls.Clear();
        SetPadState(provider, GamepadButtonMask.B | GamepadButtonMask.X);
        EngineTick(host, window);

        Assert.Equal(GamepadButton.X, viewModel.Editor.SelectedButton);
        Assert.DoesNotContain(sink.Calls, call => call.StartsWith("down:", StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Calls, call => call.StartsWith("up:", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Deflecting_A_Stick_On_The_Pad_Selects_The_Stick()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();

        ConnectPad(provider);
        EngineTick(host, window);

        SetPadState(provider, default, 0f, 0f, 0.9f, 0f);
        EngineTick(host, window);

        Assert.Equal(new StickBinding(GamepadStick.Right), viewModel.SelectedSource);
        Assert.False(viewModel.IsEditing);
        Assert.False(window.FindControl<Border>("EditorPanel")!.IsVisible);
    }
}
