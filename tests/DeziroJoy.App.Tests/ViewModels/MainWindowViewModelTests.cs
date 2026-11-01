using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

public class MainWindowViewModelTests
{
    private static (MainWindowViewModel Vm, EngineHost Host, FakeGamepadProvider Provider, InMemoryProfileStore Store) CreateHost()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var vm = new MainWindowViewModel(host, store);

        return (vm, host, provider, store);
    }

    [Fact]
    public void First_Sample_Shows_The_Controller_And_Its_Default_Profile()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        SampleHelper.Sample(vm, host);

        Assert.True(vm.HasController);
        Assert.NotNull(vm.ActiveController);
        Assert.Equal(GamepadLayout.Xbox, vm.ActiveController!.Descriptor.Layout);
        Assert.Equal(10, vm.Mappings.Count);
        Assert.Contains("Xbox", vm.StatusMessage);
    }

    [Fact]
    public void Add_Mapping_Appends_To_The_Profile_And_Applies_It()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.LeftBumper;
        vm.Editor.TargetKind = MappingTargetKind.KeyPress;
        vm.Editor.SelectedKey = GamepadKey.Space;
        vm.CommitEditorCommand.Execute(null);

        Assert.Equal(11, vm.Mappings.Count);
        Assert.Equal(11, vm.ActiveProfile!.Mappings.Count);
        Assert.Contains(vm.Mappings, row => row.TargetText.Contains("Space"));

        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.LeftBumper, 0, 0, 0, 0));
        SampleHelper.Sample(vm, host);

        Assert.True(vm.HasController);
    }

    [Fact]
    public void Edit_Mapping_Replaces_The_Selected_Row()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        var bRow = vm.Mappings.Single(row => row.SourceText.Contains('B'));
        vm.EditMappingCommand.Execute(bRow);
        vm.Editor.TargetKind = MappingTargetKind.KeyPress;
        vm.Editor.SelectedKey = GamepadKey.Space;
        vm.CommitEditorCommand.Execute(null);

        Assert.Equal(10, vm.Mappings.Count);
        Assert.DoesNotContain(vm.Mappings, row => row.TargetText.Contains("Enter"));
        Assert.Contains(vm.Mappings, row => row.TargetText.Contains("Space"));
    }

    [Fact]
    public void Delete_Mapping_Removes_The_Row()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        var row = vm.Mappings.Single(r => r.SourceText.Contains('A'));
        vm.DeleteMappingCommand.Execute(row);

        Assert.Equal(9, vm.Mappings.Count);
        Assert.DoesNotContain(vm.Mappings, r => r.SourceText.Contains('A'));
    }

    [Fact]
    public void Toggle_Mapping_Delegates_To_The_Host()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        vm.ToggleMappingCommand.Execute(null);

        Assert.False(host.IsMappingEnabled);
        Assert.False(vm.IsMappingEnabled);
    }

    [Fact]
    public void Save_Profile_Persists_To_The_Store()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        vm.SaveProfileCommand.Execute(null);

        Assert.NotNull(store.Load("xinput:0"));
    }

    [Fact]
    public void Reset_To_Default_Restores_The_Built_In_Profile()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        var row = vm.Mappings.Single(r => r.SourceText.Contains('B'));
        vm.DeleteMappingCommand.Execute(row);
        Assert.Equal(9, vm.Mappings.Count);

        vm.ResetToDefaultCommand.Execute(null);

        Assert.Equal(10, vm.Mappings.Count);
    }

    [Fact]
    public void Sample_Updates_The_Visualizer()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.A | GamepadButtonMask.DPadUp, 0.5f, -0.5f, 0, 0));

        SampleHelper.Sample(vm, host);

        Assert.True(vm.Visualizer.IsAPressed);
        Assert.True(vm.Visualizer.IsDPadUpPressed);
        Assert.Equal(0.5, vm.Visualizer.LeftStickX);
        Assert.Equal(-0.5, vm.Visualizer.LeftStickY);
    }

    [Fact]
    public void Sensitivity_Panel_Edits_The_Active_Mouse_Mapping()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        vm.MouseSensitivity.MaxSpeed = 3000f;
        vm.ApplyMouseSensitivityCommand.Execute(null);

        var target = Assert.IsType<MouseMoveTarget>(
            vm.ActiveProfile!.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Right }).Target);

        Assert.Equal(3000f, target.Settings.MaxSpeed);
    }

    [Fact]
    public void Applying_Sensitivity_Keeps_A_Custom_Precision_Button()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        // The panel loads the mapping's settings, the user changes only the speed, and applies.
        // The precision key is not touched, so it must survive the round-trip untouched.
        vm.MouseSensitivity.LoadFrom(new MouseStickSettings { PrecisionButton = GamepadButton.RightBumper });
        vm.MouseSensitivity.MaxSpeed = 3000f;
        vm.ApplyMouseSensitivityCommand.Execute(null);

        var target = Assert.IsType<MouseMoveTarget>(
            vm.ActiveProfile!.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Right }).Target);

        Assert.Equal(3000f, target.Settings.MaxSpeed);
        Assert.Equal(GamepadButton.RightBumper, target.Settings.PrecisionButton);
    }

    [Fact]
    public void Toggle_Engine_Starts_And_Stops_The_Host_Loop()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        vm.ToggleEngineCommand.Execute(null);
        Assert.True(host.IsRunning);
        Assert.True(vm.IsEngineRunning);

        vm.ToggleEngineCommand.Execute(null);
        Assert.False(host.IsRunning);
        Assert.False(vm.IsEngineRunning);
    }

    [Fact]
    public void Losing_The_Controller_Clears_The_Active_Profile()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        provider.Remove("xinput:0");
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(TimeSpan.FromSeconds(1.0 / 64.0));
        vm.SyncControllers();

        Assert.False(vm.HasController);
        Assert.Null(vm.ActiveProfile);
    }

    [Fact]
    public void Sync_Reports_A_Connected_Gamepad_While_The_Engine_Is_Stopped()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        vm.SyncControllers();

        Assert.True(vm.HasController);
        Assert.NotNull(vm.ActiveController);
        Assert.True(vm.Visualizer.IsConnected);
    }

    [Fact]
    public void Sync_Reports_A_Disconnect_While_The_Engine_Is_Stopped()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        // Establish the connected state through the engine tick path first.
        SampleHelper.Sample(vm, host);
        Assert.True(vm.HasController);

        provider.Remove("xinput:0");
        vm.SyncControllers();

        Assert.False(vm.HasController);
        Assert.Null(vm.ActiveController);
        Assert.False(vm.Visualizer.IsConnected);
    }

    [Fact]
    public void Stopped_With_No_Gamepad_Reports_Disconnected()
    {
        var (vm, host, provider, _) = CreateHost();

        vm.SyncControllers();

        Assert.False(vm.IsEngineRunning);
        Assert.False(vm.HasController);
        Assert.False(vm.Visualizer.IsConnected);
        Assert.Equal("未检测到手柄，请连接手柄", vm.StatusMessage);
    }

    [Fact]
    public void Stopped_With_Gamepad_Reports_Connected_With_Mapping_Off()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        vm.SyncControllers();

        Assert.False(vm.IsEngineRunning);
        Assert.True(vm.HasController);
        Assert.True(vm.Visualizer.IsConnected);
        Assert.Contains("Xbox", vm.StatusMessage);
        Assert.Contains("映射未启动", vm.StatusMessage);
    }

    [Fact]
    public void Running_With_Gamepad_Reports_Live_Connected_State()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));

        SampleHelper.StartEngineManually(vm, host);
        SampleHelper.Sample(vm, host);

        Assert.True(vm.IsEngineRunning);
        Assert.True(vm.HasController);
        Assert.True(vm.Visualizer.IsConnected);
        Assert.True(vm.Visualizer.IsAPressed);
        Assert.Contains("Xbox", vm.StatusMessage);
        Assert.DoesNotContain("映射未启动", vm.StatusMessage);

        vm.ToggleEngineCommand.Execute(null);
    }

    [Fact]
    public void Running_With_Gamepad_Lost_Reports_Disconnected()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        SampleHelper.StartEngineManually(vm, host);
        SampleHelper.Sample(vm, host);
        Assert.True(vm.HasController);

        provider.Remove("xinput:0");
        host.TickOnce(TimeSpan.FromSeconds(1.0 / 64.0));
        vm.SyncControllers();

        Assert.True(vm.IsEngineRunning);
        Assert.False(vm.HasController);
        Assert.False(vm.Visualizer.IsConnected);
        Assert.Equal("未检测到手柄，请连接手柄", vm.StatusMessage);

        vm.ToggleEngineCommand.Execute(null);
    }

    [Fact]
    public void Starting_The_Engine_Keeps_The_Already_Detected_Gamepad()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        vm.SyncControllers();
        var detected = vm.ActiveController;
        Assert.True(vm.HasController);

        vm.ToggleEngineCommand.Execute(null);
        vm.SyncControllers();

        Assert.Same(detected, vm.ActiveController);
        Assert.Single(vm.Controllers);
        Assert.True(vm.Visualizer.IsConnected);
        Assert.DoesNotContain("映射未启动", vm.StatusMessage);

        vm.ToggleEngineCommand.Execute(null);
    }

    [Fact]
    public void Reset_To_Default_Reloads_The_Sensitivity_Panels_From_The_New_Profile()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        vm.MouseSensitivity.MaxSpeed = 5000f;
        vm.ApplyMouseSensitivityCommand.Execute(null);
        Assert.Equal(5000f, vm.MouseSensitivity.MaxSpeed);

        vm.ResetToDefaultCommand.Execute(null);

        var mouseTarget = Assert.IsType<MouseMoveTarget>(
            vm.ActiveProfile!.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Right }).Target);
        Assert.Equal(mouseTarget.Settings.MaxSpeed, vm.MouseSensitivity.MaxSpeed);
        Assert.NotEqual(5000f, vm.MouseSensitivity.MaxSpeed);
    }

    [Fact]
    public void Import_From_Reloads_The_Sensitivity_Panels_From_The_Imported_Profile()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        var path = Path.Combine(Path.GetTempPath(), $"dezirojoy-import-{Guid.NewGuid():N}.json");
        var base_ = vm.ActiveProfile!;
        var imported = base_ with
        {
            Mappings = base_.Mappings
                .Select(m => m is { Source: StickBinding { Stick: GamepadStick.Right }, Target: MouseMoveTarget mouse }
                    ? m with { Target = new MouseMoveTarget(mouse.Settings with { MaxSpeed = 1234f }) }
                    : m)
                .ToList(),
        };
        File.WriteAllText(path, ProfileSerializer.Serialize(imported));

        try
        {
            vm.ImportFrom(path);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal(1234f, vm.MouseSensitivity.MaxSpeed);
    }
}
