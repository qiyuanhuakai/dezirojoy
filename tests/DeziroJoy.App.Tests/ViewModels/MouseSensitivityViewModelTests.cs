using DeziroJoy.App.ViewModels;
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
    public void Preview_Stays_Put_When_MaxSpeed_Is_Zero()
    {
        var vm = new MouseSensitivityViewModel { MaxSpeed = 0f };

        AdvanceMany(vm, 30);

        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewX);
        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewY);
    }

    [Fact]
    public void Preview_Stays_Put_When_Deadzone_Covers_The_Sweep()
    {
        var vm = new MouseSensitivityViewModel { MaxSpeed = 2000f, DeadZone = 0.85f };

        AdvanceMany(vm, 30);

        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewX);
        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewY);
    }

    [Fact]
    public void Higher_MaxSpeed_Moves_The_Preview_Further()
    {
        var slow = new MouseSensitivityViewModel { MaxSpeed = 600f };
        var fast = new MouseSensitivityViewModel { MaxSpeed = 2400f };

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
        var vm = new MouseSensitivityViewModel { MaxSpeed = 1600f, ResponseTime = 0f };

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
        var vm = new MouseSensitivityViewModel { MaxSpeed = 4000f };
        AdvanceMany(vm, 30);

        vm.Reset();

        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, vm.PreviewX);
        Assert.Equal(1600f, vm.MaxSpeed);
    }
}
