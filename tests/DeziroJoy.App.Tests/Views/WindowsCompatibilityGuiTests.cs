using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.Platform.Windows.Gaming;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

public sealed class WindowsCompatibilityGuiTests
{
    [AvaloniaFact]
    public void Windows_Keeps_The_Guide_Note_On_Mapping_And_GameBar_Control_In_Settings()
    {
        var guard = new FakeGameBarPopupGuard();
        var provider = new FakeGamepadProvider();
        using var host = new EngineHost(provider, new RecordingOutputSink(), new InMemoryProfileStore());
        var vm = new MainWindowViewModel(
            host,
            new InMemoryProfileStore(),
            guideButtonSupported: false,
            gameBarPopupGuard: guard);
        var window = new MainWindow(vm, host);
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.FindControl<TextBlock>("GuidePlatformNote")!.IsVisible);
        Assert.DoesNotContain(
            window.FindControl<Border>("RightPanel")!.GetVisualDescendants().OfType<Button>(),
            control => control.Name == "DisableGameBarControllerLaunchButton");

        vm.OpenSettingsCommand.Execute(null);
        window.UpdateLayout();
        // The surface is open, so the lazy host has built the panel.
        var settings = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
        var button = settings.FindControl<Button>("DisableGameBarControllerLaunchButton")!;
        Assert.True(button.IsVisible);

        Click(button, window);

