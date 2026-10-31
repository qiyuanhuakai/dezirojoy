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
        };

        AdvanceMany(vm, 64);

        Assert.True(Math.Abs(vm.WheelOffset) >= 1, $"expected scrolling, got {vm.WheelOffset}");
    }

    [Fact]
    public void Zero_Rate_Does_Not_Scroll()
    {
        var vm = new ScrollSensitivityViewModel { MaxTicksPerSecond = 0f };

        AdvanceMany(vm, 30);

        Assert.Equal(0, vm.WheelOffset);
    }

    [Fact]
    public void Reset_Clears_The_Offset()
    {
        var vm = new ScrollSensitivityViewModel { MaxTicksPerSecond = 64f, DeadZone = 0f, Curve = ResponseCurve.Linear, CurveStrength = 0f, ResponseTime = 0f };

        AdvanceMany(vm, 30);
        vm.Reset();

        Assert.Equal(0, vm.WheelOffset);
    }
}
