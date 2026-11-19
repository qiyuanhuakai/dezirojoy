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

        Assert.Equal(2, vm.DInputDevices.Count);
        Assert.Equal(1u, vm.SelectedDInputDevice!.Device.InstanceId);
        Assert.True(vm.HasSelectedDInputDevice);
        Assert.Contains("2 个 HID 接口", vm.DInputStatusMessage);
    }

    [Fact]
    public async Task An_Existing_Standard_Mapping_Can_Be_Learned_Again()
    {
        var mapped = Device(1, 19, 6, 1) with { HasStandardMapping = true };
        var vm = ViewModel(() => Scan(mapped));
        var opened = 0;
        vm.DInputMappingRequested += _ =>
        {
            opened++;
            return Task.FromResult(false);
        };
        vm.RefreshDInputDevicesCommand.Execute(null);

        Assert.Contains("已有映射，可重学", vm.SelectedDInputDevice!.DisplayName);
        await vm.MapDInputCommand.ExecuteAsync(null);

        Assert.Equal(1, opened);
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
        Assert.Empty(vm.DInputDevices);
    }

    [Fact]
    public async Task Opening_Settings_Through_The_Binding_Scans_Without_A_Command()
    {
        var store = new InMemoryProfileStore();
        using var host = new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store);
        await using var viewModel = new MainWindowViewModel(
            host,
            store,
            scanJoysticks: () => Scan(Device(1, 19, 6, 1)));

        viewModel.IsSettingsOpen = true;

        Assert.Equal(1u, viewModel.DaemonSettings.SelectedDInputDevice!.Device.InstanceId);
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
