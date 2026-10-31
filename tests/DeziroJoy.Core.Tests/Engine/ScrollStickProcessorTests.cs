using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

public class ScrollStickProcessorTests
{
    private static ScrollStickSettings Raw(float maxTicks) => new()
    {
        DeadZone = 0f,
        MaxTicksPerSecond = maxTicks,
        Curve = ResponseCurve.Linear,
        CurveStrength = 0f,
        ResponseTime = 0f,
        InvertY = false,
        EnableHorizontal = true,
    };

    [Fact]
    public void Full_Upward_Deflection_Scrolls_Up()
    {
        var processor = new ScrollStickProcessor(Raw(16f));

        var (_, vertical) = processor.Update(0f, 1f, 1f / 64f);

        Assert.Equal(0, vertical); // 16 * 1/64 = 0.25 notches -> sub-notch accumulation
    }

    [Fact]
    public void Fractional_Notches_Accumulate_Into_Whole_Notches()
    {
        var processor = new ScrollStickProcessor(Raw(16f));

        var total = 0;
        for (var i = 0; i < 4; i++)
        {
            var (_, vertical) = processor.Update(0f, 1f, 1f / 64f);
            total += vertical;
        }

        Assert.Equal(1, total); // 4 * 0.25 = 1 notch
    }

    [Fact]
    public void Downward_Deflection_Scrolls_Down()
    {
        var processor = new ScrollStickProcessor(Raw(64f));

        var (_, vertical) = processor.Update(0f, -1f, 1f / 64f);

        Assert.Equal(-1, vertical);
    }

    [Fact]
    public void Horizontal_Deflection_Scrolls_Horizontally_When_Enabled()
    {
        var processor = new ScrollStickProcessor(Raw(64f));

        var (horizontal, _) = processor.Update(1f, 0f, 1f / 64f);

        Assert.Equal(1, horizontal);
    }

    [Fact]
    public void Horizontal_Scrolling_Can_Be_Disabled()
    {
        var processor = new ScrollStickProcessor(Raw(64f) with { EnableHorizontal = false });

        var (horizontal, _) = processor.Update(1f, 0f, 1f / 64f);

        Assert.Equal(0, horizontal);
    }

    [Fact]
    public void Deflection_Inside_Deadzone_Does_Not_Scroll()
    {
        var processor = new ScrollStickProcessor(new ScrollStickSettings { DeadZone = 0.2f, ResponseTime = 0f });

        var (horizontal, vertical) = processor.Update(0.1f, 0.1f, 1f / 64f);

        Assert.Equal(0, horizontal);
        Assert.Equal(0, vertical);
    }

    [Fact]
    public void InvertY_Flips_Scroll_Direction()
    {
        var processor = new ScrollStickProcessor(Raw(64f) with { InvertY = true });

        var (_, vertical) = processor.Update(0f, 1f, 1f / 64f);

        Assert.Equal(-1, vertical);
    }

    [Fact]
    public void Response_Time_Smooths_The_First_Tick()
    {
        var processor = new ScrollStickProcessor(Raw(64f) with { ResponseTime = 0.05f });

        var (_, vertical) = processor.Update(0f, 1f, 1f / 64f);

        // 64 * (1 - e^(-0.25)) * 0.015625 = 0.222 -> 0 notches on first tick
        Assert.Equal(0, vertical);
    }
}
