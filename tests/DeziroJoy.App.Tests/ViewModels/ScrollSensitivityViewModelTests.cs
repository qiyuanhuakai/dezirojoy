using DeziroJoy.App.ViewModels;
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
}
