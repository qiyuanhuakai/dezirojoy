using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Windows.Gaming;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

public sealed class WindowsCompatibilityTests
{
    [Fact]
    public void Guide_Limitation_Follows_The_Platform_And_Selected_Source()
    {
        var editor = new MappingEditorViewModel(guideButtonSupported: false)
        {
            SelectedButton = GamepadButton.Guide,
        };

        Assert.True(editor.ShowGuideUnavailableHint);
        Assert.Contains("XInput", editor.GuideUnavailableHint);
        Assert.Contains("不会触发", editor.GuideUnavailableHint);

        editor.SelectedButton = GamepadButton.A;
        Assert.False(editor.ShowGuideUnavailableHint);

        editor.SelectedButton = GamepadButton.Guide;
        editor.SourceIsStick = true;
        Assert.False(editor.ShowGuideUnavailableHint);

        var supported = new MappingEditorViewModel(guideButtonSupported: true)
        {
            SelectedButton = GamepadButton.Guide,
        };
        Assert.False(supported.ShowGuideUnavailableHint);
    }

    [Fact]
    public void Windows_Compatibility_Command_Disables_Controller_Launch()
    {
        var guard = new FakeGameBarPopupGuard();
        using var host = CreateHost(out var store);
        var vm = new MainWindowViewModel(
            host,
            store,
            guideButtonSupported: false,
            gameBarPopupGuard: guard);

        Assert.True(vm.ShowGuidePlatformNote);
        Assert.True(vm.ShowGameBarPopupGuard);
        Assert.True(vm.CanDisableGameBarControllerLaunch);
        Assert.Contains("ms-gamebar", vm.GameBarPopupHint);

        vm.DisableGameBarControllerLaunchCommand.Execute(null);

        Assert.Equal(1, guard.DisableCalls);
        Assert.True(vm.IsGameBarControllerLaunchDisabled);
        Assert.False(vm.CanDisableGameBarControllerLaunch);
        Assert.Contains("已关闭", vm.GameBarPopupHint);
        Assert.False(vm.HasGameBarPopupError);
    }

    [Fact]
    public void GameBar_Setting_Failure_Is_Reported_Without_Changing_State()
    {
        var guard = new FakeGameBarPopupGuard { Failure = new UnauthorizedAccessException("denied") };
        using var host = CreateHost(out var store);
        var vm = new MainWindowViewModel(host, store, gameBarPopupGuard: guard);

        vm.DisableGameBarControllerLaunchCommand.Execute(null);

        Assert.False(vm.IsGameBarControllerLaunchDisabled);
        Assert.True(vm.CanDisableGameBarControllerLaunch);
        Assert.True(vm.HasGameBarPopupError);
        Assert.Contains("denied", vm.GameBarPopupError);
        Assert.Contains("系统设置", vm.GameBarPopupError);
    }

    [Fact]
    public void GameBar_Setting_Read_Failure_Is_Reported_Without_Blocking_Startup()
    {
        var guard = new FakeGameBarPopupGuard { ReadFailure = new UnauthorizedAccessException("read denied") };
        using var host = CreateHost(out var store);
        var vm = new MainWindowViewModel(host, store, gameBarPopupGuard: guard);

        Assert.True(vm.HasGameBarPopupError);
        Assert.Contains("read denied", vm.GameBarPopupError);
        Assert.True(vm.CanDisableGameBarControllerLaunch);
    }

    [Fact]
    public void Non_Windows_Platform_Hides_Both_Compatibility_Controls()
    {
        var guard = new FakeGameBarPopupGuard { IsSupported = false };
        using var host = CreateHost(out var store);
        var vm = new MainWindowViewModel(
            host,
            store,
            guideButtonSupported: true,
            gameBarPopupGuard: guard);

        Assert.False(vm.ShowGuidePlatformNote);
        Assert.False(vm.ShowGameBarPopupGuard);
        Assert.False(vm.CanDisableGameBarControllerLaunch);
    }

    private static EngineHost CreateHost(out InMemoryProfileStore store)
    {
        store = new InMemoryProfileStore();
        return new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store);
    }

    private sealed class FakeGameBarPopupGuard : IGameBarPopupGuard
    {
        public bool IsSupported { get; set; } = true;

        private bool _isControllerLaunchDisabled;

        public bool IsControllerLaunchDisabled => ReadFailure is null
            ? _isControllerLaunchDisabled
            : throw ReadFailure;

        public int DisableCalls { get; private set; }

        public Exception? Failure { get; init; }

        public Exception? ReadFailure { get; init; }

        public void DisableControllerLaunch()
        {
            DisableCalls++;
            if (Failure is not null)
            {
                throw Failure;
            }

            _isControllerLaunchDisabled = true;
        }
    }
}
