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

    [Theory]
    [InlineData(ResponseCurve.Linear)]
    [InlineData(ResponseCurve.Precise)]
    [InlineData(ResponseCurve.Aggressive)]
    [InlineData(ResponseCurve.Responsive)]
    public void Strength_Above_One_Behaves_As_The_Full_Curve(ResponseCurve curve)
    {
        // The blend is a convex combination only while strength stays in [0,1]; past 1 it
        // used to extrapolate (Aggressive reversed, Precise overshot). Strength now clamps,
        // so anything above 1 collapses onto the full curve rather than exaggerating.
        foreach (var m in new[] { 0.1f, 0.25f, 0.5f, 0.75f, 0.9f })
        {
            Assert.Equal(ResponseCurves.Shape(m, curve, 1f), ResponseCurves.Shape(m, curve, 1.5f));
            Assert.Equal(ResponseCurves.Shape(m, curve, 1f), ResponseCurves.Shape(m, curve, 2f));
        }
    }

    [Theory]
    [InlineData(ResponseCurve.Linear)]
    [InlineData(ResponseCurve.Precise)]
    [InlineData(ResponseCurve.Aggressive)]
    [InlineData(ResponseCurve.Responsive)]
    public void Strength_Below_Zero_Behaves_As_Linear(ResponseCurve curve)
    {
        foreach (var m in new[] { 0.1f, 0.25f, 0.5f, 0.75f, 0.9f })
        {
            Assert.Equal(ResponseCurves.Shape(m, curve, 0f), ResponseCurves.Shape(m, curve, -1f));
        }
    }

    [Fact]
    public void Extrapolating_Strength_No_Longer_Reverses_Or_Overshoots()
    {
        // The exact reported failures at strength 2: Aggressive m=0.25 went negative and
        // Precise m=0.10 / m=0.90 left [0,1]. With strength clamped they stay on the curve.
        Assert.Equal(0.0625f, ResponseCurves.Shape(0.25f, ResponseCurve.Aggressive, 2f));
        Assert.Equal(ResponseCurves.Shape(0.10f, ResponseCurve.Precise, 1f), ResponseCurves.Shape(0.10f, ResponseCurve.Precise, 2f));
        Assert.Equal(ResponseCurves.Shape(0.90f, ResponseCurve.Precise, 1f), ResponseCurves.Shape(0.90f, ResponseCurve.Precise, 2f));
    }

    [Theory]
    [InlineData(ResponseCurve.Linear)]
    [InlineData(ResponseCurve.Precise)]
    [InlineData(ResponseCurve.Aggressive)]
    [InlineData(ResponseCurve.Responsive)]
    public void Output_Stays_Within_Unit_Range_For_Strength_Between_Zero_And_One(ResponseCurve curve)
    {
        for (var s = 0f; s <= 1.0001f; s += 0.05f)
        {
            for (var i = 0; i <= 100; i++)
            {
                var m = i / 100f;
                Assert.InRange(ResponseCurves.Shape(m, curve, s), 0f, 1f);
            }
        }
    }

    [Theory]
    [InlineData(ResponseCurve.Linear)]
    [InlineData(ResponseCurve.Precise)]
    [InlineData(ResponseCurve.Aggressive)]
    [InlineData(ResponseCurve.Responsive)]
    public void Output_Never_Reverses_Direction_For_Positive_Input(ResponseCurve curve)
    {
        // A positive deflection must never map to negative output: that is the direction
        // reversal strength > 1 produced, now impossible because the blend stays convex.
        for (var s = 0f; s <= 1.0001f; s += 0.05f)
        {
            for (var i = 0; i <= 100; i++)
            {
                Assert.True(ResponseCurves.Shape(i / 100f, curve, s) >= 0f);
            }
        }
    }

    [Theory]
    [InlineData(ResponseCurve.Linear)]
    [InlineData(ResponseCurve.Precise)]
    [InlineData(ResponseCurve.Aggressive)]
    [InlineData(ResponseCurve.Responsive)]
    public void Output_Is_Non_Decreasing_As_Input_Increases(ResponseCurve curve)
    {
        for (var s = 0f; s <= 1.0001f; s += 0.05f)
        {
            var previous = float.MinValue;
            for (var i = 0; i <= 100; i++)
            {
                var y = ResponseCurves.Shape(i / 100f, curve, s);
                Assert.True(y >= previous, $"{curve} decreased at strength {s}");
                previous = y;
            }
        }
    }
}