        Assert.Equal(1, guard.DisableCalls);
        Assert.False(button.IsVisible);
        Assert.Contains("已写入并核验", settings.FindControl<TextBlock>("GameBarPopupHint")!.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Windows_Settings_Register_And_Restore_The_GameBar_Popup_Handler()
    {
        var guard = new FakeGameBarPopupGuard();
        var provider = new FakeGamepadProvider();
        using var host = new EngineHost(provider, new RecordingOutputSink(), new InMemoryProfileStore());
        var vm = new MainWindowViewModel(
            host,
            new InMemoryProfileStore(),
            guideButtonSupported: false,
            gameBarPopupGuard: guard);
        var window = new MainWindow(vm, host);
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        vm.OpenSettingsCommand.Execute(null);
        window.UpdateLayout();
        // The surface is open, so the lazy host has built the panel.
        var settings = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
        var register = settings.FindControl<Button>("RegisterGameBarPopupHandlerButton")!;
        var restore = settings.FindControl<Button>("RestoreGameBarPopupHandlerButton")!;
        Assert.True(register.IsVisible);
        Assert.False(restore.IsVisible);
        Assert.Contains("未注册", settings.FindControl<TextBlock>("GameBarHandlerStatus")!.Text);

        Click(register, window);

        Assert.Equal(1, guard.RegisterCalls);
        Assert.False(register.IsVisible);
        Assert.True(restore.IsVisible);
        Assert.Contains("已为本用户注册静默处理程序", settings.FindControl<TextBlock>("GameBarHandlerStatus")!.Text);

        Click(restore, window);

        Assert.Equal(1, guard.RestoreCalls);
        Assert.True(register.IsVisible);
        Assert.False(restore.IsVisible);
        Assert.Contains("未注册", settings.FindControl<TextBlock>("GameBarHandlerStatus")!.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Windows_Settings_Offer_Re_Registration_For_A_Stale_GameBar_Handler()
    {
        var guard = new FakeGameBarPopupGuard(handlerOwned: true, handlerRegistered: false);
        var provider = new FakeGamepadProvider();
        using var host = new EngineHost(provider, new RecordingOutputSink(), new InMemoryProfileStore());
        var vm = new MainWindowViewModel(
            host,
            new InMemoryProfileStore(),
            guideButtonSupported: false,
            gameBarPopupGuard: guard);
        var window = new MainWindow(vm, host);
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        vm.OpenSettingsCommand.Execute(null);
        window.UpdateLayout();
        // The surface is open, so the lazy host has built the panel.
        var settings = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
        var register = settings.FindControl<Button>("RegisterGameBarPopupHandlerButton")!;
        var restore = settings.FindControl<Button>("RestoreGameBarPopupHandlerButton")!;

        Assert.True(register.IsVisible);
        Assert.True(restore.IsVisible);
        Assert.Equal("重新注册静默处理程序", register.Content);
        Assert.Contains("重新注册", settings.FindControl<TextBlock>("GameBarHandlerStatus")!.Text);

        Click(register, window);

        Assert.Equal(1, guard.RegisterCalls);
        Assert.False(register.IsVisible);
        Assert.True(restore.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Selecting_Guide_On_Windows_Shows_The_Contextual_Warning()
    {
        var provider = new FakeGamepadProvider();
        var store = new InMemoryProfileStore();
        using var host = new EngineHost(provider, new RecordingOutputSink(), store);
        var vm = new MainWindowViewModel(
            host,
            store,
            guideButtonSupported: false,
            gameBarPopupGuard: new FakeGameBarPopupGuard());
        var window = new MainWindow(vm, host);
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var guide = window.FindControl<DeziroJoy.App.Views.Controls.GamepadVisualizer>("GamepadVisualizer")!
            .FindControl<Control>("GuideButton")!;
        Click(guide, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var editor = window.GetLogicalDescendants().OfType<MappingEditorPanel>().First();
        var hint = editor.FindControl<TextBlock>("GuideUnavailableHint")!;
        Assert.True(vm.IsEditing);
        Assert.True(hint.IsVisible);
        Assert.Contains("XInput", hint.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Windows_Compatibility_Card_Does_Not_Shrink_The_Model()
    {
        var withWindowsHints = ModelHeight(guideButtonSupported: false);
        var withoutWindowsHints = ModelHeight(guideButtonSupported: true);

        Assert.Equal(withoutWindowsHints, withWindowsHints);
    }

    private static double ModelHeight(bool guideButtonSupported)
    {
        var provider = new FakeGamepadProvider();
        var store = new InMemoryProfileStore();
        using var host = new EngineHost(provider, new RecordingOutputSink(), store);
        var vm = new MainWindowViewModel(
            host,
            store,
            guideButtonSupported: guideButtonSupported,
            gameBarPopupGuard: new FakeGameBarPopupGuard());
        var window = new MainWindow(vm, host);
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var model = window.FindControl<DeziroJoy.App.Views.Controls.GamepadVisualizer>("GamepadVisualizer")!;
        var height = model.GetVisualAncestors().OfType<Viewbox>().First().Bounds.Height;
        window.Close();
        return height;
    }

    private static void Click(Control target, Window window)
    {
        window.UpdateLayout();
        var point = target.TranslatePoint(
            new Point(target.Bounds.Width / 2, target.Bounds.Height / 2),
            window) ?? default;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class FakeGameBarPopupGuard : IGameBarPopupGuard
    {
        private bool _isControllerLaunchDisabled;
        private bool _ownsProtocolHandler;
        private bool _isProtocolHandlerRegistered;
        private bool _isProtocolHandlerTakenOver;

        public FakeGameBarPopupGuard(
            bool handlerOwned = false,
            bool handlerRegistered = false,
            bool handlerTakenOver = false)
        {
            _ownsProtocolHandler = handlerOwned;
            _isProtocolHandlerRegistered = handlerRegistered;
            _isProtocolHandlerTakenOver = handlerTakenOver;
        }

        public bool IsSupported => true;

        public bool IsControllerLaunchDisabled => _isControllerLaunchDisabled;

        public bool OwnsProtocolHandler => _ownsProtocolHandler;

        public bool IsProtocolHandlerRegistered => _isProtocolHandlerRegistered;

        public bool IsProtocolHandlerTakenOver => _isProtocolHandlerTakenOver;

        public int DisableCalls { get; private set; }

        public int RegisterCalls { get; private set; }

        public int RestoreCalls { get; private set; }

        public int OpenSettingsCalls { get; private set; }

        public void DisableControllerLaunch()
        {
            DisableCalls++;
            _isControllerLaunchDisabled = true;
        }

        public void RegisterProtocolHandler()
        {
            RegisterCalls++;
            _ownsProtocolHandler = true;
            _isProtocolHandlerRegistered = true;
        }

        public void RestoreProtocolHandler()
        {
            RestoreCalls++;
            _ownsProtocolHandler = false;
            _isProtocolHandlerRegistered = false;
        }

        public void OpenSystemSettings() => OpenSettingsCalls++;
    }
}
