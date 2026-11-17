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
/// The rendering section of the daemon settings card. The section is offered on Windows alone
/// - through the view model's platform seam, so both sides are exercised here - and applying
/// a choice persists the selection while the card says a restart is what makes it matter.
/// </summary>
public sealed class RenderModeSettingsGuiTests : IDisposable
{
    private readonly string _directory;

    public RenderModeSettingsGuiTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-render-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public void The_Section_Is_Not_Offered_Where_The_Software_Mode_Does_Not_Exist()
    {
        var (_, card) = ShowCard(CreateViewModel(isWindows: () => false));

        Assert.False(card.FindControl<Border>("RenderModeSection")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_Platform_Detection_Reads_As_The_Default_Selection()
    {
        var (_, card) = ShowCard(CreateViewModel(isWindows: () => true));

        Assert.True(card.FindControl<Border>("RenderModeSection")!.IsEffectivelyVisible);
        Assert.True(card.FindControl<RadioButton>("DefaultRenderOption")!.IsChecked);
        Assert.False(card.FindControl<RadioButton>("SoftwareRenderOption")!.IsChecked!.Value);
        Assert.False(card.FindControl<TextBlock>("RenderModeStatusText")!.IsEffectivelyVisible);
        Assert.Contains("平台检测", card.FindControl<RadioButton>("DefaultRenderOption")!.Content!.ToString());
    }

    [AvaloniaFact]
    public void Choosing_Software_And_Applying_Persists_The_Selection()
    {
        var viewModel = CreateViewModel(() => true);
        var (window, card) = ShowCard(viewModel);

        Click(card.FindControl<RadioButton>("SoftwareRenderOption")!, window);

        Assert.True(viewModel.IsSoftwareRenderSelected);
        Assert.Equal(AppRenderMode.Software, viewModel.SelectedRenderMode);
        Assert.False(card.FindControl<TextBlock>("RenderModeStatusText")!.IsEffectivelyVisible);

        Click(card.FindControl<Button>("ApplyRenderModeButton")!, window);

        // The apply writes through the shared settings service, so a second service over the
        // same directory - the next GUI start - reads the selection back.
        Assert.Equal(AppRenderMode.Software, new ThemeService(_directory).RenderMode);
        var status = card.FindControl<TextBlock>("RenderModeStatusText")!;
        Assert.True(status.IsEffectivelyVisible);
        Assert.Contains("重启主程序", status.Text!);
    }

    private DaemonSettingsViewModel CreateViewModel(Func<bool> isWindows)
        => new(new FakeDaemonService(), settings: new ThemeService(_directory), isWindows: isWindows);

    private static (Window Window, DaemonSettingsCard Card) ShowCard(DaemonSettingsViewModel viewModel)
    {
        var card = new DaemonSettingsCard { DataContext = viewModel };
        var window = new Window
        {
            Width = 480,
            Height = 800,
            Content = new ScrollViewer { Content = card },
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
