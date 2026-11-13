using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

/// <summary>
/// A target this build does not know is carried by the profile so the mapping still shows,
/// deletes and re-saves - but the engine has nothing to emit for it. It is skipped rather
/// than rejected, so one unknown target costs the user one mapping and not the whole
/// profile.
/// </summary>
public class UnknownTargetEngineTests
{
    private static UnknownTarget Unknown() => new(
        "hapticPattern",
        """{ "$type": "hapticPattern", "Intensity": 3 }""");

    [Fact]
    public void An_Unknown_Target_Is_Built_Without_Rejection()
    {
        var engine = new MappingEngine(new GamepadProfile
        {
            Name = "future",
            Mappings = [new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = Unknown() }],
        });

        Assert.Single(engine.EffectiveMappings);
        Assert.IsType<UnknownTarget>(engine.EffectiveMappings[0].Target);
    }

    [Fact]
    public void An_Unknown_Target_Emits_Nothing_While_Its_Source_Is_Held()
    {
        var engine = new MappingEngine(new GamepadProfile
        {
            Name = "future",
            Mappings =
            [
                new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = Unknown() },
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B) { Direction = InputDirection.Up },
                    Target = Unknown(),
                },
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right) { Direction = InputDirection.Up },
                    Target = Unknown(),
                },
            ],
        });

        var pressed = engine.Tick(GamepadState.Create(GamepadButtonMask.A, 1f, 0f, 1f, 1f), 1f / 64f);
        var held = engine.Tick(GamepadState.Create(GamepadButtonMask.A, 1f, 0f, 1f, 1f), 1f / 64f);
        var released = engine.Tick(GamepadState.Neutral, 1f / 64f);

        Assert.Empty(pressed);
        Assert.Empty(held);
        Assert.Empty(released);
    }
}
