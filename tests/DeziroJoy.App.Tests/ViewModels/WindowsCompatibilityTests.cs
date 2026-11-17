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
        Assert.Contains("已关闭", vm.GameBarPreferenceStatus);
        Assert.DoesNotContain("已关闭", vm.GameBarPopupHint);
        Assert.False(vm.HasGameBarPopupError);
    }

    [Fact]
    public void Windows_Compatibility_Can_Open_The_System_Settings()
    {
        var guard = new FakeGameBarPopupGuard();
        using var host = CreateHost(out var store);
        var vm = new MainWindowViewModel(host, store, gameBarPopupGuard: guard);

        vm.OpenGameBarSettingsCommand.Execute(null);

        Assert.Equal(1, guard.OpenSettingsCalls);
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
        Assert.False(vm.CanRegisterGameBarPopupHandler);
        Assert.False(vm.CanRestoreGameBarPopupHandler);
    }

    [Fact]
    public void GameBar_Popup_Handler_Can_Be_Registered()
    {
        var guard = new FakeGameBarPopupGuard();
        using var host = CreateHost(out var store);
        var vm = new MainWindowViewModel(host, store, gameBarPopupGuard: guard);

        Assert.True(vm.CanRegisterGameBarPopupHandler);
        Assert.False(vm.CanRestoreGameBarPopupHandler);
        Assert.False(vm.IsGameBarPopupHandlerStale);
        Assert.Contains("未注册", vm.GameBarHandlerStatus);

        vm.RegisterGameBarPopupHandlerCommand.Execute(null);

        Assert.Equal(1, guard.RegisterCalls);
        Assert.True(vm.IsGameBarPopupHandlerRegistered);
        Assert.False(vm.CanRegisterGameBarPopupHandler);
        Assert.True(vm.CanRestoreGameBarPopupHandler);
        Assert.Contains("已注册", vm.GameBarHandlerStatus);
        Assert.DoesNotContain("已注册", vm.GameBarPopupHint);

        // Status and scope each appear once: the handler reports its state, while the shared
        // hint carries the residual popup limitation instead of repeating that state.
        Assert.Contains("ms-gamebar", vm.GameBarHandlerStatus);
        Assert.DoesNotContain("仍可能弹窗", vm.GameBarHandlerStatus);
        Assert.Contains("不保证抑制所有弹窗", vm.GameBarPopupHint);
        Assert.False(vm.HasGameBarPopupError);
    }

    [Fact]
    public void GameBar_Popup_Handler_Unregistered_State_Does_Not_Promise_Suppression()
    {
        var guard = new FakeGameBarPopupGuard();
        using var host = CreateHost(out var store);
        var vm = new MainWindowViewModel(host, store, gameBarPopupGuard: guard);

        Assert.Contains("未注册", vm.GameBarHandlerStatus);
        Assert.DoesNotContain("不保证抑制所有弹窗", vm.GameBarHandlerStatus);
        Assert.Contains("不保证抑制所有弹窗", vm.GameBarPopupHint);
    }

    [Fact]
    public void GameBar_Popup_Handler_Can_Be_Restored_To_The_System_Default()
    {
        var guard = new FakeGameBarPopupGuard(handlerOwned: true, handlerRegistered: true);
        using var host = CreateHost(out var store);
        var vm = new MainWindowViewModel(host, store, gameBarPopupGuard: guard);

        Assert.True(vm.CanRestoreGameBarPopupHandler);
        Assert.False(vm.CanRegisterGameBarPopupHandler);

        vm.RestoreGameBarPopupHandlerCommand.Execute(null);

        Assert.Equal(1, guard.RestoreCalls);
        Assert.False(vm.IsGameBarPopupHandlerRegistered);
        Assert.False(vm.OwnsGameBarPopupHandler);
        Assert.True(vm.CanRegisterGameBarPopupHandler);
        Assert.False(vm.CanRestoreGameBarPopupHandler);
        Assert.Contains("未注册", vm.GameBarHandlerStatus);
        Assert.False(vm.HasGameBarPopupError);
    }

    [Fact]
    public void GameBar_Popup_Handler_Stale_Registration_Offers_Re_Registration_And_Restore()
    {
        // Owned, but the recorded command names another path - an app that moved.
        var guard = new FakeGameBarPopupGuard(handlerOwned: true, handlerRegistered: false);
        using var host = CreateHost(out var store);
        var vm = new MainWindowViewModel(host, store, gameBarPopupGuard: guard);

        Assert.True(vm.IsGameBarPopupHandlerStale);
        Assert.True(vm.CanRegisterGameBarPopupHandler);
        Assert.True(vm.CanRestoreGameBarPopupHandler);
        Assert.Equal("重新注册静默处理程序", vm.RegisterGameBarPopupHandlerText);
        Assert.Contains("重新注册", vm.GameBarHandlerStatus);
        Assert.DoesNotContain("重新注册", vm.GameBarPopupHint);

        vm.RegisterGameBarPopupHandlerCommand.Execute(null);

        Assert.Equal(1, guard.RegisterCalls);
        Assert.True(vm.IsGameBarPopupHandlerRegistered);
        Assert.False(vm.IsGameBarPopupHandlerStale);
        Assert.Equal("注册静默处理程序", vm.RegisterGameBarPopupHandlerText);
    }

    [Fact]
    public void GameBar_Popup_Handler_Taken_Over_By_Another_Program_Offers_No_Destructive_Action()
    {
        // The marker is still ours, but another program replaced the command: restoring
        // would delete THEIR handler and re-registering would overwrite it, so neither is
        // offered and the state is reported instead of silently repaired.
        var guard = new FakeGameBarPopupGuard(handlerTakenOver: true);
        using var host = CreateHost(out var store);
        var vm = new MainWindowViewModel(host, store, gameBarPopupGuard: guard);

        Assert.True(vm.IsGameBarPopupHandlerTakenOver);
        Assert.False(vm.IsGameBarPopupHandlerStale);
        Assert.False(vm.OwnsGameBarPopupHandler);
        Assert.False(vm.IsGameBarPopupHandlerRegistered);
        Assert.False(vm.CanRegisterGameBarPopupHandler);
        Assert.False(vm.CanRestoreGameBarPopupHandler);
        Assert.Contains("已被其他程序接管", vm.GameBarHandlerStatus);
        Assert.Contains("不提供注册或恢复", vm.GameBarHandlerStatus);
        Assert.DoesNotContain("接管", vm.GameBarPopupHint);
    }

    [Fact]
    public void GameBar_Popup_Handler_Restore_Refuses_When_Taken_Over()
    {
        var guard = new FakeGameBarPopupGuard(handlerTakenOver: true);
        using var host = CreateHost(out var store);
        var vm = new MainWindowViewModel(host, store, gameBarPopupGuard: guard);

        // Reached only through a race or the CLI; the guard refuses and nothing changes.
        vm.RestoreGameBarPopupHandlerCommand.Execute(null);

        Assert.True(vm.HasGameBarPopupError);
        Assert.Contains("接管", vm.GameBarPopupError);
        Assert.Contains("未做任何改动", vm.GameBarPopupError);
    }

    [Fact]
    public void GameBar_Popup_Copy_States_The_Scope_Instead_Of_Promising_WinG_And_Recording()
    {
        // The no-op handler answers ms-gamebar URIs, and some launch paths go through that
        // URI, so the copy may only claim what is true: no system recording settings are
        // touched, nothing outside the URI is intercepted, and suppression is never promised.
        var guard = new FakeGameBarPopupGuard();
        using var host = CreateHost(out var store);
        var vm = new MainWindowViewModel(host, store, gameBarPopupGuard: guard);

        Assert.Contains("不保证抑制所有弹窗", vm.GameBarPopupHint);
        Assert.Contains("不修改录制设置", vm.GameBarPopupHint);
        Assert.DoesNotContain("不影响 Win+G", vm.GameBarPopupHint);

        vm.RegisterGameBarPopupHandlerCommand.Execute(null);

        Assert.Contains("不主动拦截其他启动方式", vm.GameBarPopupHint);
        Assert.Contains("不修改录制设置", vm.GameBarPopupHint);
        Assert.Contains("不保证抑制所有弹窗", vm.GameBarPopupHint);
        Assert.DoesNotContain("不影响 Win+G", vm.GameBarPopupHint);
    }

    [Fact]
    public void GameBar_Popup_Handler_Registration_Failure_Is_Reported_Without_Changing_State()    {
        var guard = new FakeGameBarPopupGuard
        {
            RegisterFailure = new UnauthorizedAccessException("denied"),
        };
        using var host = CreateHost(out var store);
        var vm = new MainWindowViewModel(host, store, gameBarPopupGuard: guard);

        vm.RegisterGameBarPopupHandlerCommand.Execute(null);

        Assert.Equal(1, guard.RegisterCalls);
        Assert.False(vm.IsGameBarPopupHandlerRegistered);
        Assert.True(vm.CanRegisterGameBarPopupHandler);
        Assert.True(vm.HasGameBarPopupError);
        Assert.Contains("denied", vm.GameBarPopupError);
        Assert.Contains("ms-gamebar", vm.GameBarPopupError);
    }

    [Fact]
    public void GameBar_Popup_Handler_Restore_Failure_Is_Reported_Without_Changing_State()
    {
        var guard = new FakeGameBarPopupGuard(handlerOwned: true, handlerRegistered: true)
        {
            RestoreFailure = new IOException("locked"),
        };
        using var host = CreateHost(out var store);
        var vm = new MainWindowViewModel(host, store, gameBarPopupGuard: guard);

        vm.RestoreGameBarPopupHandlerCommand.Execute(null);

        Assert.Equal(1, guard.RestoreCalls);
        Assert.True(vm.OwnsGameBarPopupHandler);
        Assert.True(vm.HasGameBarPopupError);
        Assert.Contains("locked", vm.GameBarPopupError);
        Assert.Contains("恢复", vm.GameBarPopupError);
    }

    [Fact]
    public void GameBar_Preference_And_Popup_Suppression_Are_Reported_As_Two_Layers()
    {
        var guard = new FakeGameBarPopupGuard(handlerOwned: true, handlerRegistered: true);
        using var host = CreateHost(out var store);
        var vm = new MainWindowViewModel(host, store, gameBarPopupGuard: guard);

        vm.DisableGameBarControllerLaunchCommand.Execute(null);

        Assert.True(vm.IsGameBarControllerLaunchDisabled);
        Assert.Contains("已关闭", vm.GameBarPreferenceStatus);
        Assert.True(vm.IsGameBarPopupHandlerRegistered);
        Assert.Contains("已注册", vm.GameBarHandlerStatus);
        Assert.Contains("已关闭", vm.GameBarPreferenceStatus);
        Assert.DoesNotContain("已注册", vm.GameBarPopupHint);
        Assert.DoesNotContain("已关闭", vm.GameBarPopupHint);
    }

    private static EngineHost CreateHost(out InMemoryProfileStore store)
    {
        store = new InMemoryProfileStore();
        return new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store);
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

        public bool IsSupported { get; set; } = true;

        public bool IsControllerLaunchDisabled => ReadFailure is null
            ? _isControllerLaunchDisabled
            : throw ReadFailure;

        public bool OwnsProtocolHandler => ReadFailure is null
            ? _ownsProtocolHandler
            : throw ReadFailure;

        public bool IsProtocolHandlerRegistered => ReadFailure is null
            ? _isProtocolHandlerRegistered
            : throw ReadFailure;

        public bool IsProtocolHandlerTakenOver => ReadFailure is null
            ? _isProtocolHandlerTakenOver
            : throw ReadFailure;

        public int DisableCalls { get; private set; }

        public int RegisterCalls { get; private set; }

        public int RestoreCalls { get; private set; }

        public int OpenSettingsCalls { get; private set; }

        public Exception? Failure { get; init; }

        public Exception? RegisterFailure { get; init; }

        public Exception? RestoreFailure { get; init; }

        public Exception? ReadFailure { get; init; }

        /// <summary>Leaves the registration owned but stale, like an executable that moved.</summary>
        public bool RegisterStale { get; init; }

        public void DisableControllerLaunch()
        {
            DisableCalls++;
            if (Failure is not null)
            {
                throw Failure;
            }

            _isControllerLaunchDisabled = true;
        }

        public void RegisterProtocolHandler()
        {
            RegisterCalls++;
            if (RegisterFailure is not null)
            {
                throw RegisterFailure;
            }

            _ownsProtocolHandler = true;
            _isProtocolHandlerRegistered = !RegisterStale;
        }

        public void RestoreProtocolHandler()
        {
            RestoreCalls++;
            if (RestoreFailure is not null)
            {
                throw RestoreFailure;
            }

            if (_isProtocolHandlerTakenOver)
            {
                throw new InvalidOperationException(
                    "ms-gamebar 处理程序已被其他程序接管（当前命令已不是本应用的），未做任何改动；"
                    + "请检查系统设置，或安装/修复 Xbox Game Bar。");
            }

            _ownsProtocolHandler = false;
            _isProtocolHandlerRegistered = false;
        }

        public void OpenSystemSettings() => OpenSettingsCalls++;
    }
}
