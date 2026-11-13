using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The 前台规则 surface as the window hosts it: reachable, and reading the live watcher rather
/// than a copy of it. The watcher belongs to the view model and outlives the window in daemon
/// mode, so what the window adds is the poll that drives it and the panel that explains it -
/// and what it must give back on close is both, so a window built afterwards starts clean.
/// </summary>
public sealed class MainWindowForegroundRulesTests
{
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

    private static (TabItem Tab, ForegroundRulesPanel Panel) RulesSurface(MainWindow window)
    {
        var tab = window.GetVisualDescendants()
            .OfType<TabItem>()
            .Single(item => (string?)item.Header == "前台规则");
        var panel = Assert.IsType<ForegroundRulesPanel>(tab.Content);
        return (tab, panel);
    }

    [AvaloniaFact]
    public void The_Rules_Panel_Is_Reachable_From_The_Window_And_Explains_The_Active_Rule()
    {
        var (window, viewModel) = BuildWindow();
        var (tab, panel) = RulesSurface(window);

        Assert.IsType<ForegroundRulesViewModel>(panel.DataContext);

        tab.IsSelected = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // The desktop default is what runs with no rule to match, and the panel says so - the
        // explanation is the feature's whole point, so it has to reach the screen.
        viewModel.Foreground.Update(new ForegroundSettings { DefaultSlot = 3 });
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var reason = panel.FindControl<TextBlock>("ActiveReasonText")!;
        var profile = panel.FindControl<TextBlock>("ActiveProfileText")!;

        Assert.True(reason.IsEffectivelyVisible);
        Assert.Contains("预设 3", reason.Text);
        Assert.Contains("预设 3", profile.Text);
    }

    [AvaloniaFact]
    public void The_Panel_Reads_The_Live_Watcher_Rather_Than_A_Copy_Of_It()
    {
        var (window, viewModel) = BuildWindow();
        var (_, panel) = RulesSurface(window);

        viewModel.Foreground.Update(new ForegroundSettings
        {
            DefaultSlot = 1,
            Rules = [new ForegroundRule { ProcessName = "game", Priority = 5, Slot = 4 }],
        });
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var reason = panel.FindControl<TextBlock>("ActiveReasonText")!;

        // No program is in front on a headless box, so the rule cannot match and the desktop
        // default is what the panel must be quoting - from the watcher, not from a snapshot of it.
        Assert.Contains("预设 1", reason.Text);
        Assert.DoesNotContain("game", reason.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Closing_The_Window_Releases_The_Rules_Panel_So_A_Second_Window_Starts_Clean()
    {
        var (window, _) = BuildWindow();
        var (_, panel) = RulesSurface(window);
        var viewModel = Assert.IsType<ForegroundRulesViewModel>(panel.DataContext);

        window.Close();
        Dispatcher.UIThread.RunJobs();

        // The panel is gone with the window: the tab it lived in is removed, so a second window
        // hosts its own instead of stacking a second subscription on the same watcher.
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TabItem>(), item => item.Content == panel);

        var (second, _) = BuildWindow();
        var (_, secondPanel) = RulesSurface(second);

        Assert.NotSame(panel, secondPanel);
        Assert.NotSame(viewModel, secondPanel.DataContext);
    }
}
