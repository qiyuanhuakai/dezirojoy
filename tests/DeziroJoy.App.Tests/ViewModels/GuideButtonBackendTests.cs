using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// Guide-button availability follows the running backend, not just the OS. SDL3 reports the
/// button on every platform, so a daemon snapshot naming SDL3 makes Guide bindable on
/// Windows; the platform default keeps XInput's missing bit, and an evdev Linux is
/// unchanged. The construction-time state stays the platform answer until the daemon says.
/// </summary>
public sealed class GuideButtonBackendTests
{
    [Fact]
    public void The_Factory_Answer_Is_The_Platform_One()
    {
        // Unchanged platform answer: XInput has no Guide bit, evdev can report BTN_MODE.
        // The running backend is layered on top of it by the window, not by the factory.
        Assert.Equal(!OperatingSystem.IsWindows(), PlatformFactory.GuideButtonSupported);
    }

    [Fact]
    public void The_Construction_State_Is_The_Platform_Answer()
    {
        var store = new InMemoryProfileStore();

        var injected = new MainWindowViewModel(
            new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store),
            store,
            guideButtonSupported: false);

        Assert.False(injected.GuideButtonSupported);
        Assert.True(injected.ShowGuidePlatformNote);
        Assert.False(injected.Editor.GuideButtonSupported);

        var platformDefault = new MainWindowViewModel(
            new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store),
            store);

        Assert.Equal(PlatformFactory.GuideButtonSupported, platformDefault.GuideButtonSupported);
    }

    [Fact]
    public async Task A_Daemon_Running_Sdl3_Makes_Guide_Available_On_The_XInput_Platform()
    {
        var context = await MainWindowViewModelDaemonTests.CreateDaemonViewModel(
            host => host.ActiveInputBackend = InputBackend.Sdl3,
            guideButtonSupported: false);
        await using var viewModel = context.ViewModel;

        // The window opened on the platform answer (XInput has no Guide bit); the snapshot
        // naming SDL3 is the only surface that knows the daemon reads the button anyway.
        Assert.True(viewModel.GuideButtonSupported);
        Assert.False(viewModel.ShowGuidePlatformNote);
        Assert.True(viewModel.Editor.GuideButtonSupported);

        viewModel.Editor.SelectedButton = DeziroJoy.Core.Input.GamepadButton.Guide;
        Assert.False(viewModel.Editor.ShowGuideUnavailableHint);
    }

    [Fact]
    public async Task A_Daemon_Back_On_The_Platform_Backend_Hides_Guide_Again()
    {
        var context = await MainWindowViewModelDaemonTests.CreateDaemonViewModel(
            host => host.ActiveInputBackend = InputBackend.Sdl3,
            guideButtonSupported: false);
        await using var viewModel = context.ViewModel;
        Assert.True(viewModel.GuideButtonSupported);

        // The daemon restarts onto the platform backend: the next snapshot says so and the
        // hint returns, so the window never keeps promising a button the engine cannot read.
        context.DaemonHost.ActiveInputBackend = InputBackend.Platform;
        context.DaemonHost.AddDevice("pad-1");
        await WaitForSnapshot(viewModel);

        Assert.False(viewModel.GuideButtonSupported);
        Assert.True(viewModel.ShowGuidePlatformNote);
        Assert.False(viewModel.Editor.GuideButtonSupported);
    }

    private static async Task WaitForSnapshot(MainWindowViewModel viewModel)
    {
        var current = viewModel.DaemonSnapshot!.Sequence;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (viewModel.DaemonSnapshot!.Sequence == current && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
