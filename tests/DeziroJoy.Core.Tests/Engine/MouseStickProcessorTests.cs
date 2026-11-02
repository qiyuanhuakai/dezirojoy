using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

public class MouseStickProcessorTests
{
    private static MouseStickSettings Raw(float maxSpeed) => new()
    {
        DeadZone = 0f,
        MaxSpeed = maxSpeed,
        Curve = ResponseCurve.Linear,
        CurveStrength = 0f,
        ResponseTime = 0f,
        InvertY = false,
    };

    private const float ExactTick = 1f / 64f;

    [Fact]
    public void Full_Deflection_Moves_Exactly_MaxSpeed_Times_Dt()
    {
        var processor = new MouseStickProcessor(Raw(640f));

        var (dx, dy) = processor.Update(1f, 0f, precisionActive: false, ExactTick);

        Assert.Equal(10, dx);
        Assert.Equal(0, dy);
    }

    [Fact]
    public void Deflection_Inside_Deadzone_Produces_No_Movement()
    {
        var processor = new MouseStickProcessor(new MouseStickSettings { DeadZone = 0.15f, ResponseTime = 0f });

        var (dx, dy) = processor.Update(0.1f, 0f, precisionActive: false, 0.01f);

        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
    }

    [Fact]
    public void Precision_Modifier_Scales_Speed_Down()
    {
        var settings = new MouseStickSettings
        {
            DeadZone = 0f,
            MaxSpeed = 1000f,
            Curve = ResponseCurve.Linear,
            CurveStrength = 0f,
            ResponseTime = 0f,
            PrecisionScale = 0.35f,
        };
        var processor = new MouseStickProcessor(settings);

        var (dx, _) = processor.Update(1f, 0f, precisionActive: true, 0.01f);

        Assert.Equal(3, dx); // 1000 * 0.35 * 0.01 = 3.5 -> truncated 3
    }

    [Fact]
    public void InvertY_Flips_Vertical_Direction()
    {
        var settings = Raw(640f) with { InvertY = true };
        var processor = new MouseStickProcessor(settings);

        var (_, dy) = processor.Update(0f, 1f, precisionActive: false, ExactTick);

        Assert.Equal(-10, dy);
    }

    [Fact]
    public void Response_Time_Smooths_The_First_Tick()
    {
        var settings = Raw(1000f) with { ResponseTime = 0.08f };
        var processor = new MouseStickProcessor(settings);

        var (dx, _) = processor.Update(1f, 0f, precisionActive: false, 0.01f);

        // 1000 * (1 - e^(-0.01/0.08)) * 0.01 = 1.175... -> 1 px
        Assert.Equal(1, dx);
    }

    [Fact]
    public void Sub_Pixel_Movement_Accumulates_Until_A_Whole_Pixel()
    {
        // 512 px/s at dt = 1/1024 s -> exactly 0.5 px per tick, 2 ticks per pixel
        var processor = new MouseStickProcessor(Raw(512f));

        var first = processor.Update(1f, 0f, precisionActive: false, 1f / 1024f);
        var second = processor.Update(1f, 0f, precisionActive: false, 1f / 1024f);

        Assert.Equal(0, first.Dx);
        Assert.Equal(1, second.Dx);
    }

    [Fact]
    public void Releasing_The_Stick_Decays_Speed_Toward_Zero()
    {
        var settings = Raw(1000f) with { ResponseTime = 0.08f };
        var processor = new MouseStickProcessor(settings);

        for (var i = 0; i < 20; i++)
        {
            processor.Update(1f, 0f, precisionActive: false, 0.016f);
        }

        var fullSpeed = processor.CurrentSpeed;

        for (var i = 0; i < 5; i++)
        {
            processor.Update(0f, 0f, precisionActive: false, 0.016f);
        }

        var decayed = processor.CurrentSpeed;

        Assert.True(fullSpeed > 900, $"expected near-max speed, got {fullSpeed}");
        Assert.True(decayed < fullSpeed * 0.5, $"expected decay, got {decayed}");
        Assert.True(decayed >= 0);
    }

    [Fact]
    public void Per_Axis_Scale_Is_Applied()
    {
        var settings = Raw(640f) with { ScaleX = 0.5f, ScaleY = 2f };
        var processor = new MouseStickProcessor(settings);

        var (dx, _) = processor.Update(1f, 0f, precisionActive: false, ExactTick);
        var (_, dy) = processor.Update(0f, 1f, precisionActive: false, ExactTick);

        Assert.Equal(5, dx);
        Assert.Equal(20, dy);
    }

