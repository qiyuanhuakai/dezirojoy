using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

public class ResponseCurvesTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(1f)]
    public void Linear_With_Zero_Strength_Is_Identity(float magnitude)
    {
        Assert.Equal(magnitude, ResponseCurves.Shape(magnitude, ResponseCurve.Linear, 0f));
    }

    [Fact]
    public void Precise_Matches_Smoothstep()
    {
        Assert.Equal(0f, ResponseCurves.Shape(0f, ResponseCurve.Precise, 1f));
        Assert.Equal(0.15625f, ResponseCurves.Shape(0.25f, ResponseCurve.Precise, 1f));
        Assert.Equal(0.5f, ResponseCurves.Shape(0.5f, ResponseCurve.Precise, 1f));
        Assert.Equal(1f, ResponseCurves.Shape(1f, ResponseCurve.Precise, 1f));
    }

    [Fact]
    public void Aggressive_Is_Quadratic()
    {
        Assert.Equal(0.0625f, ResponseCurves.Shape(0.25f, ResponseCurve.Aggressive, 1f));
        Assert.Equal(0.25f, ResponseCurves.Shape(0.5f, ResponseCurve.Aggressive, 1f));
    }

    [Fact]
    public void Responsive_Is_Square_Root()
    {
        Assert.Equal(0.5f, ResponseCurves.Shape(0.25f, ResponseCurve.Responsive, 1f));
        Assert.Equal(0.70710678f, ResponseCurves.Shape(0.5f, ResponseCurve.Responsive, 1f), 5);
    }

    [Fact]
    public void Strength_Blends_Between_Linear_And_Curve()
    {
        // 0.25 + (0.15625 - 0.25) * 0.5
        Assert.Equal(0.203125f, ResponseCurves.Shape(0.25f, ResponseCurve.Precise, 0.5f), 5);
    }

    [Fact]
    public void Magnitude_Is_Clamped_To_Unit_Range()
    {
        Assert.Equal(1f, ResponseCurves.Shape(2f, ResponseCurve.Linear, 1f));
        Assert.Equal(0f, ResponseCurves.Shape(-1f, ResponseCurve.Linear, 1f));
    }
}
