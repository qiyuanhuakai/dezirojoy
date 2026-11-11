using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.Tests.ViewModels;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.App.Views.Controls;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

public sealed class DaemonStatusSettingsGuiTests
{
    [AvaloniaFact]
    public void Settings_Render_The_Actual_Daemon_Identity_Revision_Counters_And_Error()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dezirojoy-daemon-status-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new InMemoryProfileStore();
            var host = new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store);
            var viewModel = new MainWindowViewModel(host, store, daemonRequired: true)
            {
                DaemonSnapshot = new StateSnapshot(
                    Sequence: 7,
                    Revision: 3,
                    Running: true,
                    MappingEnabled: true,
                    InjectorPid: 4242,
                    InjectorIntegrity: "high",
                    ProfileName: "Arena",
                    LastError: "target-error",
                    InjectedEvents: 10,
                    BlockedEvents: 2,
                    HasController: true),
                IsSettingsOpen = true,
            };
            var window = new MainWindow(viewModel, host, new ThemeService(directory));
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var panel = window.FindControl<SettingsPanel>("SettingsPanelHost")!;
            Assert.Equal("high", panel.FindControl<TextBlock>("InjectorIntegrityText")!.Text);
            Assert.Equal("3", panel.FindControl<TextBlock>("DaemonRevisionText")!.Text);
            Assert.Equal("10", panel.FindControl<TextBlock>("InjectedEventsText")!.Text);
            Assert.Equal("2", panel.FindControl<TextBlock>("BlockedEventsText")!.Text);
            Assert.Equal("target-error", panel.FindControl<TextBlock>("DaemonLastErrorText")!.Text);
            Assert.Contains("4242", viewModel.InjectorDescription);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [AvaloniaFact]
    public void Linux_Card_Offers_Start_Stop_And_Drops_The_Administrator_Hint()
    {
        var service = new DaemonSettingsViewModelLinuxTests.FakeLinuxDaemonService
        {
            IsDaemonInstalled = true,
            IsDaemonRunning = false,
        };
        var (_, card) = ShowCard(new DaemonSettingsViewModel(service));

        var start = card.FindControl<Button>("StartDaemonButton")!;
        var stop = card.FindControl<Button>("StopDaemonButton")!;

        Assert.True(card.FindControl<WrapPanel>("DaemonRuntimeRow")!.IsVisible);
        Assert.True(start.IsVisible);
        Assert.True(stop.IsVisible);
        Assert.True(start.IsEnabled);
        Assert.False(stop.IsEnabled);

        // The Windows "needs administrator" sentence is gone: installing a systemd user unit
        // needs no administrator, so the sentence would be false here.
        Assert.False(card.FindControl<TextBlock>("AutostartHintText")!.IsVisible);
        var linuxHint = card.FindControl<TextBlock>("AutostartHintLinuxText")!;
        Assert.True(linuxHint.IsVisible);
        Assert.DoesNotContain("管理员", linuxHint.Text!);
    }

    [AvaloniaFact]
    public void Linux_Card_Start_Button_Wires_The_Command_And_Flips_The_Buttons()
    {
        var service = new DaemonSettingsViewModelLinuxTests.FakeLinuxDaemonService { IsDaemonInstalled = true };
        var (window, card) = ShowCard(new DaemonSettingsViewModel(service));

        var start = card.FindControl<Button>("StartDaemonButton")!;
        Click(start, window);

        Assert.Equal(1, service.StartCalls);
        Assert.False(start.IsEnabled);
        Assert.True(card.FindControl<Button>("StopDaemonButton")!.IsEnabled);
        Assert.Contains("已启动", card.FindControl<TextBlock>("DaemonStatusText")!.Text);
        Assert.Contains("正在运行", card.FindControl<TextBlock>("DaemonStateText")!.Text);
    }

    [AvaloniaFact]
    public void Linux_Card_Shows_The_Enablement_As_Its_Own_Line()
    {
        var service = new DaemonSettingsViewModelLinuxTests.FakeLinuxDaemonService
        {
            IsDaemonInstalled = true,
            IsDaemonRunning = true,
            IsDaemonEnabled = true,
        };
        var vm = new DaemonSettingsViewModel(service);
        var (_, card) = ShowCard(vm);

        var enabled = card.FindControl<TextBlock>("DaemonEnabledText")!;
        Assert.True(enabled.IsVisible);
        Assert.Equal("已启用（登录自动）", enabled.Text);

        // The enablement flips on its own, without the run state moving with it.
        service.IsDaemonEnabled = false;
        vm.RefreshCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(enabled.IsVisible);
        Assert.Equal("未启用（登录不会自动启动）", enabled.Text);
        Assert.Contains("正在运行", card.FindControl<TextBlock>("DaemonStateText")!.Text);
    }

    [AvaloniaFact]
    public void Linux_Card_Reports_A_Failed_Query_Instead_Of_Not_Running()
    {
        var service = new DaemonSettingsViewModelLinuxTests.FakeLinuxDaemonService
        {
            IsDaemonInstalled = true,
            IsDaemonRunning = false,
            DaemonQueryError = "环境缺少 XDG_RUNTIME_DIR 与 DBUS_SESSION_BUS_ADDRESS，systemctl --user 连不上用户总线",
        };
        var (_, card) = ShowCard(new DaemonSettingsViewModel(service));

        var state = card.FindControl<TextBlock>("DaemonStateText")!;
        Assert.Contains("无法查询状态", state.Text);
        Assert.Contains("XDG_RUNTIME_DIR", state.Text);
        Assert.DoesNotContain("未运行", state.Text);

        // Nothing is known about the enablement either, so that line stays away.
        Assert.False(card.FindControl<TextBlock>("DaemonEnabledText")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Windows_Card_Keeps_Its_Wording_And_Hides_Start_Stop()
    {
        var (_, card) = ShowCard(new DaemonSettingsViewModel(
            new FakeDaemonService { IsDaemonInstalled = true }));

        // The Windows wording stays exactly what it was, administrator sentence included.
        var hint = card.FindControl<TextBlock>("AutostartHintText")!;
        Assert.True(hint.IsVisible);
        Assert.Equal("登录后启动主程序；常驻服务负责后台映射。安装或卸载需要管理员权限。", hint.Text);
        Assert.False(card.FindControl<TextBlock>("AutostartHintLinuxText")!.IsVisible);

        // schtasks has no standalone start or stop, so the card exposes neither.
        Assert.False(card.FindControl<WrapPanel>("DaemonRuntimeRow")!.IsVisible);
        Assert.False(card.FindControl<Button>("StartDaemonButton")!.IsEffectivelyVisible);
        Assert.False(card.FindControl<Button>("StopDaemonButton")!.IsEffectivelyVisible);

        // A scheduled task runs at logon once installed: no enablement line to show.
        Assert.False(card.FindControl<TextBlock>("DaemonEnabledText")!.IsEffectivelyVisible);
    }

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