    [Fact]
    public void Zero_Scale_Disables_That_Axis_Only()
    {
        var settings = Raw(640f) with { ScaleX = 0f, ScaleY = 2f };
        var processor = new MouseStickProcessor(settings);

        var (dx, _) = processor.Update(1f, 0f, precisionActive: false, ExactTick);
        var (_, dy) = processor.Update(0f, 1f, precisionActive: false, ExactTick);

        Assert.Equal(0, dx);
        Assert.Equal(20, dy);
    }

    [Fact]
    public void NaN_Deflection_Is_Treated_As_Center()
    {
        var processor = new MouseStickProcessor(Raw(1000f));

        var (dx, dy) = processor.Update(float.NaN, float.NaN, precisionActive: false, 0.01f);

        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
    }

    [Fact]
    public void Zero_Delta_Does_Not_Move()
    {
        var processor = new MouseStickProcessor(Raw(1000f));

        var (dx, dy) = processor.Update(1f, 1f, precisionActive: false, 0f);

        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
    }

    [Fact]
    public void Released_Stick_Reaches_Exact_Zero_In_Finite_Ticks()
    {
        var processor = new MouseStickProcessor(Raw(1000f) with { ResponseTime = 0.08f });

        for (var i = 0; i < 20; i++)
        {
            processor.Update(1f, 0f, precisionActive: false, 0.016f);
        }

        for (var i = 0; i < 300; i++)
        {
            processor.Update(0f, 0f, precisionActive: false, 0.016f);
        }

        for (var i = 0; i < 10; i++)
        {
            var (dx, dy) = processor.Update(0f, 0f, precisionActive: false, 0.016f);

            Assert.Equal(0, dx);
            Assert.Equal(0, dy);
        }

        Assert.Equal(0, processor.CurrentSpeed);
        Assert.Equal(0, processor.CurrentSpeedX);
        Assert.Equal(0, processor.CurrentSpeedY);
    }

    [Fact]
    public void Sub_Pixel_Residual_Clears_Once_At_Rest()
    {
        var processor = new MouseStickProcessor(Raw(512f));

        var first = processor.Update(1f, 0f, precisionActive: false, 1f / 1024f);
        Assert.Equal(0, first.Dx);
        Assert.Equal(512, processor.CurrentSpeed);

        for (var i = 0; i < 300; i++)
        {
            processor.Update(0f, 0f, precisionActive: false, 1f / 1024f);
        }

        var after = processor.Update(0f, 0f, precisionActive: false, 1f / 1024f);

        Assert.Equal(0, after.Dx);
        Assert.Equal(0, after.Dy);
        Assert.Equal(0, processor.CurrentSpeed);
    }

    [Fact]
    public void Snap_To_Zero_Does_Not_Affect_Non_Zero_Targets()
    {
        var processor = new MouseStickProcessor(Raw(1000f) with { ResponseTime = 0.08f });

        for (var i = 0; i < 50; i++)
        {
            processor.Update(1f, 0f, precisionActive: false, 0.016f);
        }

        Assert.True(processor.CurrentSpeed > 900, $"expected sustained speed, got {processor.CurrentSpeed}");
    }

    [Fact]
    public void UpdateSettings_Keeps_Smoothed_Speed_And_Sub_Pixel_Residual()
    {
        // 1536 px/s at dt = 1/2048 s is exactly 0.75 px per tick, so three ticks leave the
        // smoothed speed at max with a known 0.25 px still pending in the residual.
        var processor = new MouseStickProcessor(Raw(1536f));

        for (var i = 0; i < 3; i++)
        {
            processor.Update(1f, 0f, precisionActive: false, 1f / 2048f);
        }

        Assert.Equal(1536, processor.CurrentSpeed);

        // A different MaxSpeed, exactly what dragging the sensitivity slider produces.
        processor.UpdateSettings(Raw(1792f));

        // The swap must not disturb the smoothed velocity...
        Assert.Equal(1536, processor.CurrentSpeed);

        // ...nor the residual: the carried 0.25 px plus 1792/2048 = 0.875 px crosses the
        // whole-pixel boundary, so one pixel is emitted. A dropped residual would emit none.
        var (dx, dy) = processor.Update(1f, 0f, precisionActive: false, 1f / 2048f);

        Assert.Equal(1, dx);
        Assert.Equal(0, dy);

        // The new MaxSpeed is live from here on.
        Assert.Equal(1792, processor.CurrentSpeed);
    }
}
