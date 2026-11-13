using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The import/export seam: the view model owns the commands and the file I/O, and only asks the
/// view for a path. These tests stand in for the view by answering the request directly.
/// </summary>
public class MainWindowViewModelTransferTests : IDisposable
{
    private readonly string _directory;

    public MainWindowViewModelTransferTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-vm-transfer-tests", Guid.NewGuid().ToString("N"));
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

    private static (MainWindowViewModel Vm, EngineHost Host, FakeGamepadProvider Provider, InMemoryProfileStore Store) CreateHost()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var vm = new MainWindowViewModel(host, store);

        return (vm, host, provider, store);
    }

    private static (MainWindowViewModel Vm, EngineHost Host, FakeGamepadProvider Provider, InMemoryProfileStore Store) Connected()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        return (vm, host, provider, store);
    }

    private string TempPath(string name) => Path.Combine(_directory, name);

    [Fact]
    public void Export_Command_Asks_The_View_For_A_Path()
    {
        var (vm, _, _, _) = Connected();
        var requested = 0;
        vm.ExportPathRequested += (_, _) => requested++;

        vm.ExportProfileCommand.Execute(null);

        Assert.Equal(1, requested);
    }

    [Fact]
    public void Import_Command_Asks_The_View_For_A_Path()
    {
        var (vm, _, _, _) = Connected();
        var requested = 0;
        vm.ImportPathRequested += (_, _) => requested++;

        vm.ImportProfileCommand.Execute(null);

        Assert.Equal(1, requested);
    }

    [Fact]
    public void ExportTo_Writes_The_Active_Profile_To_The_Chosen_File()
    {
        var (vm, _, _, _) = Connected();
        var path = TempPath("exported.json");

        vm.ExportTo(path);

        Assert.True(File.Exists(path), "the chosen file must be written");
        Assert.Contains("已导出", vm.TransferMessage);
        Assert.False(vm.IsTransferError);
        Assert.True(vm.HasTransferMessage);

        var reloaded = ProfileSerializer.Deserialize(File.ReadAllText(path));
        Assert.Equal(vm.ActiveProfile!.Name, reloaded.Name);
        Assert.Equal(vm.ActiveProfile.Mappings.Count, reloaded.Mappings.Count);
    }

    [Fact]
    public void ExportTo_With_A_Cancelled_Pick_Does_Nothing()
    {
        var (vm, _, _, _) = Connected();

        vm.ExportTo(null);

        Assert.False(vm.HasTransferMessage);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ExportTo_Without_A_Profile_Reports_An_Error()
    {
        var (vm, _, _, _) = CreateHost();

        vm.ExportTo(TempPath("nothing.json"));

        Assert.True(vm.IsTransferError);
        Assert.Contains("导出失败", vm.TransferMessage);
        Assert.False(File.Exists(TempPath("nothing.json")));
    }

    [Fact]
    public void ImportFrom_Replaces_The_Active_Profile_And_Rebinds_It_To_The_Device()
    {
        var (vm, _, _, store) = Connected();
        var source = TempPath("incoming.json");
        File.WriteAllText(source, ProfileSerializer.Serialize(new GamepadProfile
        {
            Name = "导入的配置",
            DeviceId = "some-other-pad",
            Layout = GamepadLayout.DualShock,
            Mappings = [new MappingEntry { Source = new ButtonBinding(GamepadButton.X), Target = new KeyPressTarget(GamepadKey.Space) }],
        }));

        vm.ImportFrom(source);

        Assert.False(vm.IsTransferError, vm.TransferMessage);
        Assert.Contains("导入的配置", vm.TransferMessage);
        Assert.Equal("导入的配置", vm.ActiveProfile!.Name);
        Assert.Equal("xinput:0", vm.ActiveProfile.DeviceId);
        Assert.Single(vm.Mappings);
        Assert.Contains(vm.Mappings, row => row.TargetText.Contains("Space"));

        // The imported profile is the device's profile from now on, including after a reconnect.
        Assert.Equal("导入的配置", store.Load("xinput:0")!.Name);
    }

    [Fact]
    public void ImportFrom_Of_A_Malformed_File_Reports_An_Error_And_Keeps_The_Current_Profile()
    {
        var (vm, _, _, _) = Connected();
        var before = vm.ActiveProfile;
        var source = TempPath("broken.json");
        File.WriteAllText(source, "{ definitely not a profile");

        vm.ImportFrom(source);

        Assert.True(vm.IsTransferError);
        Assert.Contains("导入失败", vm.TransferMessage);
        Assert.Same(before, vm.ActiveProfile);
        Assert.Equal(10, vm.Mappings.Count);
    }

    [Fact]
    public void ImportFrom_Of_A_Missing_File_Reports_An_Error_And_Keeps_The_Current_Profile()
    {
        var (vm, _, _, _) = Connected();
        var before = vm.ActiveProfile;

        vm.ImportFrom(TempPath("gone.json"));

        Assert.True(vm.IsTransferError);
        Assert.Same(before, vm.ActiveProfile);
    }

    [Fact]
    public void ImportFrom_Without_A_Controller_Reports_An_Error()
    {
        var (vm, _, _, _) = CreateHost();
        var source = TempPath("portable.json");
        File.WriteAllText(source, ProfileSerializer.Serialize(new GamepadProfile { Name = "便携配置", Mappings = [] }));

        vm.ImportFrom(source);

        Assert.True(vm.IsTransferError);
        Assert.Contains("导入失败", vm.TransferMessage);
        Assert.Null(vm.ActiveProfile);
    }

    [Fact]
    public void ImportFrom_With_A_Cancelled_Pick_Does_Nothing()
    {
        var (vm, _, _, _) = Connected();
        var before = vm.ActiveProfile;

        vm.ImportFrom(null);

        Assert.False(vm.HasTransferMessage);
        Assert.Same(before, vm.ActiveProfile);
    }

    [Fact]
    public void A_Failed_Transfer_Clears_The_Error_On_The_Next_Success()
    {
        var (vm, _, _, _) = Connected();

        vm.ImportFrom(TempPath("gone.json"));
        Assert.True(vm.IsTransferError);

        vm.ExportTo(TempPath("ok.json"));

        Assert.False(vm.IsTransferError);
        Assert.Contains("已导出", vm.TransferMessage);
    }

    [Fact]
    public void ApplySample_Feeds_The_Real_Input_Calibration_Of_Both_Sensitivity_Panels()
    {
        var (vm, host, provider, _) = Connected();
        provider.SetState(
            "xinput:0",
            GamepadState.Create(default, leftStickX: 0f, leftStickY: -0.5f, rightStickX: 0.25f, rightStickY: 0f));
        vm.MouseSensitivity.IsRealInputMode = true;
        vm.ScrollSensitivity.IsRealInputMode = true;

        SampleHelper.Sample(vm, host);

        // The panels read the same samples the visualizer does: the mouse panel the right
        // stick, the scroll panel the left.
        Assert.Equal(0.25, vm.MouseSensitivity.RawStickX);
        Assert.Equal(-0.5, vm.ScrollSensitivity.RawStickY);
    }

    [Fact]
    public void ApplyPresenceSample_Feeds_The_Calibration_While_The_Engine_Is_Stopped()
    {
        var (vm, _, _, _) = Connected();
        vm.MouseSensitivity.IsRealInputMode = true;

        // The presence poll carries its own state, so the calibration is fed the same way
        // the running engine's samples are.
        var state = GamepadState.Create(default, leftStickX: 0f, leftStickY: 0f, rightStickX: 0.75f, rightStickY: 0f);
        vm.ApplyPresenceSample(new ControllerSampledEventArgs(vm.ActiveController!, state));

        Assert.Equal(0.75, vm.MouseSensitivity.RawStickX);
    }
}
