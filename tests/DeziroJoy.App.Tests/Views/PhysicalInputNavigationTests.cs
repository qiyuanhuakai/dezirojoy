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
/// Physical-gamepad navigation through the real window: a press on the connected pad opens
/// that element's mapping settings, with the engine running or stopped, and while a mapping
/// is open the press navigates the editor instead of being injected.
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
    public void Pressing_A_Physical_Button_Opens_The_Mapping_Settings()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();

        ConnectPad(provider);
        EngineTick(host, window);

        SetPadState(provider, GamepadButtonMask.B);
        EngineTick(host, window);

        Assert.True(viewModel.IsEditing);
        Assert.Equal(GamepadButton.B, viewModel.Editor.SelectedButton);
        Assert.True(window.FindControl<Border>("EditorPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public void Pressing_A_Physical_Button_Navigates_With_The_Engine_Stopped()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();

        ConnectPad(provider);
        viewModel.SyncControllers();
        PresenceSample(host, window);

        SetPadState(provider, GamepadButtonMask.B);
        PresenceSample(host, window);

        Assert.False(host.IsRunning);
        Assert.True(viewModel.IsEditing);
        Assert.Equal(GamepadButton.B, viewModel.Editor.SelectedButton);
        Assert.True(window.FindControl<Border>("EditorPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public void A_Button_Pressed_While_Editing_Navigates_Instead_Of_Injecting()
    {
        var (window, viewModel, host, provider, sink) = BuildWindow();

        ConnectPad(provider);
        EngineTick(host, window);

        SetPadState(provider, GamepadButtonMask.B);
        EngineTick(host, window);
        Assert.True(viewModel.IsEditing);

        sink.Calls.Clear();
        SetPadState(provider, GamepadButtonMask.B | GamepadButtonMask.X);
        EngineTick(host, window);

        Assert.Equal(GamepadButton.X, viewModel.Editor.SelectedButton);
        Assert.DoesNotContain(sink.Calls, call => call.StartsWith("down:", StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Calls, call => call.StartsWith("up:", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Deflecting_A_Stick_On_The_Pad_Opens_The_Stick_Mapping_Settings()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();

        ConnectPad(provider);
        EngineTick(host, window);

        SetPadState(provider, default, 0f, 0f, 0.9f, 0f);
        EngineTick(host, window);

        Assert.True(viewModel.IsEditing);
        Assert.True(viewModel.Editor.SourceIsStick);
        Assert.Equal(GamepadStick.Right, viewModel.Editor.SelectedStick);
    }
}
