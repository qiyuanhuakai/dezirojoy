using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views.Controls;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The input-backend section of the daemon settings card. The platform backend reads as
/// checked by default, SDL3 is an explicit opt-in, and applying it persists the selection
/// and asks for the safe restart - while the running backend the daemon reports is shown
/// beside it as its own line, so a switch that has not taken effect is visible.
/// </summary>
public sealed class InputBackendSettingsGuiTests
{
    [AvaloniaFact]
    public void The_Platform_Backend_Reads_As_The_Default_Selection()
    {
        var (_, card) = ShowCard(CreateViewModel());

        Assert.True(card.FindControl<RadioButton>("PlatformBackendOption")!.IsChecked);
        Assert.False(card.FindControl<RadioButton>("Sdl3BackendOption")!.IsChecked!.Value);
        Assert.Contains("未知", card.FindControl<TextBlock>("RunningBackendText")!.Text!);
        Assert.False(card.FindControl<TextBlock>("RunningBackendErrorText")!.IsEffectivelyVisible);
        Assert.False(card.FindControl<TextBlock>("BackendMismatchText")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Choosing_Sdl3_And_Applying_Persists_The_Selection()
    {
        var store = new InMemoryInputBackendStore();
        var restarts = 0;
        var viewModel = CreateViewModel(store, () =>
        {
            restarts++;
            return Task.FromResult(true);
        });
        var (window, card) = ShowCard(viewModel);

        Click(card.FindControl<RadioButton>("Sdl3BackendOption")!, window);

        Assert.True(viewModel.IsSdl3BackendSelected);
        Assert.Equal(InputBackend.Sdl3, viewModel.SelectedBackend);

        Click(card.FindControl<Button>("ApplyBackendButton")!, window);

        Assert.Equal(1, restarts);
        Assert.Equal(InputBackend.Sdl3, store.Load());
        Assert.Contains("重启", card.FindControl<TextBlock>("BackendStatusText")!.Text!);
    }

    [AvaloniaFact]
    public void The_Running_Backend_The_Daemon_Reports_Gets_Its_Own_Lines()
    {
        var viewModel = CreateViewModel();
        var (_, card) = ShowCard(viewModel);

        viewModel.ReportRunningBackend(InputBackend.Sdl3, "SDL3 输入后端初始化失败：libSDL3.so 缺失");
        Dispatcher.UIThread.RunJobs();

        var running = card.FindControl<TextBlock>("RunningBackendText")!;
        Assert.Contains("SDL3", running.Text!);

        var error = card.FindControl<TextBlock>("RunningBackendErrorText")!;
        Assert.True(error.IsEffectivelyVisible);
        Assert.Contains("libSDL3.so", error.Text!);
        Assert.Contains("未切换回其他后端", error.Text!);

        // The card still shows the platform selection, and says the two differ.
        var mismatch = card.FindControl<TextBlock>("BackendMismatchText")!;
        Assert.True(mismatch.IsEffectivelyVisible);
        Assert.Contains("重启常驻服务后生效", mismatch.Text!);
    }

    private static DaemonSettingsViewModel CreateViewModel(
        InMemoryInputBackendStore? store = null,
        Func<Task<bool>>? restartDaemon = null)
        => new(new FakeDaemonService(), backendStore: store ?? new InMemoryInputBackendStore(), restartDaemon: restartDaemon);

    private static (Window Window, DaemonSettingsCard Card) ShowCard(DaemonSettingsViewModel viewModel)
    {
        var card = new DaemonSettingsCard { DataContext = viewModel };
        var window = new Window
        {
            Width = 480,
            Height = 800,
            Content = card,
        };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, card);
    }

    private static void Click(Control target, Window window)
    {
        window.Focus();
        Dispatcher.UIThread.RunJobs();

        window.UpdateLayout();
        target.BringIntoView();
        window.UpdateLayout();
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
