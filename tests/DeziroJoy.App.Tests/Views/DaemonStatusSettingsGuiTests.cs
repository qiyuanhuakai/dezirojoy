using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
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
}
