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
            CurveStrength = 0.6f,
            ResponseTime = 0.12f,
            PrecisionScale = 0.5f,
            InvertY = false,
        };

        var settings = vm.BuildSettings();

        Assert.Equal(2400f, settings.MaxSpeed);
        Assert.Equal(0.25f, settings.DeadZone);
        Assert.Equal(ResponseCurve.Aggressive, settings.Curve);
        Assert.Equal(0.6f, settings.CurveStrength);
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
    public void CurveStrength_Above_One_Is_Clamped_To_The_Full_Curve()
    {
        var vm = new MouseSensitivityViewModel();

        vm.CurveStrength = 1.5f;

        Assert.Equal(1f, vm.CurveStrength);
        Assert.Equal(1f, vm.BuildSettings().CurveStrength);
    }

    [Fact]
    public void CurveStrength_Below_Zero_Is_Clamped_To_Linear()
    {
        var vm = new MouseSensitivityViewModel();

        vm.CurveStrength = -0.5f;

        Assert.Equal(0f, vm.CurveStrength);
        Assert.Equal(0f, vm.BuildSettings().CurveStrength);
    }

    [Fact]
    public void LoadFrom_Clamps_An_Imported_Strength_Above_One()
    {
        // An old profile could legally store strength > 1; loading it into the panel must
        // normalize it back into [0,1] rather than echo the extrapolating value back out.
        var vm = new MouseSensitivityViewModel();

        vm.LoadFrom(new MouseStickSettings { CurveStrength = 1.75f });

        Assert.Equal(1f, vm.CurveStrength);
        Assert.Equal(1f, vm.BuildSettings().CurveStrength);
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
    public void The_Reported_Precision_Mode_Follows_The_Loaded_Setting()
    {
        var vm = new MouseSensitivityViewModel();

        // The panel is read-only: it reports the mode the mapping carries, labelled with the
        // same Chinese semantics the mapping surface uses, not the Hold / Toggle enum names.
        Assert.Equal(PrecisionMode.Hold, vm.PrecisionMode);
        Assert.Equal("一直按住才触发", vm.PrecisionModeLabel);

        vm.LoadFrom(new MouseStickSettings { PrecisionMode = PrecisionMode.Toggle });
        Assert.Equal(PrecisionMode.Toggle, vm.PrecisionMode);
        Assert.Equal("按一下启用，再按一下取消", vm.PrecisionModeLabel);
        Assert.Equal(PrecisionMode.Toggle, vm.BuildSettings().PrecisionMode);

        vm.LoadFrom(new MouseStickSettings { PrecisionMode = PrecisionMode.Hold });
        Assert.Equal(PrecisionMode.Hold, vm.PrecisionMode);
        Assert.Equal("一直按住才触发", vm.PrecisionModeLabel);
    }

    [Fact]
    public void The_Reported_Precision_Mode_Notifies_When_The_Loaded_Mode_Changes()
    {
        var vm = new MouseSensitivityViewModel();
        var notified = new List<string>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName!);

        vm.LoadFrom(new MouseStickSettings { PrecisionMode = PrecisionMode.Toggle });

        Assert.Contains(nameof(MouseSensitivityViewModel.PrecisionModeLabel), notified);
        Assert.Contains(nameof(MouseSensitivityViewModel.PrecisionMode), notified);
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

    [Fact]
    public void Real_Input_Mode_Shows_Raw_Deadzone_Filtered_And_Final_Speed()
    {
        var vm = new MouseSensitivityViewModel
        {
            MaxSpeed = 1600f,
            ResponseTime = 0f,
            IsLive = true,
            IsRealInputMode = true,
        };

        vm.ApplySample(GamepadState.Create(default, 0f, 0f, 0.5f, -0.25f));

        // Stage 1: the raw stick, exactly as the pad reported it.
        Assert.Equal(0.5, vm.RawStickX);
        Assert.Equal(-0.25, vm.RawStickY);
        Assert.Equal(0.559, vm.RawMagnitude, 3);

        // Stage 2: the deadzone-filtered deflection the response curve actually sees -
        // the radial ramp rescales (0.559 - 0.15) / (1 - 0.15) back onto full deflection.
        Assert.Equal(0.481, vm.FilteredMagnitude, 3);
        Assert.Equal(0.430, vm.FilteredStickX, 3);

        // Stage 3: the engine's own smoothed speed, which with no smoothing equals the
        // daily target speed computed from the filtered deflection.
        vm.Advance(TimeSpan.FromSeconds(1.0 / 64.0));

        Assert.Equal(vm.DailySpeed, vm.CurrentSpeed, 3);
        Assert.True(vm.CurrentSpeed > 0, $"expected real speed, got {vm.CurrentSpeed}");
    }

    [Fact]
    public void Real_Input_Mode_Reads_The_Right_Stick_Only()
    {
        var vm = new MouseSensitivityViewModel
        {
            MaxSpeed = 1600f,
            ResponseTime = 0f,
            IsLive = true,
            IsRealInputMode = true,
        };

        // The mouse mapping reads the right stick, so the left one is not the mouse's input.
        vm.ApplySample(GamepadState.Create(default, 0.8f, 0f, 0f, 0f));
        vm.Advance(TimeSpan.FromSeconds(1.0 / 64.0));

        Assert.Equal(0, vm.CurrentSpeed);
        Assert.Equal(0, vm.RawMagnitude);

        vm.ApplySample(GamepadState.Create(default, 0f, 0f, 0.8f, 0f));
        vm.Advance(TimeSpan.FromSeconds(1.0 / 64.0));

        Assert.True(vm.CurrentSpeed > 0, $"expected real speed, got {vm.CurrentSpeed}");
    }

    [Fact]
    public void Demo_Mode_Ignores_Real_Samples()
    {
        // The synthetic sweep is the default preview: a real sample arriving while the demo
        // radio is checked must not hijack the script or move the calibration readouts.
        var vm = new MouseSensitivityViewModel { MaxSpeed = 1600f, ResponseTime = 0f, IsLive = true };

        vm.ApplySample(GamepadState.Create(default, 0f, 0f, 0.9f, 0f));

        Assert.Equal(0, vm.RawStickX);
        Assert.Equal(0, vm.RawMagnitude);

        AdvanceMany(vm, 30);

        Assert.True(vm.CurrentSpeed > 0, $"expected the demo sweep, got {vm.CurrentSpeed}");
    }

    [Fact]
    public void The_Precision_Key_From_Real_Input_Splits_Daily_And_Precision_Speed()
    {
        var vm = new MouseSensitivityViewModel
        {
            MaxSpeed = 1600f,
            PrecisionScale = 0.35f,
            ResponseTime = 0f,
            IsLive = true,
            IsRealInputMode = true,
        };

        vm.ApplySample(GamepadState.Create(default, 0f, 0f, 1f, 0f));
        vm.Advance(TimeSpan.FromSeconds(1.0 / 64.0));

        // Daily and precision are tuned separately: each has its own readout at full
        // deflection, and the key only moves the engine's output, never the daily number.
        Assert.False(vm.PrecisionActive);
        Assert.Equal(1600, vm.DailySpeed, 3);
        Assert.Equal(560, vm.PrecisionSpeed, 3);
        Assert.Equal(1600, vm.CurrentSpeed, 3);

        vm.ApplySample(GamepadState.Create(GamepadButton.LeftStick.ToMask(), 0f, 0f, 1f, 0f));
        vm.Advance(TimeSpan.FromSeconds(1.0 / 64.0));

        Assert.True(vm.PrecisionActive);
        Assert.Equal(560, vm.CurrentSpeed, 3);
        Assert.Equal(1600, vm.DailySpeed, 3);
    }

    [Fact]
    public void Holding_The_Stick_Still_Accumulates_Drift_Samples_On_A_Time_Basis()
    {
        // The regression this fixes: a pad reports a sample when its state changes and then
        // goes quiet - the daemon forwards nothing at all while the stick is still - so a
        // collection driven by the input stream captured exactly one sample and could never
        // suggest a deadzone. The collection now rides the preview clock, so holding the
        // stick still fills the distribution the suggestion is built from.
        var vm = new MouseSensitivityViewModel { IsRealInputMode = true, IsLive = true };

        // One sample only: a stick resting at (0.04, 0.01), which then changes no more.
        vm.ApplySample(GamepadState.Create(default, 0f, 0f, 0.04f, 0.01f));

        vm.StartDriftMeasurementCommand.Execute(null);

        for (var i = 0; i < 40; i++)
        {
            vm.Advance(MouseSensitivityViewModel.DriftSampleInterval);
        }

        Assert.True(vm.IsMeasuringDrift);
        Assert.Equal(40, vm.DriftSampleCount);
        Assert.Equal(0.0412, vm.DriftMeanMagnitude, 3);
        Assert.Equal(0.0412, vm.DriftMaxMagnitude, 3);

        vm.StopDriftMeasurementCommand.Execute(null);

        // The suggestion clears the observed peak with headroom, rounded up to a step the
        // deadzone slider can actually represent - and it waits for confirmation.
        Assert.Equal(0.06, vm.SuggestedDeadZone!.Value, 3);
        Assert.Equal("0.06", vm.SuggestedDeadZoneText);
        Assert.True(vm.CanApplySuggestion);
    }

    [Fact]
    public void Stopping_The_Drift_Measurement_Stops_The_Samples()
    {
        var vm = new MouseSensitivityViewModel { IsRealInputMode = true, IsLive = true };

        vm.ApplySample(GamepadState.Create(default, 0f, 0f, 0.04f, 0.01f));
        vm.StartDriftMeasurementCommand.Execute(null);
        for (var i = 0; i < 10; i++)
        {
            vm.Advance(MouseSensitivityViewModel.DriftSampleInterval);
        }

        vm.StopDriftMeasurementCommand.Execute(null);
        for (var i = 0; i < 30; i++)
        {
            vm.Advance(MouseSensitivityViewModel.DriftSampleInterval);
        }

        Assert.Equal(10, vm.DriftSampleCount);
        Assert.Null(vm.SuggestedDeadZone);
    }

    [Fact]
    public void Drift_Measurement_Suggests_A_Deadzone_Above_The_Observed_Rest_Drift()
    {
        var vm = new MouseSensitivityViewModel { IsRealInputMode = true };

        vm.StartDriftMeasurementCommand.Execute(null);
        Assert.True(vm.IsMeasuringDrift);

        // A stick resting with noise: the magnitudes hover around 0.05 with a 0.054 peak.
        // The noise arrives as samples; the collection samples it on the preview clock.
        for (var i = 0; i < 40; i++)
        {
            vm.ApplySample(GamepadState.Create(default, 0f, 0f, i % 2 == 0 ? 0.05f : 0.03f, i % 2 == 0 ? 0.02f : -0.04f));
            vm.Advance(MouseSensitivityViewModel.DriftSampleInterval);
        }

        vm.StopDriftMeasurementCommand.Execute(null);

        Assert.False(vm.IsMeasuringDrift);
        Assert.Equal(40, vm.DriftSampleCount);
        Assert.Equal(0.054, vm.DriftMaxMagnitude, 3);

        // The suggestion clears the observed peak with headroom, rounded up to a step the
        // deadzone slider can actually represent - and it waits for confirmation.
        Assert.Equal(0.07, vm.SuggestedDeadZone!.Value, 3);
        Assert.Equal("0.07", vm.SuggestedDeadZoneText);
        Assert.True(vm.CanApplySuggestion);
        Assert.Equal(0.15f, vm.DeadZone);

        vm.ApplySuggestedDeadZoneCommand.Execute(null);

        Assert.Equal(0.07f, vm.DeadZone);
        Assert.Equal(0.07f, vm.BuildSettings().DeadZone);
    }

    [Fact]
    public void A_Clean_Stick_Suggests_No_Deadzone()
    {
        var vm = new MouseSensitivityViewModel { IsRealInputMode = true };

        vm.StartDriftMeasurementCommand.Execute(null);
        for (var i = 0; i < 30; i++)
        {
            vm.ApplySample(GamepadState.Neutral);
            vm.Advance(MouseSensitivityViewModel.DriftSampleInterval);
        }

        vm.StopDriftMeasurementCommand.Execute(null);

        Assert.Null(vm.SuggestedDeadZone);
        Assert.Equal("—", vm.SuggestedDeadZoneText);
        Assert.False(vm.CanApplySuggestion);

        // Nothing to confirm, so applying must not silently wipe the configured deadzone.
        vm.ApplySuggestedDeadZoneCommand.Execute(null);

        Assert.Equal(0.15f, vm.DeadZone);
    }

    [Fact]
    public void The_Deadzone_Suggestion_Needs_Enough_Samples_And_Is_Clamped_To_The_Slider()
    {
        var few = new MouseSensitivityViewModel { IsRealInputMode = true };
        few.StartDriftMeasurementCommand.Execute(null);
        for (var i = 0; i < 19; i++)
        {
            few.ApplySample(GamepadState.Create(default, 0f, 0f, 0.05f, 0f));
            few.Advance(MouseSensitivityViewModel.DriftSampleInterval);
        }

        Assert.Null(few.SuggestedDeadZone);

        few.Advance(MouseSensitivityViewModel.DriftSampleInterval);
        Assert.Equal(0.07, few.SuggestedDeadZone!.Value, 3);

        // A wildly drifting pad must still suggest a deadzone the slider can reach.
        var wild = new MouseSensitivityViewModel { IsRealInputMode = true };
        wild.StartDriftMeasurementCommand.Execute(null);
        for (var i = 0; i < 30; i++)
        {
            wild.ApplySample(GamepadState.Create(default, 0f, 0f, 1f, 0f));
            wild.Advance(MouseSensitivityViewModel.DriftSampleInterval);
        }

        Assert.Equal(0.6, wild.SuggestedDeadZone!.Value, 3);
    }

    [Fact]
    public void Leaving_Real_Input_Mode_Clears_The_Calibration_State()
    {
        var vm = new MouseSensitivityViewModel { IsLive = true, IsRealInputMode = true };

        vm.ApplySample(GamepadState.Create(default, 0f, 0f, 0.5f, 0f));
        vm.StartDriftMeasurementCommand.Execute(null);
        for (var i = 0; i < 30; i++)
        {
            vm.ApplySample(GamepadState.Create(default, 0f, 0f, 0.04f, 0.01f));
            vm.Advance(MouseSensitivityViewModel.DriftSampleInterval);
        }

        Assert.True(vm.SuggestedDeadZone is not null);

        vm.IsRealInputMode = false;

        // Re-entering starts a clean session instead of confirming stale numbers, and the
        // parked sample decays the preview like any other rest.
        Assert.False(vm.IsMeasuringDrift);
        Assert.Equal(0, vm.DriftSampleCount);
        Assert.Null(vm.SuggestedDeadZone);
        Assert.Equal(0, vm.RawStickX);
        Assert.Equal(0, vm.RawMagnitude);
        Assert.Equal(0, vm.DailySpeed);
        Assert.False(vm.IsRealInputMode);

        // With the calibration off and the pad still live the demo sweep takes back over;
        // dropping the lease parks the preview at rest.
        AdvanceMany(vm, 5);
        Assert.True(vm.CurrentSpeed > 0, "leaving the calibration returns to the demo sweep");

        vm.IsLive = false;
        AdvanceMany(vm, 700);

        Assert.Equal(0, vm.CurrentSpeed);
        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewX);
        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewY);
    }

    [Fact]
    public void Reset_Returns_The_Panel_To_Demo_Mode()
    {
        var vm = new MouseSensitivityViewModel { IsRealInputMode = true };

        vm.StartDriftMeasurementCommand.Execute(null);
        vm.Reset();

        Assert.False(vm.IsRealInputMode);
        Assert.False(vm.IsRealInputMode);
        Assert.False(vm.IsMeasuringDrift);
        Assert.Null(vm.SuggestedDeadZone);
    }
}
