using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

public class GamepadVisualizerViewModelTests
{
    [Fact]
    public void Update_Sets_Every_Button_Flag()
    {
        var vm = new GamepadVisualizerViewModel();

        vm.Update(GamepadState.Create(
            GamepadButtonMask.A | GamepadButtonMask.B | GamepadButtonMask.X | GamepadButtonMask.Y
            | GamepadButtonMask.LeftBumper | GamepadButtonMask.RightBumper
            | GamepadButtonMask.LeftTrigger | GamepadButtonMask.RightTrigger
            | GamepadButtonMask.Back | GamepadButtonMask.Start | GamepadButtonMask.Guide
            | GamepadButtonMask.LeftStick | GamepadButtonMask.RightStick
            | GamepadButtonMask.DPadUp | GamepadButtonMask.DPadDown | GamepadButtonMask.DPadLeft | GamepadButtonMask.DPadRight,
            0, 0, 0, 0));

        Assert.True(vm.IsAPressed);
        Assert.True(vm.IsBPressed);
        Assert.True(vm.IsXPressed);
        Assert.True(vm.IsYPressed);
        Assert.True(vm.IsLeftBumperPressed);
        Assert.True(vm.IsRightBumperPressed);
        Assert.True(vm.IsLeftTriggerPressed);
        Assert.True(vm.IsRightTriggerPressed);
        Assert.True(vm.IsBackPressed);
        Assert.True(vm.IsStartPressed);
        Assert.True(vm.IsGuidePressed);
        Assert.True(vm.IsLeftStickPressed);
        Assert.True(vm.IsRightStickPressed);
        Assert.True(vm.IsDPadUpPressed);
        Assert.True(vm.IsDPadDownPressed);
        Assert.True(vm.IsDPadLeftPressed);
        Assert.True(vm.IsDPadRightPressed);
    }

    [Fact]
    public void Neutral_Update_Clears_Everything()
    {
        var vm = new GamepadVisualizerViewModel();
        vm.Update(GamepadState.Create(GamepadButtonMask.A, 1, 1, 1, 1));

        vm.Update(GamepadState.Neutral);

        Assert.False(vm.IsAPressed);
        Assert.Equal(0, vm.LeftStickX);
        Assert.Equal(0, vm.LeftMagnitude);
    }

    [Fact]
    public void Update_Reports_Stick_Positions_And_Magnitudes()
    {
        var vm = new GamepadVisualizerViewModel();

        vm.Update(GamepadState.Create(GamepadButtonMask.None, 0.6f, 0.8f, -0.6f, 0.8f));

        Assert.Equal(0.6, vm.LeftStickX, 6);
        Assert.Equal(0.8, vm.LeftStickY, 6);
        Assert.Equal(1.0, vm.LeftMagnitude, 3);
        Assert.Equal(1.0, vm.RightMagnitude, 3);
    }

    [Fact]
    public void Disconnect_Clears_The_State()
    {
        var vm = new GamepadVisualizerViewModel();
        vm.Update(GamepadState.Create(GamepadButtonMask.A, 1, 0, 0, 0));

        vm.SetConnected(false);

        Assert.False(vm.IsConnected);
        Assert.False(vm.IsAPressed);
    }

    [Fact]
    public void ButtonCount_Text_Reports_The_Count_The_Device_Declared()
    {
        var vm = new GamepadVisualizerViewModel();

        vm.SetDevice(new GamepadDescriptor("pad-1", "Controller"));

        Assert.Null(vm.ButtonCount);
        Assert.Equal("按键数未知", vm.ButtonCountText);

        vm.SetDevice(new GamepadDescriptor("pad-1", "Controller") { ButtonCount = 17 });

        Assert.Equal(17, vm.ButtonCount);
        Assert.Equal("检测到 17 个按键", vm.ButtonCountText);
    }

    [Fact]
    public void Disconnect_Drops_The_Stale_Button_Count()
    {
        var vm = new GamepadVisualizerViewModel();
        vm.SetDevice(new GamepadDescriptor("pad-1", "Controller") { ButtonCount = 17 });

        vm.SetConnected(false);

        Assert.Null(vm.ButtonCount);
        Assert.Equal("按键数未知", vm.ButtonCountText);
    }
}
