using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Platform.Sdl3;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

public sealed class DInputSettingsTests
{
    [Fact]
    public void Scan_Lists_Raw_Hid_Interfaces_And_Selects_The_Gamepad_Shaped_One()
    {
        var vm = ViewModel(() => Scan(Device(2, 8, 0, 0), Device(1, 19, 6, 1)));

        vm.RefreshDInputDevicesCommand.Execute(null);

        Assert.Equal(2, vm.UnmappedDInputDevices.Count);
        Assert.Equal(1u, vm.SelectedDInputDevice!.Device.InstanceId);
        Assert.True(vm.HasSelectedDInputDevice);
        Assert.Contains("2 个未映射 HID 接口", vm.DInputStatusMessage);
    }

    [Fact]
    public async Task Only_A_Saved_Mapping_Triggers_The_Existing_Safe_Restart()
    {
        var restarts = 0;
        var scans = 0;
        var saved = false;
        var vm = ViewModel(
            () => ++scans == 1 ? Scan(Device(1, 19, 6, 1)) : Scan(),
            () =>
            {
                restarts++;
                return Task.FromResult(true);
            });
        vm.DInputMappingRequested += _ => Task.FromResult(saved);
        vm.RefreshDInputDevicesCommand.Execute(null);

        await vm.MapDInputCommand.ExecuteAsync(null);
        Assert.Equal(0, restarts);
        Assert.Contains("未保存", vm.DInputStatusMessage);

        saved = true;
        await vm.MapDInputCommand.ExecuteAsync(null);
        Assert.Equal(1, restarts);
        Assert.Empty(vm.UnmappedDInputDevices);
    }

    private static DaemonSettingsViewModel ViewModel(
        Func<Sdl3JoystickScan> scan,
        Func<Task<bool>>? restart = null)
        => new(
            new FakeDaemonService(),
            backendStore: new InMemoryInputBackendStore(),
            restartDaemon: restart,
            scanJoysticks: scan);

    private static Sdl3JoystickScan Scan(params Sdl3JoystickInfo[] devices) => new()
    {
        Runtime = null,
        Initialized = true,
        InitError = null,
        Joysticks = devices,
        Platform = "Windows",
    };

    private static Sdl3JoystickInfo Device(uint id, int buttons, int axes, int hats) => new()
    {
        InstanceId = id,
        Name = "0x3537/0x1041",
        GuidString = "03000000373500004110000000000000",
        Vendor = 0x3537,
        Product = 0x1041,
        ProductVersion = 0,
        Serial = string.Empty,
        Path = $"hid-{id}",
        ButtonCount = buttons,
        AxisCount = axes,
        HatCount = hats,
        TypeName = "Unknown",
        HasStandardMapping = false,
    };
}
