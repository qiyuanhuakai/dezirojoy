using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The transfer seam's shared message state: the scheme import/export flow reports through
/// the same <see cref="MainWindowViewModel.TransferMessage"/> the preset operations do, and a
/// failure has to clear on the next success. The scheme flow itself is covered by
/// <see cref="MainWindowViewModelSchemeBundleTests"/>.
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
    public void A_Failed_Scheme_Transfer_Clears_The_Error_On_The_Next_Success()
    {
        var (vm, _, _, _) = Connected();

        vm.ImportSchemeFrom(TempPath("gone.json"));
        Assert.True(vm.IsTransferError);

        vm.ExportSchemeTo(TempPath("ok.json"));

        Assert.False(vm.IsTransferError);
        Assert.Contains("方案已导出", vm.TransferMessage);
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
