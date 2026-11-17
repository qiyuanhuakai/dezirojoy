using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The rendering half of the settings card. The selection is persisted through the shared
/// settings service and only a restart of the GUI applies it - the renderer is built once per
/// process, and the daemon, which draws nothing, is deliberately not bounced for it. The
/// section is offered on Windows alone, because that is the only platform the software mode
/// exists on, and the card claims nothing about what the mode does better.
/// </summary>
public sealed class DaemonSettingsViewModelRenderModeTests : IDisposable
{
    private readonly string _directory;

    public DaemonSettingsViewModelRenderModeTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-render-tests", Guid.NewGuid().ToString("N"));
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

    private static ThemeService Settings(string directory) => new(directory);

    [Fact]
    public void The_Persisted_Selection_Is_What_The_Card_Opens_With()
    {
        var settings = Settings(_directory);
        settings.SetRenderMode(AppRenderMode.Software);

        var vm = Create(settings);

        Assert.Equal(AppRenderMode.Software, vm.SelectedRenderMode);
        Assert.True(vm.IsSoftwareRenderSelected);
        Assert.False(vm.IsDefaultRenderSelected);
    }

    [Fact]
    public void The_Default_Selection_Is_The_Platform_Detection()
    {
        var vm = Create(Settings(_directory));

        Assert.Equal(AppRenderMode.Default, vm.SelectedRenderMode);
        Assert.True(vm.IsDefaultRenderSelected);
        Assert.False(vm.IsSoftwareRenderSelected);
    }

    [Fact]
    public void Choosing_A_Radio_Moves_The_Selection()
    {
        var vm = Create(Settings(_directory));

        vm.IsSoftwareRenderSelected = true;

        Assert.Equal(AppRenderMode.Software, vm.SelectedRenderMode);
        Assert.True(vm.IsSoftwareRenderSelected);
        Assert.False(vm.IsDefaultRenderSelected);

        vm.IsDefaultRenderSelected = true;

        Assert.Equal(AppRenderMode.Default, vm.SelectedRenderMode);
        Assert.True(vm.IsDefaultRenderSelected);
        Assert.False(vm.IsSoftwareRenderSelected);
    }

    [Fact]
    public void Applying_Persists_The_Selection_And_Names_The_Restart_That_Applies_It()
    {
        var vm = Create(Settings(_directory));

        vm.SelectedRenderMode = AppRenderMode.Software;
        vm.ApplyRenderModeCommand.Execute(null);

        Assert.Equal(AppRenderMode.Software, Settings(_directory).RenderMode);
        Assert.Contains("重启主程序", vm.RenderModeStatusMessage);
        Assert.True(vm.HasRenderModeStatusMessage);

        // The card never claims the running window already changed how it draws.
        Assert.DoesNotContain("已切换", vm.RenderModeStatusMessage);
    }

    [Fact]
    public void The_Section_Is_Offered_Only_Where_The_Software_Mode_Exists()
    {
        Assert.True(Create(Settings(_directory), isWindows: () => true).IsRenderModeVisible);
        Assert.False(Create(Settings(_directory), isWindows: () => false).IsRenderModeVisible);
    }

    private static DaemonSettingsViewModel Create(ThemeService settings, Func<bool>? isWindows = null)
        => new(new FakeDaemonService(), settings: settings, isWindows: isWindows);
}
