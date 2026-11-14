using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

public class ScrollSensitivityViewModelTests
{
    private static void AdvanceMany(ScrollSensitivityViewModel vm, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            vm.Advance(TimeSpan.FromSeconds(1.0 / 64.0));
        }
    }

    [Fact]
    public void BuildSettings_Reflects_Every_Property()
    {
        var vm = new ScrollSensitivityViewModel
        {
            MaxTicksPerSecond = 20f,
            DeadZone = 0.3f,
            Curve = ResponseCurve.Linear,
            CurveStrength = 0f,
            ResponseTime = 0f,
            EnableHorizontal = false,
        };

        var settings = vm.BuildSettings();

        Assert.Equal(20f, settings.MaxTicksPerSecond);
        Assert.Equal(0.3f, settings.DeadZone);
        Assert.False(settings.EnableHorizontal);
    }

    [Fact]
    public void Preview_Accumulates_Wheel_Notches()
    {
        var vm = new ScrollSensitivityViewModel
        {
            MaxTicksPerSecond = 64f,
            DeadZone = 0f,
            Curve = ResponseCurve.Linear,
            CurveStrength = 0f,
            ResponseTime = 0f,
            IsLive = true,
        };

        AdvanceMany(vm, 64);

        Assert.True(Math.Abs(vm.WheelOffset) >= 1, $"expected scrolling, got {vm.WheelOffset}");
    }

    [Fact]
    public void Zero_Rate_Does_Not_Scroll()
    {
        var vm = new ScrollSensitivityViewModel { MaxTicksPerSecond = 0f, IsLive = true };

        AdvanceMany(vm, 30);

        Assert.Equal(0, vm.WheelOffset);
    }

    [Fact]
    public void Reset_Clears_The_Offset()
    {
        var vm = new ScrollSensitivityViewModel { MaxTicksPerSecond = 64f, DeadZone = 0f, Curve = ResponseCurve.Linear, CurveStrength = 0f, ResponseTime = 0f, IsLive = true };

        AdvanceMany(vm, 30);
        vm.Reset();

        Assert.Equal(0, vm.WheelOffset);
    }

    [Fact]
    public void Preview_Settles_To_Rest_When_There_Is_No_Live_Input()
    {
        var vm = new ScrollSensitivityViewModel();

        for (var i = 0; i < 700; i++) // > 10 simulated seconds at the 16 ms preview interval
        {
            vm.Advance(TimeSpan.FromMilliseconds(16));
        }

        Assert.Equal(0, vm.CurrentRate);
    }

    [Fact]
    public void Rest_Is_Reached_In_A_Finite_Number_Of_Ticks()
    {
        var vm = new ScrollSensitivityViewModel { MaxTicksPerSecond = 64f, IsLive = true };

        AdvanceMany(vm, 120);
        Assert.True(vm.CurrentRate > 0, "sweep should be producing a rate while live");

        vm.IsLive = false;

        var ticksToRest = 0;
        while (ticksToRest < 700 && vm.CurrentRate != 0)
        {
            vm.Advance(TimeSpan.FromMilliseconds(16));
            ticksToRest++;
        }

        Assert.Equal(0, vm.CurrentRate);
        Assert.True(ticksToRest < 700, $"rest took {ticksToRest} ticks, expected a finite decay");
    }

    [Fact]
    public void Rest_Is_Stable_Once_Reached()
    {
        var vm = new ScrollSensitivityViewModel { MaxTicksPerSecond = 64f, IsLive = true };
        AdvanceMany(vm, 120);

        vm.IsLive = false;
        AdvanceMany(vm, 700);

        var settledOffset = vm.WheelOffset;
        AdvanceMany(vm, 700);

        Assert.Equal(settledOffset, vm.WheelOffset);
        Assert.Equal(0, vm.CurrentRate);
    }

    [Fact]
    public void Sweep_Resumes_When_Live_Input_Returns()
    {
        var vm = new ScrollSensitivityViewModel { MaxTicksPerSecond = 64f };

        AdvanceMany(vm, 700);
        Assert.Equal(0, vm.CurrentRate);

        vm.IsLive = true;
        AdvanceMany(vm, 60);

        Assert.True(vm.CurrentRate > 0, $"expected the sweep to resume, got {vm.CurrentRate}");
    }

    [Fact]
    public void Changing_A_Setting_Keeps_The_Smoothing_State_So_Rate_Does_Not_Reset()
    {
        var vm = new ScrollSensitivityViewModel { MaxTicksPerSecond = 40f, ResponseTime = 0.15f, IsLive = true };
        AdvanceMany(vm, 100);
        var rateBefore = vm.CurrentRate;
        Assert.True(rateBefore > 5, $"sweep should have built up rate, got {rateBefore}");

        vm.MaxTicksPerSecond = 42f;
        AdvanceMany(vm, 1);

        Assert.True(vm.CurrentRate > rateBefore * 0.5,
            $"changing a setting reset the smoothing state: {vm.CurrentRate} vs {rateBefore}");
    }

    [Fact]
    public void InvertY_Changes_The_Live_Preview_Direction()
    {
        var normal = new ScrollSensitivityViewModel
        {
            MaxTicksPerSecond = 64f,
            DeadZone = 0f,
            Curve = ResponseCurve.Linear,
            CurveStrength = 0f,
            ResponseTime = 0f,
            IsLive = true,
        };
        var inverted = new ScrollSensitivityViewModel
        {
            MaxTicksPerSecond = 64f,
            DeadZone = 0f,
            Curve = ResponseCurve.Linear,
            CurveStrength = 0f,
            ResponseTime = 0f,
            InvertY = true,
            IsLive = true,
        };

        AdvanceMany(normal, 16);
        AdvanceMany(inverted, 16);

        Assert.NotEqual(0, normal.WheelOffset);
        Assert.Equal(-normal.WheelOffset, inverted.WheelOffset, 6);
    }

    [Fact]
    public void CurveStrength_Above_One_Is_Clamped_To_The_Full_Curve()
    {
        var vm = new ScrollSensitivityViewModel();

        vm.CurveStrength = 1.5f;

        Assert.Equal(1f, vm.CurveStrength);
        Assert.Equal(1f, vm.BuildSettings().CurveStrength);
    }

    [Fact]
    public void CurveStrength_Below_Zero_Is_Clamped_To_Linear()
    {
        var vm = new ScrollSensitivityViewModel();

        vm.CurveStrength = -0.5f;

        Assert.Equal(0f, vm.CurveStrength);
        Assert.Equal(0f, vm.BuildSettings().CurveStrength);
    }

    [Fact]
    public void LoadFrom_Clamps_An_Imported_Strength_Above_One()
    {
        // An old profile could legally store strength > 1; loading it into the panel must
        // normalize it back into [0,1] rather than echo the extrapolating value back out.
        var vm = new ScrollSensitivityViewModel();

        vm.LoadFrom(new ScrollStickSettings { CurveStrength = 1.75f });

        Assert.Equal(1f, vm.CurveStrength);
        Assert.Equal(1f, vm.BuildSettings().CurveStrength);
    }

    [Fact]
    public void Real_Input_Mode_Shows_Raw_Deadzone_Filtered_And_Final_Rate()
    {
        var vm = new ScrollSensitivityViewModel
        {
            MaxTicksPerSecond = 24f,
            ResponseTime = 0f,
            IsLive = true,
            IsRealInputMode = true,
        };

        vm.ApplySample(GamepadState.Create(default, 0f, 0.6f, 0f, 0f));

        // Stage 1: the raw stick, exactly as the pad reported it.
        Assert.Equal(0.6, vm.RawMagnitude, 3);

        // Stage 2: the deadzone-filtered deflection the response curve sees -
        // (0.6 - 0.2) / (1 - 0.2) ramps back onto full deflection.
        Assert.Equal(0.5, vm.FilteredMagnitude, 3);

        // Stage 3: the engine's own smoothed rate, which with no smoothing equals the
        // target rate computed from the filtered deflection.
        vm.Advance(TimeSpan.FromSeconds(1.0 / 64.0));

        Assert.Equal(vm.DailyRate, vm.CurrentRate, 3);
        Assert.True(vm.CurrentRate > 0, $"expected a real rate, got {vm.CurrentRate}");
    }

    [Fact]
    public void Real_Input_Mode_Reads_The_Left_Stick_Only()
    {
        var vm = new ScrollSensitivityViewModel
        {
            MaxTicksPerSecond = 24f,
            ResponseTime = 0f,
            IsLive = true,
            IsRealInputMode = true,
        };

        // The wheel mapping reads the left stick, so the right one is not the wheel's input.
        vm.ApplySample(GamepadState.Create(default, 0f, 0f, 0.8f, 0f));
        vm.Advance(TimeSpan.FromSeconds(1.0 / 64.0));

        Assert.Equal(0, vm.CurrentRate);
        Assert.Equal(0, vm.RawMagnitude);

        vm.ApplySample(GamepadState.Create(default, 0f, 0.8f, 0f, 0f));
        vm.Advance(TimeSpan.FromSeconds(1.0 / 64.0));

        Assert.True(vm.CurrentRate > 0, $"expected a real rate, got {vm.CurrentRate}");
    }

    [Fact]
    public void Demo_Mode_Ignores_Real_Samples()
    {
        var vm = new ScrollSensitivityViewModel { MaxTicksPerSecond = 24f, ResponseTime = 0f, IsLive = true };

        vm.ApplySample(GamepadState.Create(default, 0f, 0.9f, 0f, 0f));

        Assert.Equal(0, vm.RawMagnitude);

        AdvanceMany(vm, 30);

        Assert.True(vm.CurrentRate > 0, $"expected the demo sweep, got {vm.CurrentRate}");
    }

    [Fact]
    public void Drift_Measurement_Suggests_A_Deadzone_Above_The_Observed_Rest_Drift()
    {
        var vm = new ScrollSensitivityViewModel { IsRealInputMode = true };

        vm.StartDriftMeasurementCommand.Execute(null);
        for (var i = 0; i < 40; i++)
        {
            // The wheel reads the left stick, so the drift rides the left axes. Collection runs
            // on the preview clock, so each sample is followed by a tick that records it.
            vm.ApplySample(GamepadState.Create(default, i % 2 == 0 ? 0.05f : 0.03f, i % 2 == 0 ? 0.02f : -0.04f, 0f, 0f));
            vm.Advance(ScrollSensitivityViewModel.DriftSampleInterval);
        }

        vm.StopDriftMeasurementCommand.Execute(null);

        Assert.Equal(40, vm.DriftSampleCount);
        Assert.Equal(0.054, vm.DriftMaxMagnitude, 3);
        Assert.Equal(0.07, vm.SuggestedDeadZone!.Value, 3);
        Assert.Equal(0.2f, vm.DeadZone);

        vm.ApplySuggestedDeadZoneCommand.Execute(null);

        Assert.Equal(0.07f, vm.DeadZone);
        Assert.Equal(0.07f, vm.BuildSettings().DeadZone);
    }

    [Fact]
    public void A_Clean_Stick_Suggests_No_Deadzone()
    {
        var vm = new ScrollSensitivityViewModel { IsRealInputMode = true };

        vm.StartDriftMeasurementCommand.Execute(null);
        vm.ApplySample(GamepadState.Neutral);
        for (var i = 0; i < 30; i++)
        {
            vm.Advance(ScrollSensitivityViewModel.DriftSampleInterval);
        }

        vm.StopDriftMeasurementCommand.Execute(null);

        Assert.Null(vm.SuggestedDeadZone);
        Assert.False(vm.CanApplySuggestion);

        vm.ApplySuggestedDeadZoneCommand.Execute(null);

        Assert.Equal(0.2f, vm.DeadZone);
    }

    [Fact]
    public void Leaving_Real_Input_Mode_Clears_The_Calibration_State()
    {
        var vm = new ScrollSensitivityViewModel { IsLive = true, IsRealInputMode = true };

        vm.ApplySample(GamepadState.Create(default, 0f, 0.5f, 0f, 0f));
        vm.StartDriftMeasurementCommand.Execute(null);
        for (var i = 0; i < 30; i++)
        {
            vm.ApplySample(GamepadState.Create(default, 0.04f, 0.01f, 0f, 0f));
            vm.Advance(ScrollSensitivityViewModel.DriftSampleInterval);
        }

        Assert.True(vm.SuggestedDeadZone is not null);

        vm.IsRealInputMode = false;

        Assert.False(vm.IsMeasuringDrift);
        Assert.Equal(0, vm.DriftSampleCount);
        Assert.Null(vm.SuggestedDeadZone);
        Assert.Equal(0, vm.RawMagnitude);
        Assert.Equal(0, vm.DailyRate);
        Assert.False(vm.IsRealInputMode);

        // With the calibration off and the pad still live the demo sweep takes back over;
        // dropping the lease parks the preview at rest.
        AdvanceMany(vm, 5);
        Assert.True(vm.CurrentRate > 0, "leaving the calibration returns to the demo sweep");

        vm.IsLive = false;
        AdvanceMany(vm, 700);

        Assert.Equal(0, vm.CurrentRate);
    }

    [Fact]
    public void Reset_Returns_The_Panel_To_Demo_Mode()
    {
        var vm = new ScrollSensitivityViewModel { IsRealInputMode = true };

        vm.StartDriftMeasurementCommand.Execute(null);
        vm.Reset();

        Assert.False(vm.IsRealInputMode);
        Assert.False(vm.IsRealInputMode);
        Assert.False(vm.IsMeasuringDrift);
        Assert.Null(vm.SuggestedDeadZone);
    }
}
