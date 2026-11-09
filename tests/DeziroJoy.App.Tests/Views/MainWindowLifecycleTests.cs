using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeziroJoy.App.Hosting;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using System.Diagnostics;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The main window is a releasable presentation resource, not the owner of the runtime.
/// Closing it must detach everything it attached - both timers, both host sampling
/// subscriptions, the view-model listener - drop callbacks that were already queued, and
/// leave the presentation host running: production mapping belongs to the daemon and continues
/// with no window open. A window built afterwards starts from a clean slate, so a
/// destroy/recreate cycle never stacks a second set of subscriptions.
/// </summary>
public sealed class MainWindowLifecycleTests : IDisposable
{
    private const string DeviceId = "pad-01";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 64.0);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "dezirojoy-window-lifecycle-tests",
        Guid.NewGuid().ToString("N"));

    public MainWindowLifecycleTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

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

    [AvaloniaFact]
    public void Closing_With_Close_To_Tray_Acknowledges_The_Tray_Before_Ending_The_Render_Process()
    {
        var themes = new ThemeService(_directory);
        var fixture = BuildRender(themes);

        Assert.Equal(ShutdownMode.OnExplicitShutdown, fixture.Lifetime.ShutdownMode);
        fixture.Window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["spawn", "release", "probe", "shutdown"], fixture.Events);
        Assert.Contains("--tray", Assert.Single(fixture.Spawned).Arguments, StringComparison.Ordinal);
        Assert.False(fixture.Window.IsVisible);
        Assert.Equal(0, fixture.Tray.KillCalls);
    }

    [AvaloniaFact]
    public void Closing_With_Close_To_Tray_Disabled_Stops_The_Daemon_Without_Spawning_A_Tray()
    {
        var themes = new ThemeService(_directory);
        themes.SetCloseToTrayEnabled(false);
        var fixture = BuildRender(themes);

        fixture.Window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["stop-daemon", "shutdown"], fixture.Events);
        Assert.Empty(fixture.Spawned);
        Assert.False(fixture.Window.IsVisible);
    }

    [AvaloniaFact]
    public void Ordinary_Minimize_Keeps_The_Render_Process_And_Daemon_Alive()
    {
        var themes = new ThemeService(_directory);
        themes.SetMinimizeToTrayEnabled(false);
        var fixture = BuildRender(themes);

        fixture.Window.RequestMinimize();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(WindowState.Minimized, fixture.Window.WindowState);
        Assert.True(fixture.Window.IsVisible);
        Assert.Empty(fixture.Events);
        Assert.Empty(fixture.Spawned);
    }

    [AvaloniaFact]
    public void A_Failed_Minimize_Handover_Restores_And_Keeps_The_Window()
    {
        var themes = new ThemeService(_directory);
        var fixture = BuildRender(themes, probe: () => Task.FromResult(false));

        fixture.Window.RequestMinimize();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(WindowState.Normal, fixture.Window.WindowState);
        Assert.True(fixture.Window.IsVisible);
        Assert.Equal(1, fixture.Tray.KillCalls);
        Assert.Equal(["spawn", "release", "probe", "reacquire"], fixture.Events);
        Assert.Empty(fixture.Shutdowns);
    }

    [AvaloniaFact]
    public void Minimize_To_Tray_Hands_Over_Even_When_Close_To_Tray_Is_Disabled()
    {
        var themes = new ThemeService(_directory);
        themes.SetCloseToTrayEnabled(false);
        var fixture = BuildRender(themes);

        fixture.Window.RequestMinimize();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["spawn", "release", "probe", "shutdown"], fixture.Events);
        Assert.DoesNotContain("stop-daemon", fixture.Events);
        Assert.False(fixture.Window.IsVisible);
    }

    [AvaloniaFact]
    public void A_Failed_Tray_Handover_Rolls_Back_And_Keeps_The_Window()
    {
        var themes = new ThemeService(_directory);
        var fixture = BuildRender(themes, probe: () => Task.FromResult(false));

        fixture.Window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, fixture.Tray.KillCalls);
        Assert.Equal(["spawn", "release", "probe", "reacquire"], fixture.Events);
        Assert.True(fixture.Window.IsVisible);
        Assert.Empty(fixture.Shutdowns);
    }

    [AvaloniaFact]
    public void Closing_After_A_Third_Party_Takes_The_Lease_Retires_Only_The_Unlocked_Render()
    {
        var themes = new ThemeService(_directory);
        var identity = new GuiInstanceIdentity(Path.Combine(_directory, "handover-race.lock"), "unused");
        using var oldRenderLease = GuiInstanceLease.TryAcquire(identity)!;
        GuiInstanceLease? newRenderLease = null;
        RenderFixture? fixture = null;
        fixture = BuildRender(
            themes,
            probe: () =>
            {
                // Once this lease moves, the third-party render is free to build its window.
                newRenderLease = GuiInstanceLease.TryAcquire(identity);
                fixture!.Events.Add("third-party-render-window");
                return Task.FromResult(false);
            },
            releaseInstance: oldRenderLease.Dispose,
            reacquireInstance: () =>
            {
                var reclaimed = GuiInstanceLease.TryAcquire(identity);
                reclaimed?.Dispose();
                return reclaimed is not null;
            });

        try
        {
            fixture.Window.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(newRenderLease);
            Assert.Equal(1, fixture.Tray.KillCalls);
            Assert.False(fixture.Window.IsVisible);
            Assert.Single(fixture.Shutdowns);
            Assert.DoesNotContain("stop-daemon", fixture.Events);
            Assert.Equal(
                ["spawn", "release", "probe", "third-party-render-window", "reacquire", "shutdown"],
                fixture.Events);
        }
        finally
        {
            newRenderLease?.Dispose();
        }
    }

    [AvaloniaFact]
    public void Minimize_After_A_Third_Party_Takes_The_Lease_Also_Retires_The_Unlocked_Render()
    {
        var themes = new ThemeService(_directory);
        var fixture = BuildRender(
            themes,
            probe: () => Task.FromResult(false),
            reacquireInstance: () => false);

        fixture.Window.RequestMinimize();
        Dispatcher.UIThread.RunJobs();

        Assert.False(fixture.Window.IsVisible);
        Assert.Single(fixture.Shutdowns);
        Assert.DoesNotContain("stop-daemon", fixture.Events);
        Assert.Equal(["spawn", "release", "probe", "reacquire", "shutdown"], fixture.Events);
    }

    [AvaloniaFact]
    public void Concurrent_Close_And_Minimize_Share_One_Tray_Handover()
    {
        var themes = new ThemeService(_directory);
        var probe = new TaskCompletionSource<bool>();
        var fixture = BuildRender(themes, probe: () => probe.Task);

        fixture.Window.Close();
        fixture.Window.RequestMinimize();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(fixture.Spawned);
        Assert.Equal(1, fixture.Events.Count(entry => entry == "probe"));

        probe.SetResult(true);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(fixture.Shutdowns);
        Assert.Single(fixture.Spawned);
    }

    [AvaloniaFact]
    public void A_Failed_Daemon_Stop_Keeps_The_Window_Open_For_Retry()
    {
        var themes = new ThemeService(_directory);
        themes.SetCloseToTrayEnabled(false);
        var stops = 0;
        var fixture = BuildRender(themes, stopDaemon: () => Task.FromResult(++stops > 1));

        fixture.Window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.True(fixture.Window.IsVisible);
        Assert.Empty(fixture.Shutdowns);
        Assert.Empty(fixture.Spawned);
        Assert.Equal(["stop-daemon"], fixture.Events);

        fixture.Window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(fixture.Window.IsVisible);
        Assert.Single(fixture.Shutdowns);
        Assert.Equal(["stop-daemon", "stop-daemon", "shutdown"], fixture.Events);
    }

    private static RenderFixture BuildRender(
        ThemeService themes,
        Func<Task<bool>>? probe = null,
        Func<Task<bool>>? stopDaemon = null,
        Action? releaseInstance = null,
        Func<bool>? reacquireInstance = null)
    {
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var (_, viewModel, host, _) = BuildWindow();
        var tray = new FakeTrayProcess();
        var fixture = new RenderFixture(lifetime, tray);

        var window = RenderProcess.InstallWindow(
            lifetime,
            viewModel,
            host,
            themes,
            spawnTray: info =>
            {
                fixture.Spawned.Add(info);
                fixture.Events.Add("spawn");
                return tray;
            },
            shutdown: () =>
            {
                fixture.Shutdowns.Add(1);
                fixture.Events.Add("shutdown");
            },
            releaseInstance: () =>
            {
                fixture.Events.Add("release");
                releaseInstance?.Invoke();
            },
            reacquireInstance: () =>
            {
                fixture.Events.Add("reacquire");
                return reacquireInstance?.Invoke() ?? true;
            },
            probe: (_, _, _, _) =>
            {
                fixture.Events.Add("probe");
                return probe?.Invoke() ?? Task.FromResult(true);
            },
            pollDelay: static () => Task.CompletedTask,
            handoverBudget: TimeSpan.Zero,
            stopDaemon: () =>
            {
                fixture.Events.Add("stop-daemon");
                return stopDaemon?.Invoke() ?? Task.FromResult(true);
            });
        fixture.Window = window;
        return fixture;
    }

    private sealed class RenderFixture(
        ClassicDesktopStyleApplicationLifetime lifetime,
        FakeTrayProcess tray)
    {
        public ClassicDesktopStyleApplicationLifetime Lifetime { get; } = lifetime;

        public MainWindow Window { get; set; } = null!;

        public FakeTrayProcess Tray { get; } = tray;

        public List<ProcessStartInfo> Spawned { get; } = [];

        public List<int> Shutdowns { get; } = [];

        public List<string> Events { get; } = [];
    }

    private sealed class FakeTrayProcess : IProcessHandle
    {
        public bool HasExited { get; private set; }

        public int KillCalls { get; private set; }

        public event Action? Exited;

        public void Kill(bool entireTree)
        {
            KillCalls++;
            HasExited = true;
            Exited?.Invoke();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
