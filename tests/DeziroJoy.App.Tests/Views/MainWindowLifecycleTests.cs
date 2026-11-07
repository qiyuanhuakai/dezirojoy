using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The main window is a releasable presentation resource, not the owner of the runtime.
/// Closing it must detach everything it attached - both timers, both host sampling
/// subscriptions, the view-model listener - drop callbacks that were already queued, and
/// leave the engine running: the Windows daemon and the Linux local engine keep mapping with
/// no window open. A window built afterwards starts from a clean slate, so a
/// destroy/recreate cycle never stacks a second set of subscriptions.
/// </summary>
public sealed class MainWindowLifecycleTests
{
    private const string DeviceId = "pad-01";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 64.0);

    private static (MainWindow Window, MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider) BuildWindow()
    {
        var provider = new FakeGamepadProvider();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, new RecordingOutputSink(), store);
        var viewModel = new MainWindowViewModel(host, store);
        var window = new MainWindow(viewModel, host);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel, host, provider);
    }

    /// <summary>
    /// Raises the window's Activated event the way the platform exporters do. Headless never
    /// grants activation to a second window, so the event a real platform raises when it
    /// shows and activates a window is raised here - the wiring under test is the window's
    /// own subscription, not the platform's decision.
    /// </summary>
    private static void RaiseActivated(Window window)
    {
        var field = typeof(WindowBase).GetField("Activated", BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (EventHandler?)field?.GetValue(window);
        handler?.Invoke(window, EventArgs.Empty);
    }

    private static int WindowSubscriptionCount(object source, string eventName)
    {
        for (var type = source.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is null)
            {
                continue;
            }

            var handler = (Delegate?)field.GetValue(source);
            return handler?.GetInvocationList().Count(subscriber => subscriber.Target is MainWindow) ?? 0;
        }

        return 0;
    }

    [AvaloniaFact]
    public void A_Shown_Window_Subscribes_Exactly_Once()
    {
        var (window, viewModel, host, _) = BuildWindow();

        Assert.Equal(1, WindowSubscriptionCount(host, nameof(EngineHost.Sampled)));
        Assert.Equal(1, WindowSubscriptionCount(host, nameof(EngineHost.PresenceSampled)));
        Assert.Equal(1, WindowSubscriptionCount(viewModel, nameof(MainWindowViewModel.PropertyChanged)));
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void Closing_The_Window_Detaches_Every_Subscription_It_Made()
    {
        var (window, viewModel, host, _) = BuildWindow();

        window.Close();

        Assert.Equal(0, WindowSubscriptionCount(host, nameof(EngineHost.Sampled)));
        Assert.Equal(0, WindowSubscriptionCount(host, nameof(EngineHost.PresenceSampled)));
        Assert.Equal(0, WindowSubscriptionCount(viewModel, nameof(MainWindowViewModel.PropertyChanged)));
    }

    [AvaloniaFact]
    public void Closing_The_Window_Leaves_The_Mapping_Engine_Running()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        viewModel.ToggleEngineCommand.Execute(null);
        Assert.True(host.IsRunning);

        window.Close();

        Assert.True(host.IsRunning, "the window is not the runtime owner: closing it must not stop mapping");
    }

    [AvaloniaFact]
    public void A_Sample_Queued_Before_The_Close_Is_Dropped_By_The_Generation_Token()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        host.TickOnce(Tick);
        Dispatcher.UIThread.RunJobs();

        // Queue a press, then close before the queued callback runs.
        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.B, 0f, 0f, 0f, 0f));
        host.TickOnce(Tick);

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewModel.SelectedSource);
    }

    [AvaloniaFact]
    public void A_Window_Built_After_A_Close_Subscribes_Exactly_Once()
    {
        var (first, viewModel, host, _) = BuildWindow();

        first.Close();
        Assert.Equal(0, WindowSubscriptionCount(host, nameof(EngineHost.Sampled)));

        var second = new MainWindow(viewModel, host);
        second.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, WindowSubscriptionCount(host, nameof(EngineHost.Sampled)));
        Assert.Equal(1, WindowSubscriptionCount(host, nameof(EngineHost.PresenceSampled)));
        Assert.Equal(1, WindowSubscriptionCount(viewModel, nameof(MainWindowViewModel.PropertyChanged)));
    }

    [AvaloniaFact]
    public void A_Recreated_Window_Opens_In_Scope_On_The_Mapping_Tab()
    {
        var (first, viewModel, host, provider) = BuildWindow();

        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        host.TickOnce(Tick);
        Dispatcher.UIThread.RunJobs();

        first.Close();
        Assert.False(viewModel.IsPhysicalInputInScope);

        var second = new MainWindow(viewModel, host);
        second.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(second.IsVisible);
        Assert.Equal(0, second.FindControl<TabControl>("MainTabs")!.SelectedIndex);

        RaiseActivated(second);

        Assert.True(viewModel.IsPhysicalInputInScope, "the new window must open with physical input in scope");
    }

    /// <summary>
    /// The render process is stateless: the window IS the process. Closing it shuts the
    /// desktop lifetime down, which ends the process immediately - unsaved drafts are
    /// discarded by design - while the tray and the daemon stay resident and keep mapping.
    /// </summary>
    [AvaloniaFact]
    public void Closing_The_Window_Ends_The_Render_Process()
    {
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var (_, viewModel, host, _) = BuildWindow();

        var window = RenderProcess.InstallWindow(lifetime, viewModel, host, null!);

        Assert.Equal(ShutdownMode.OnMainWindowClose, lifetime.ShutdownMode);
        Assert.Same(window, lifetime.MainWindow);

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible, "the render process's window is gone with the process");
    }
}
