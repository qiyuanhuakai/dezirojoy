using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

public class MouseSensitivityViewModelTests
{
    private static void AdvanceMany(MouseSensitivityViewModel vm, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            vm.Advance(TimeSpan.FromSeconds(1.0 / 64.0));
        }
    }

    [Fact]
    public void BuildSettings_Reflects_Every_Property()
    {
        var vm = new MouseSensitivityViewModel
        {
            MaxSpeed = 2400f,
            DeadZone = 0.25f,
            Curve = ResponseCurve.Aggressive,
            CurveStrength = 1.5f,
            ResponseTime = 0.12f,
            PrecisionScale = 0.5f,
            InvertY = false,
        };

        var settings = vm.BuildSettings();

        Assert.Equal(2400f, settings.MaxSpeed);
        Assert.Equal(0.25f, settings.DeadZone);
        Assert.Equal(ResponseCurve.Aggressive, settings.Curve);
        Assert.Equal(1.5f, settings.CurveStrength);
        Assert.Equal(0.12f, settings.ResponseTime);
        Assert.Equal(0.5f, settings.PrecisionScale);
        Assert.False(settings.InvertY);
    }

    [Fact]
    public void BuildSettings_Carries_The_Precision_Button()
    {
        var vm = new MouseSensitivityViewModel { PrecisionButton = GamepadButton.RightBumper };

        Assert.Equal(GamepadButton.RightBumper, vm.BuildSettings().PrecisionButton);
    }

    [Fact]
    public void LoadFrom_Restores_The_Precision_Button()
    {
        var vm = new MouseSensitivityViewModel();

        vm.LoadFrom(new MouseStickSettings { PrecisionButton = GamepadButton.LeftTrigger });

        Assert.Equal(GamepadButton.LeftTrigger, vm.PrecisionButton);
    }

    [Fact]
    public void Reset_Restores_The_Default_Precision_Button()
    {
        var vm = new MouseSensitivityViewModel { PrecisionButton = GamepadButton.RightBumper };

        vm.Reset();

        Assert.Equal(GamepadButton.LeftStick, vm.PrecisionButton);
    }

    [Fact]
    public void The_Precision_Picker_Offers_Every_Button_That_Has_A_Mask_Bit()
    {
        var vm = new MouseSensitivityViewModel();

        // GamepadButton.Extra has no mask bit and ToMask() throws for it, so offering it would
        // build a settings object the engine cannot read the precision state from.
        Assert.DoesNotContain(GamepadButton.Extra, vm.PrecisionButtonOptions);
        Assert.Equal(
            Enum.GetValues<GamepadButton>().Where(button => button != GamepadButton.Extra).OrderBy(button => button),
            vm.PrecisionButtonOptions.OrderBy(button => button));
    }

    [Fact]
    public void LoadFrom_Restores_The_Reported_Precision_Button()
    {
        var vm = new MouseSensitivityViewModel();

        vm.LoadFrom(new MouseStickSettings { PrecisionButton = GamepadButton.LeftTrigger });

        Assert.Equal(GamepadButton.LeftTrigger, vm.PrecisionButton);
        Assert.Equal(MappingDescriptions.Button(GamepadButton.LeftTrigger), vm.PrecisionButtonLabel);
    }

    [Fact]
    public void Reset_Restores_The_Default_Reported_Precision_Button()
    {
        var vm = new MouseSensitivityViewModel { PrecisionButton = GamepadButton.RightBumper };

        vm.Reset();

        Assert.Equal(GamepadButton.LeftStick, vm.PrecisionButton);
        Assert.Equal(MappingDescriptions.Button(GamepadButton.LeftStick), vm.PrecisionButtonLabel);
    }

    [Fact]
    public void The_Reported_Precision_Button_Notifies_When_The_Loaded_Key_Changes()
    {
        var vm = new MouseSensitivityViewModel();
        var notified = new List<string>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName!);

        vm.LoadFrom(new MouseStickSettings { PrecisionButton = GamepadButton.LeftTrigger });

        Assert.Contains(nameof(MouseSensitivityViewModel.PrecisionButtonLabel), notified);
        Assert.Contains(nameof(MouseSensitivityViewModel.PrecisionButton), notified);
    }

    [Fact]
    public void Preview_Stays_Put_When_MaxSpeed_Is_Zero()
    {
        var vm = new MouseSensitivityViewModel { MaxSpeed = 0f, IsLive = true };

        AdvanceMany(vm, 30);

        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewX);
        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewY);
    }

    [Fact]
    public void Preview_Stays_Put_When_Deadzone_Covers_The_Sweep()
    {
        var vm = new MouseSensitivityViewModel { MaxSpeed = 2000f, DeadZone = 0.85f, IsLive = true };

        AdvanceMany(vm, 30);

        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewX);
        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewY);
    }

    [Fact]
    public void Higher_MaxSpeed_Moves_The_Preview_Further()
    {
        var slow = new MouseSensitivityViewModel { MaxSpeed = 600f, IsLive = true };
        var fast = new MouseSensitivityViewModel { MaxSpeed = 2400f, IsLive = true };

        AdvanceMany(slow, 60);
        AdvanceMany(fast, 60);

        var slowTravel = Math.Abs(slow.PreviewX - MouseSensitivityViewModel.PreviewCenter)
                         + Math.Abs(slow.PreviewY - MouseSensitivityViewModel.PreviewCenter);
        var fastTravel = Math.Abs(fast.PreviewX - MouseSensitivityViewModel.PreviewCenter)
                         + Math.Abs(fast.PreviewY - MouseSensitivityViewModel.PreviewCenter);

        Assert.True(fastTravel > slowTravel * 2, $"slow={slowTravel}, fast={fastTravel}");
    }

    [Fact]
    public void CurrentSpeed_Reports_The_Smoothed_Stick_Speed()
    {
        var vm = new MouseSensitivityViewModel { MaxSpeed = 1600f, ResponseTime = 0f, IsLive = true };

        AdvanceMany(vm, 30);

        Assert.True(vm.CurrentSpeed > 100, $"expected real speed, got {vm.CurrentSpeed}");
    }

    [Fact]
    public void Response_Curve_Points_Are_Sampled_From_Zero_To_One()
    {
        var vm = new MouseSensitivityViewModel { Curve = ResponseCurve.Precise };

        Assert.Equal(33, vm.ResponseCurvePoints.Count);
        Assert.Equal(0, vm.ResponseCurvePoints[0].X);
        Assert.Equal(0, vm.ResponseCurvePoints[0].Y);
        Assert.Equal(1, vm.ResponseCurvePoints[^1].X);
        Assert.Equal(1, vm.ResponseCurvePoints[^1].Y);
    }

    [Fact]
    public void Changing_Curve_Rebuilds_The_Graph()
    {
        var vm = new MouseSensitivityViewModel { Curve = ResponseCurve.Linear };
        var linearY = vm.ResponseCurvePoints[8].Y;

        vm.Curve = ResponseCurve.Aggressive;

        Assert.NotEqual(linearY, vm.ResponseCurvePoints[8].Y);
    }

    [Fact]
    public void Reset_Restores_Defaults_And_Recenters_The_Preview()
    {
        var vm = new MouseSensitivityViewModel { MaxSpeed = 4000f, IsLive = true };
        AdvanceMany(vm, 30);

        vm.Reset();

        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewX);
        Assert.Equal(1600f, vm.MaxSpeed);
    }

    [Fact]
    public void Preview_Settles_To_Rest_When_There_Is_No_Live_Input()
    {
        var vm = new MouseSensitivityViewModel();

        for (var i = 0; i < 700; i++) // > 10 simulated seconds at the 16 ms preview interval
        {
            vm.Advance(TimeSpan.FromMilliseconds(16));
        }

        Assert.Equal(0, vm.CurrentSpeed);
        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewX);
        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewY);
    }

    [Fact]
    public void Rest_Is_Reached_In_A_Finite_Number_Of_Ticks()
    {
        var vm = new MouseSensitivityViewModel { MaxSpeed = 4000f, IsLive = true };

        AdvanceMany(vm, 120);
        Assert.True(vm.CurrentSpeed > 0, "sweep should be producing speed while live");

        vm.IsLive = false;

        var ticksToRest = 0;
        while (ticksToRest < 700 &&
               (vm.CurrentSpeed != 0
                || vm.PreviewX != MouseSensitivityViewModel.PreviewCenter
                || vm.PreviewY != MouseSensitivityViewModel.PreviewCenter))
        {
            vm.Advance(TimeSpan.FromMilliseconds(16));
            ticksToRest++;
        }

        Assert.Equal(0, vm.CurrentSpeed);
        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewX);
        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewY);
        Assert.True(ticksToRest < 700, $"rest took {ticksToRest} ticks, expected a finite decay");
    }

    [Fact]
    public void Rest_Is_Stable_Once_Reached()
    {
        var vm = new MouseSensitivityViewModel { MaxSpeed = 4000f, IsLive = true };
        AdvanceMany(vm, 120);

        vm.IsLive = false;
        AdvanceMany(vm, 700);

        var settledX = vm.PreviewX;
        var settledY = vm.PreviewY;
        var settledSpeed = vm.CurrentSpeed;

        AdvanceMany(vm, 700);

        Assert.Equal(settledX, vm.PreviewX);
        Assert.Equal(settledY, vm.PreviewY);
        Assert.Equal(settledSpeed, vm.CurrentSpeed);
        Assert.Equal(0, vm.CurrentSpeed);
    }

    [Fact]
    public void Sweep_Resumes_When_Live_Input_Returns()
    {
        var vm = new MouseSensitivityViewModel { MaxSpeed = 4000f };

        AdvanceMany(vm, 700);
        Assert.Equal(0, vm.CurrentSpeed);

        vm.IsLive = true;
        AdvanceMany(vm, 60);

        Assert.True(vm.CurrentSpeed > 0, $"expected the sweep to resume, got {vm.CurrentSpeed}");
        Assert.True(
            vm.PreviewX != MouseSensitivityViewModel.PreviewCenter
            || vm.PreviewY != MouseSensitivityViewModel.PreviewCenter,
            "expected the preview dot to leave center once the sweep resumes");
    }

    [Fact]
    public void Sweep_Does_Not_Advance_Its_Phase_While_At_Rest()
    {
        var resumed = new MouseSensitivityViewModel { MaxSpeed = 4000f, ResponseTime = 0f, IsLive = true };
        var continuous = new MouseSensitivityViewModel { MaxSpeed = 4000f, ResponseTime = 0f, IsLive = true };

        AdvanceMany(resumed, 60);
        AdvanceMany(continuous, 60);

        resumed.IsLive = false;
        AdvanceMany(resumed, 100);
        resumed.IsLive = true;
        AdvanceMany(resumed, 1);

        AdvanceMany(continuous, 1);

        Assert.Equal(continuous.CurrentSpeed, resumed.CurrentSpeed);
    }

    [Fact]
    public void Changing_A_Setting_Keeps_The_Smoothing_State_So_Speed_Does_Not_Reset()
    {
        // Dragging a slider fires a settings change on every tick. The processor must keep
        // its smoothed velocity across that change, or CurrentSpeed craters back toward zero
        // for a tick and the preview stutters ("不停重置").
        var vm = new MouseSensitivityViewModel { MaxSpeed = 2000f, ResponseTime = 0.15f, IsLive = true };
        AdvanceMany(vm, 100);
        var speedBefore = vm.CurrentSpeed;
        Assert.True(speedBefore > 500, $"sweep should have built up speed, got {speedBefore}");

        vm.MaxSpeed = 2100f;
        AdvanceMany(vm, 1);

        Assert.True(vm.CurrentSpeed > speedBefore * 0.5,
            $"changing a setting reset the smoothing state: {vm.CurrentSpeed} vs {speedBefore}");
    }

    [Fact]
    public void Settling_To_Rest_Zeroes_Cumulative_Displacement()
    {
        var vm = new MouseSensitivityViewModel { MaxSpeed = 4000f, IsLive = true };
        AdvanceMany(vm, 120);
        Assert.True(vm.TravelDistance > 0, "sweep should accumulate displacement while live");

        vm.IsLive = false;
        AdvanceMany(vm, 700);

        Assert.Equal(0, vm.CurrentSpeed);
        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewX);
        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewY);
        Assert.Equal(0, vm.TravelDistance);
    }

    [Fact]
    public void Axis_Gain_And_Precision_Mode_Survive_Build_Load_And_Reset()
    {
        var vm = new MouseSensitivityViewModel
        {
            ScaleX = 0.7f,
            ScaleY = 1.4f,
            PrecisionMode = PrecisionMode.Toggle,
        };

        var settings = vm.BuildSettings();
        Assert.Equal(0.7f, settings.ScaleX);
        Assert.Equal(1.4f, settings.ScaleY);
        Assert.Equal(PrecisionMode.Toggle, settings.PrecisionMode);

        var loaded = new MouseSensitivityViewModel();
        loaded.LoadFrom(settings);
        Assert.Equal(0.7f, loaded.ScaleX);
        Assert.Equal(1.4f, loaded.ScaleY);
        Assert.Equal(PrecisionMode.Toggle, loaded.PrecisionMode);

        loaded.Reset();
        Assert.Equal(1f, loaded.ScaleX);
        Assert.Equal(1f, loaded.ScaleY);
        Assert.Equal(PrecisionMode.Hold, loaded.PrecisionMode);
    }

    [Fact]
    public void Selecting_A_Precision_Mode_Option_Updates_The_Core_Setting()
    {
        var vm = new MouseSensitivityViewModel();

        vm.SelectedPrecisionModeOption = vm.PrecisionModeOptions.Single(option => option.Value == PrecisionMode.Toggle);

        Assert.Equal(PrecisionMode.Toggle, vm.PrecisionMode);
        Assert.Equal(PrecisionMode.Toggle, vm.BuildSettings().PrecisionMode);

        vm.LoadFrom(new MouseStickSettings { PrecisionMode = PrecisionMode.Hold });
        Assert.Equal(PrecisionMode.Hold, vm.SelectedPrecisionModeOption!.Value);
    }

    [Fact]
    public void Zero_Horizontal_Gain_Stops_Horizontal_Preview_Movement()
    {
        var vm = new MouseSensitivityViewModel
        {
            ScaleX = 0f,
            ScaleY = 1f,
            MaxSpeed = 2000f,
            ResponseTime = 0f,
            IsLive = true,
        };

        AdvanceMany(vm, 30);

        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewX);
        Assert.NotEqual(MouseSensitivityViewModel.PreviewCenter, vm.PreviewY);
    }
}
