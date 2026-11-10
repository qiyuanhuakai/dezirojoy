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

    /// <summary>
    /// The preview shares the mapping path's radial deadzone: resting drift and low-amplitude
    /// noise park position AND magnitude at rest, so the caps cannot twitch around the centre
    /// while the stick is untouched.
    /// </summary>
    [Fact]
    public void Drift_Below_The_Preview_Deadzone_Parks_Position_And_Magnitude()
    {
        var vm = new GamepadVisualizerViewModel();

        foreach (var (lx, ly, rx, ry) in new[]
        {
            (0.05f, 0.05f, -0.04f, 0.10f),
            (0.1f, 0f, 0f, -0.12f),
            (-0.14f, 0.02f, 0.03f, 0.13f),
        })
        {
            vm.Update(GamepadState.Create(GamepadButtonMask.None, lx, ly, rx, ry));
        }

        Assert.Equal(0, vm.LeftStickX);
        Assert.Equal(0, vm.LeftStickY);
        Assert.Equal(0, vm.RightStickX);
        Assert.Equal(0, vm.RightStickY);
        Assert.Equal(0, vm.LeftMagnitude);
        Assert.Equal(0, vm.RightMagnitude);
    }

    /// <summary>
    /// The deadzone gates the preview, it does not damp it: full deflection passes through
    /// unchanged and a push just past the threshold still moves the cap in real time, ramping
    /// from zero rather than snapping.
    /// </summary>
    [Fact]
    public void A_Clear_Push_Past_The_Deadzone_Still_Moves_The_Preview_In_Real_Time()
    {
        var vm = new GamepadVisualizerViewModel();

        vm.Update(GamepadState.Create(GamepadButtonMask.None, 0.6f, 0.8f, 0f, -1f));

        // Unit-magnitude deflection clears the deadzone untouched: the cap tracks the raw axes.
        Assert.Equal(0.6, vm.LeftStickX, 6);
        Assert.Equal(0.8, vm.LeftStickY, 6);
        Assert.Equal(1.0, vm.LeftMagnitude, 6);
        Assert.Equal(1.0, vm.RightMagnitude, 6);

        vm.Update(GamepadState.Create(GamepadButtonMask.None, 0.2f, 0f, 0f, 0f));

        Assert.True(vm.LeftStickX > 0, "a push past the deadzone must still move the cap");
        Assert.True(vm.LeftStickX < 0.2, "the ramped deflection must stay below the raw push");
        Assert.True(vm.LeftMagnitude > 0, "the magnitude must rise with the push");
        Assert.True(vm.LeftMagnitude < 0.2, "the ramped magnitude must stay below the raw push");
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
