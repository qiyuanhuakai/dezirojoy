using DeziroJoy.Core.Engine;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

public class StickMathTests
{
    [Fact]
    public void Zero_Deflection_Is_Untouched()
    {
        var (x, y) = StickMath.ApplyRadialDeadzone(0f, 0f, 0.15f);

        Assert.Equal(0f, x);
        Assert.Equal(0f, y);
    }

    [Fact]
    public void Deflection_Inside_Deadzone_Is_Zeroed()
    {
        var (x, y) = StickMath.ApplyRadialDeadzone(0.1f, 0.1f, 0.15f);

        Assert.Equal(0f, x);
        Assert.Equal(0f, y);
    }

    [Fact]
    public void Full_Deflection_Passes_Through()
    {
        var (x, y) = StickMath.ApplyRadialDeadzone(1f, 0f, 0.15f);

        Assert.Equal(1f, x, 5);
        Assert.Equal(0f, y, 5);
    }

    [Fact]
    public void Deadzone_Edge_Maps_To_Zero_And_Ramps_Linearly()
    {
        // magnitude 0.575 with deadzone 0.15 -> (0.575 - 0.15) / (1 - 0.15) = 0.5
        var (x, _) = StickMath.ApplyRadialDeadzone(0.575f, 0f, 0.15f);

        Assert.Equal(0.5f, x, 5);
    }

    [Fact]
    public void Diagonal_Deflection_Preserves_Direction()
    {
        var (x, y) = StickMath.ApplyRadialDeadzone(0.7f, 0.7f, 0.15f);

        Assert.Equal(x, y, 5);
        var magnitude = Math.Sqrt(x * x + y * y);
        // (sqrt(0.98) - 0.15) / (1 - 0.15)
        Assert.True(Math.Abs(magnitude - 0.988176) < 1e-4, $"unexpected magnitude {magnitude}");
    }

    [Fact]
    public void Zero_Deadzone_Is_Identity()
    {
        var (x, y) = StickMath.ApplyRadialDeadzone(0.3f, -0.4f, 0f);

        Assert.Equal(0.3f, x, 5);
        Assert.Equal(-0.4f, y, 5);
    }

    [Fact]
    public void Full_Deadzone_Zeroes_Everything()
    {
        var (x, y) = StickMath.ApplyRadialDeadzone(1f, 1f, 1f);

        Assert.Equal(0f, x);
        Assert.Equal(0f, y);
    }
}
