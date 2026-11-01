using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

public sealed class WindowShortcutGuiTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "dezirojoy-window-shortcut-tests",
        Guid.NewGuid().ToString("N"));

    private static (MainWindow Window, MainWindowViewModel ViewModel) BuildWindow(string directory)
    {
        var provider = new FakeGamepadProvider();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, new RecordingOutputSink(), store);
        var viewModel = new MainWindowViewModel(host, store);
        var window = new MainWindow(viewModel, host, new ThemeService(directory));
        window.Show();
        return (window, viewModel);
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers, PhysicalKey physicalKey)
        => window.KeyPress(key, modifiers, physicalKey, null);

    [AvaloniaFact]
    public void Ctrl_Number_Selects_Each_Tab_Exactly_Once()
    {
        var (window, _) = BuildWindow(_directory);
        var tabs = window.FindControl<TabControl>("MainTabs")!;

        Press(window, Key.D2, RawInputModifiers.Control, PhysicalKey.Digit2);
        Assert.Equal(1, tabs.SelectedIndex);
        Press(window, Key.D3, RawInputModifiers.Control, PhysicalKey.Digit3);
        Assert.Equal(2, tabs.SelectedIndex);
        Press(window, Key.D1, RawInputModifiers.Control, PhysicalKey.Digit1);
        Assert.Equal(0, tabs.SelectedIndex);
    }

    [AvaloniaFact]
    public void Ctrl_Comma_Opens_And_Closes_Settings_Without_Toggling_Twice()
    {
        var (window, viewModel) = BuildWindow(_directory);

        Press(window, Key.OemComma, RawInputModifiers.Control, PhysicalKey.Comma);
        Assert.True(viewModel.IsSettingsOpen);
        Press(window, Key.OemComma, RawInputModifiers.Control, PhysicalKey.Comma);
        Assert.False(viewModel.IsSettingsOpen);
    }

    [AvaloniaFact]
    public void Capture_Suppresses_The_Global_Settings_Shortcut()
    {
        var (window, viewModel) = BuildWindow(_directory);
        viewModel.Editor.IsCapturingKey = true;

        Press(window, Key.OemComma, RawInputModifiers.Control, PhysicalKey.Comma);

        Assert.False(viewModel.IsSettingsOpen);
    }

    [AvaloniaFact]
    public void Settings_Escape_Closes_The_Topmost_Layer_And_Stops_Capture_Without_Canceling_The_Editor()
    {
        var (window, viewModel) = BuildWindow(_directory);
        viewModel.IsEditing = true;
        viewModel.Editor.IsCapturingKey = true;
        viewModel.IsSettingsOpen = true;

        Press(window, Key.Escape, RawInputModifiers.None, PhysicalKey.Escape);

        Assert.False(viewModel.IsSettingsOpen);
        Assert.False(viewModel.Editor.IsCapturingKey);
        Assert.True(viewModel.IsEditing);
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
    }
}
