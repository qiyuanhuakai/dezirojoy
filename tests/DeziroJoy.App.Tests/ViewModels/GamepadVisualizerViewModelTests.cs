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
    /// noise never arm a stick, so position AND magnitude stay parked at rest and the caps cannot
    /// twitch around the centre while the stick is untouched.
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
    /// The deadzone gates the preview, it does not damp it: a push past the threshold arms the
    /// stick and lands the cap on the ramped deflection on the very sample it arrives - ramping
    /// from the enter threshold rather than snapping from the raw axes, and staying below the raw
    /// push.
    /// </summary>
    [Fact]
    public void A_Clear_Push_Past_The_Deadzone_Still_Moves_The_Preview_In_Real_Time()
    {
        var vm = new GamepadVisualizerViewModel();

        vm.Update(GamepadState.Create(GamepadButtonMask.None, 0.6f, 0.8f, 0f, -1f));

        // Unit-magnitude deflection clears the deadzone untouched: the cap jumps to the ramped axes.
        Assert.Equal(0.6, vm.LeftStickX, 6);
        Assert.Equal(0.8, vm.LeftStickY, 6);
        Assert.Equal(1.0, vm.LeftMagnitude, 6);
        Assert.Equal(1.0, vm.RightMagnitude, 6);

        // A smaller push from rest still arms and lands below the raw push on the first sample.
        var small = new GamepadVisualizerViewModel();
        small.Update(GamepadState.Create(GamepadButtonMask.None, 0.2f, 0f, 0f, 0f));

        Assert.True(small.LeftStickX > 0, "a push past the deadzone must still move the cap");
        Assert.True(small.LeftStickX < 0.2, "the ramped deflection must stay below the raw push");
        Assert.True(small.LeftMagnitude > 0, "the magnitude must rise with the push");
        Assert.True(small.LeftMagnitude < 0.2, "the ramped magnitude must stay below the raw push");
    }

    /// <summary>
    /// A long run of resting sensor noise - every reading under the enter deadzone - never arms a
    /// stick, so the cap holds exact rest and, because the frame never changes, the preview makes
    /// no canvas writes while it sits there.
    /// </summary>
    [Fact]
    public void Resting_Noise_Never_Moves_The_Cap_Or_Repaints_The_Preview()
    {
        var vm = new GamepadVisualizerViewModel();
        var frames = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is null or "") frames++; };

        foreach (var (lx, ly) in new[]
        {
            (0.02f, -0.03f),
            (-0.06f, 0.05f),
            (0.09f, 0.08f),
            (0.11f, -0.04f),
            (0.03f, 0.12f),
        })
        {
            for (var i = 0; i < 8; i++)
            {
                vm.Update(GamepadState.Create(GamepadButtonMask.None, lx, ly, -lx, -ly));
            }
        }

        Assert.Equal(0, vm.LeftStickX);
        Assert.Equal(0, vm.LeftStickY);
        Assert.Equal(0, vm.LeftMagnitude);
        Assert.Equal(0, vm.RightMagnitude);
        Assert.Equal(0, frames);
    }

    /// <summary>
    /// A magnitude walking back and forth across the enter line is the exact case a hard deadzone
    /// flickers on: it drops the cap to exact rest on every sub-threshold sample. The hysteresis
    /// keeps the stick armed through the dips (they all sit above the 0.10 exit) and the filter
    /// holds one small continuous offset, so the cap never parks at rest mid-noise and never pops
    /// to a real deflection.
    /// </summary>
    [Fact]
    public void Noise_Oscillating_Across_The_Deadzone_Line_Does_Not_Flicker_The_Cap()
    {
        var vm = new GamepadVisualizerViewModel();

        var magnitudes = new[] { 0.16f, 0.14f, 0.17f, 0.13f, 0.16f, 0.12f, 0.15f, 0.18f, 0.11f, 0.16f };
        var peak = 0.0;

        for (var i = 0; i < magnitudes.Length; i++)
        {
            vm.Update(GamepadState.Create(GamepadButtonMask.None, magnitudes[i], 0f, 0f, 0f));
            peak = Math.Max(peak, vm.LeftMagnitude);

            if (i > 0)
            {
                Assert.True(vm.LeftMagnitude > 0,
                    $"frame {i}: the smoothed cap must not park at exact rest mid-noise");
            }
        }

        Assert.True(peak < 0.05, $"the cap must read the boundary noise as a small signal, peak was {peak}");
    }

    /// <summary>
    /// A clear push answers on the sample it lands - the cap jumps to the ramped deflection rather
    /// than easing in - and releasing below the deadzone glides it home over a few samples instead
    /// of snapping, parking exactly at rest once the reading is centred.
    /// </summary>
    [Fact]
    public void A_Push_Arms_Immediately_And_The_Release_Glides_The_Cap_Home()
    {
        var vm = new GamepadVisualizerViewModel();

        vm.Update(GamepadState.Create(GamepadButtonMask.None, 0.5f, 0f, 0f, 0f));
        Assert.Equal(0.4117647, vm.LeftStickX, 6);
        Assert.Equal(0.4117647, vm.LeftMagnitude, 6);

        vm.Update(GamepadState.Create(GamepadButtonMask.None, 0.05f, 0f, 0f, 0f));
        var afterRelease = vm.LeftStickX;
        Assert.True(afterRelease > 0 && afterRelease < 0.4117647, "the release must start gliding home, not snap");

        vm.Update(GamepadState.Create(GamepadButtonMask.None, 0.05f, 0f, 0f, 0f));
        Assert.True(vm.LeftStickX < afterRelease, "the cap must keep closing on centre");

        vm.Update(GamepadState.Neutral);
        Assert.Equal(0, vm.LeftStickX);
        Assert.Equal(0, vm.LeftMagnitude);
    }

    /// <summary>
    /// The exit threshold sits below the enter threshold so a stick that dips back toward centre
    /// after a push stays armed: the cap eases toward centre through the dip and re-engages
    /// smoothly when the push returns, rather than disarming and snapping back to full deflection
    /// the way a single-threshold gate would.
    /// </summary>
    [Fact]
    public void A_Stick_That_Dips_Toward_Centre_Stays_Armed_Instead_Of_Dropping_And_Re_Snapping()
    {
        var vm = new GamepadVisualizerViewModel();

        vm.Update(GamepadState.Create(GamepadButtonMask.None, 0.5f, 0f, 0f, 0f));
        Assert.Equal(0.4117647, vm.LeftStickX, 6);

        vm.Update(GamepadState.Create(GamepadButtonMask.None, 0.12f, 0f, 0f, 0f));
        Assert.True(vm.LeftStickX < 0.4117647 && vm.LeftStickX > 0, "the cap eases toward centre on the dip");

        vm.Update(GamepadState.Create(GamepadButtonMask.None, 0.5f, 0f, 0f, 0f));
        Assert.True(vm.LeftStickX > 0 && vm.LeftStickX < 0.4117647,
            "re-engaging must ease from the dipped position, not snap back to full deflection");
    }

    /// <summary>
    /// The stick frame is republished as one unit: a sample that moves both sticks raises a single
    /// change notification, so the view redraws every layer from one consistent snapshot instead of
    /// once per changed axis. A frame that has not changed - a cap parked at rest or holding still -
    /// raises nothing, keeping the idle preview off the canvas.
    /// </summary>
    [Fact]
    public void A_Moved_Frame_Republishes_Once_And_A_Parked_Cap_Republishes_Never()
    {
        var vm = new GamepadVisualizerViewModel();
        var frames = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is null or "") frames++; };

        for (var i = 0; i < 10; i++)
        {
            vm.Update(GamepadState.Neutral);
        }
        Assert.Equal(0, frames);

        vm.Update(GamepadState.Create(GamepadButtonMask.None, 0.5f, 0.5f, -0.5f, 0.5f));
        Assert.Equal(1, frames);

        for (var i = 0; i < 5; i++)
        {
            vm.Update(GamepadState.Create(GamepadButtonMask.None, 0.5f, 0.5f, -0.5f, 0.5f));
        }
        Assert.Equal(1, frames);
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
