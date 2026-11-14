using System.Reflection;
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
/// <para>
/// All of that is scoped to a visible, active mapping tab. Leaving the tab, deactivating the
/// window or destroying it disables navigation and capture immediately and releases this
/// connection's editor lease; reentry resets the held edges so a button held across the
/// boundary is never read as a press.
/// </para>
/// </summary>
public sealed class PhysicalInputNavigationTests
{
    private const string DeviceId = "pad-01";
    private const int MappingTabIndex = 0;
    private const int MouseTabIndex = 1;

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 64.0);

    private static (MainWindow Window, MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider, RecordingOutputSink Sink) BuildWindow()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var viewModel = new MainWindowViewModel(host, store);
        // These tests exercise pad navigation, which the temporary hijack gates; it ships off.
        viewModel.SetGamepadNavigationEnabled(true);
        var window = new MainWindow(viewModel, host);
        window.Show();
        Dispatcher.UIThread.RunJobs();
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

    private static TabControl Tabs(Window window) => window.FindControl<TabControl>("MainTabs")!;

    /// <summary>
    /// Raises the window's Deactivated event the way the platform exporters do. Headless has
    /// no focus model, so the backing delegate is invoked directly - the wiring under test is
    /// the window's own subscription, not the platform's.
    /// </summary>
    private static void RaiseDeactivated(Window window)
    {
        var field = typeof(WindowBase).GetField("Deactivated", BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (EventHandler?)field?.GetValue(window);
        handler?.Invoke(window, EventArgs.Empty);
    }

    private static void RaiseActivated(Window window)
    {
        var field = typeof(WindowBase).GetField("Activated", BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (EventHandler?)field?.GetValue(window);
        handler?.Invoke(window, EventArgs.Empty);
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
        Assert.True(host.IsInjectionSuspended, "explicit hijack owns the in-scope pad even before the editor opens");
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

    [AvaloniaFact]
    public void The_Window_Opens_With_Physical_Input_In_Scope()
    {
        var (window, viewModel, _, _, _) = BuildWindow();

        Assert.True(window.IsVisible);
        Assert.Equal(MappingTabIndex, Tabs(window).SelectedIndex);

        RaiseActivated(window);

        Assert.True(viewModel.IsPhysicalInputInScope, "a shown, active window on the mapping tab is in scope");
    }

    [AvaloniaFact]
    public void Leaving_The_Mapping_Tab_Disables_Navigation_Immediately()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();

        ConnectPad(provider);
        EngineTick(host, window);

        Tabs(window).SelectedIndex = MouseTabIndex;
        Assert.False(viewModel.IsPhysicalInputInScope, "leaving the mapping tab takes the pad out of scope");

        SetPadState(provider, GamepadButtonMask.B);
        EngineTick(host, window);

        Assert.Null(viewModel.SelectedSource);

        // Coming back resets the held edges: the button held across the boundary is the new
        // baseline, not a press.
        Tabs(window).SelectedIndex = MappingTabIndex;
        Assert.True(viewModel.IsPhysicalInputInScope);

        EngineTick(host, window);
        Assert.Null(viewModel.SelectedSource);

        SetPadState(provider);
        EngineTick(host, window);
        SetPadState(provider, GamepadButtonMask.B);
        EngineTick(host, window);

        Assert.Equal(new ButtonBinding(GamepadButton.B), viewModel.SelectedSource);
    }

    [AvaloniaFact]
    public void Deactivating_The_Window_Disables_Navigation_And_Releases_The_Editor_Lease()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();

        ConnectPad(provider);
        EngineTick(host, window);

        SetPadState(provider, GamepadButtonMask.B);
        EngineTick(host, window);
        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.B));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.True(viewModel.IsEditing);
        Assert.True(host.IsInjectionSuspended);

        RaiseDeactivated(window);
        Assert.False(viewModel.IsPhysicalInputInScope, "a deactivated window takes the pad out of scope");
        Assert.False(host.IsInjectionSuspended, "leaving scope releases this connection's lease");

        SetPadState(provider, GamepadButtonMask.B | GamepadButtonMask.X);
        EngineTick(host, window);
        Assert.Equal(GamepadButton.B, viewModel.Editor.SelectedButton);

        RaiseActivated(window);
        Assert.True(viewModel.IsPhysicalInputInScope);
        Assert.True(host.IsInjectionSuspended, "reentry restores the lease for the retained draft");
    }

    [AvaloniaFact]
    public void Destroying_The_Window_Disables_Navigation_And_Detaches_Sampling()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();

        ConnectPad(provider);
        EngineTick(host, window);

        window.Close();
        Assert.False(viewModel.IsPhysicalInputInScope, "a destroyed window takes the pad out of scope");

        // The window's sampling subscription is gone: a tick raises nothing the view model
        // can see, so a press can no longer navigate.
        SetPadState(provider, GamepadButtonMask.B);
        host.TickOnce(Tick);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewModel.SelectedSource);
    }

    [AvaloniaFact]
    public void A_Recreated_Window_Navigates_Again_With_Reset_Edges()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();

        ConnectPad(provider);
        EngineTick(host, window);

        SetPadState(provider, GamepadButtonMask.B);
        EngineTick(host, window);
        Assert.Equal(new ButtonBinding(GamepadButton.B), viewModel.SelectedSource);

        window.Close();

        var recreated = new MainWindow(viewModel, host);
        recreated.Show();
        Dispatcher.UIThread.RunJobs();
        RaiseActivated(recreated);
        Assert.True(viewModel.IsPhysicalInputInScope, "the new window opens in scope");

        // B is still held: the first sample of the new window is the baseline, not a press.
        EngineTick(host, recreated);
        Assert.Equal(new ButtonBinding(GamepadButton.B), viewModel.SelectedSource);

        SetPadState(provider);
        EngineTick(host, recreated);
        SetPadState(provider, GamepadButtonMask.X);
        EngineTick(host, recreated);

        Assert.Equal(new ButtonBinding(GamepadButton.X), viewModel.SelectedSource);
    }
}
