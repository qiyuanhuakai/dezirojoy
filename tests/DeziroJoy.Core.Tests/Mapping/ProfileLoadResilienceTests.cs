using System.Text.Json;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Mapping;

/// <summary>
/// A profile this build cannot fully understand still has to load. A target written by a
/// newer build carries a "$type" discriminator an older one does not know - the shape that
/// produced "Read unrecognized type discriminator id specialKey" - and refusing the whole
/// profile over it left the mapping undeletable and the table empty. The unknown target is
/// therefore kept verbatim: it displays, it deletes, it re-saves, and the engine ignores it.
/// </summary>
public class ProfileLoadResilienceTests
{
    private const string FutureTargetJson = """
    {
      "Name": "future",
      "Mappings": [
        {
          "Source": { "$type": "button", "Button": "RightBumper" },
          "Target": { "$type": "hapticPattern", "Intensity": 3, "Curve": "Pulse" }
        }
      ]
    }
    """;

    private static GamepadProfile ProfileWith(MappingTarget target) => new()
    {
        Name = "resilience",
        Mappings = [new MappingEntry { Source = new ButtonBinding(GamepadButton.RightBumper), Target = target }],
    };

    [Fact]
    public void An_Unknown_Target_Discriminator_Loads_Instead_Of_Throwing()
    {
        var back = ProfileSerializer.Deserialize(FutureTargetJson);
        var target = Assert.IsType<UnknownTarget>(back.Mappings[0].Target);

        Assert.Equal("hapticPattern", target.Discriminator);
        Assert.Contains("\"Intensity\": 3", target.RawJson);
    }

    [Fact]
    public void An_Unknown_Target_Keeps_Its_Source_And_Identity()
    {
        var back = ProfileSerializer.Deserialize(FutureTargetJson);
        var entry = Assert.Single(back.Mappings);

        Assert.IsType<ButtonBinding>(entry.Source);
        Assert.NotEqual(Guid.Empty, entry.Id);
    }

    [Fact]
    public void An_Unknown_Target_Survives_A_Save_Byte_For_Byte()
    {
        var profile = ProfileSerializer.Deserialize(FutureTargetJson);
        var saved = ProfileSerializer.Serialize(profile);

        Assert.Contains("\"$type\": \"hapticPattern\"", saved);
        Assert.Contains("\"Intensity\": 3", saved);

        var back = ProfileSerializer.Deserialize(saved);

        Assert.Equal(profile.Mappings, back.Mappings);
        Assert.Equal(saved, ProfileSerializer.Serialize(back));
    }

    [Fact]
    public void An_Unknown_Target_Built_By_Hand_Roundtrips()
    {
        const string raw = """{ "$type": "hapticPattern", "Intensity": 3 }""";
        var target = new UnknownTarget("hapticPattern", raw);

        var back = ProfileSerializer.Deserialize(ProfileSerializer.Serialize(ProfileWith(target)));

        Assert.Equal(target, Assert.IsType<UnknownTarget>(back.Mappings[0].Target));
    }

    [Fact]
    public void Every_Known_Discriminator_Still_Roundtrips_Beside_The_Unknown_One()
    {
        var profile = new GamepadProfile
        {
            Name = "mixed",
            Mappings =
            [
                new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = new KeyPressTarget(GamepadKey.Enter) },
                new MappingEntry { Source = new ButtonBinding(GamepadButton.B), Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C) },
                new MappingEntry { Source = new ButtonBinding(GamepadButton.X), Target = new MouseButtonTarget(MouseButton.Right) },
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right),
                    Target = new MouseMoveTarget(new MouseStickSettings { MaxSpeed = 2000f, DeadZone = 0.2f, InvertY = true }),
                },
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Left),
                    Target = new WheelScrollTarget(new ScrollStickSettings { MaxTicksPerSecond = 15f, EnableHorizontal = false }),
                },
                new MappingEntry { Source = new ButtonBinding(GamepadButton.Y), Target = new SpecialKeyTarget(SpecialKeyKind.Layer, LayerId: 2) },
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.LeftBumper)
                    {
                        Gesture = new ButtonGesture { LongPressThreshold = 0.5f },
                    },
                    Target = new ComplexShortcutTarget { Script = "hold Alt\nevery 1s tap Tab" },
                },
                new MappingEntry { Source = new ButtonBinding(GamepadButton.DPadUp), Target = new NoneTarget() },
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.RightBumper),
                    Target = new UnknownTarget("hapticPattern", """{ "$type": "hapticPattern", "Intensity": 3 }"""),
                },
            ],
        };

        var back = ProfileSerializer.Deserialize(ProfileSerializer.Serialize(profile));

        Assert.Equal(profile.Mappings, back.Mappings);
    }

    [Fact]
    public void A_Profile_With_One_Unreadable_Mapping_Loads_The_Rest()
    {
        const string json = """
        {
          "Name": "half-broken",
          "Mappings": [
            { "Source": { "$type": "button", "Button": "A" }, "Target": { "$type": "key", "Key": "Enter" } },
            { "Source": { "$type": "button", "Button": "NotAButton" }, "Target": { "$type": "key", "Key": "Escape" } },
            { "Source": { "$type": "button", "Button": "B" }, "Target": { "$type": "key", "Key": "Space" } }
          ]
        }
        """;

        Assert.Throws<JsonException>(() => ProfileSerializer.Deserialize(json));

        var load = ProfileSerializer.Load(json);

        Assert.True(load.HasProblems);
        Assert.Single(load.Problems);
        Assert.Equal("half-broken", load.Profile.Name);
        Assert.Equal(2, load.Profile.Mappings.Count);
        Assert.Equal(GamepadKey.Enter, Assert.IsType<KeyPressTarget>(load.Profile.Mappings[0].Target).Key);
        Assert.Equal(GamepadKey.Space, Assert.IsType<KeyPressTarget>(load.Profile.Mappings[1].Target).Key);
    }

    [Fact]
    public void A_Profile_Whose_Header_Is_Damaged_Keeps_The_Mappings_It_Could_Read()
    {
        const string json = """
        {
          "Name": "damaged-header",
          "PresetSlot": "one",
          "Mappings": [
            { "Source": { "$type": "button", "Button": "A" }, "Target": { "$type": "key", "Key": "Enter" } }
          ]
        }
        """;

        Assert.Throws<JsonException>(() => ProfileSerializer.Deserialize(json));

        var load = ProfileSerializer.Load(json);

        Assert.True(load.HasProblems);
        Assert.Equal("damaged-header", load.Profile.Name);
        Assert.Equal(1, load.Profile.PresetSlot);
        var target = Assert.IsType<KeyPressTarget>(Assert.Single(load.Profile.Mappings).Target);
        Assert.Equal(GamepadKey.Enter, target.Key);
    }

    [Fact]
    public void A_Body_That_Is_Not_Json_At_All_Loads_As_An_Empty_Profile_With_A_Reason()
    {
        const string json = """
        { "Name": "truncated", "Mappings": [ { "Source":
        """;

        var load = ProfileSerializer.Load(json);

        Assert.True(load.HasProblems);
        Assert.Empty(load.Profile.Mappings);
    }

    [Fact]
    public void A_Target_That_Is_Not_An_Object_Is_Damage_Rather_Than_An_Unknown_Type()
    {
        const string json = """
        {
          "Name": "damaged",
          "Mappings": [
            { "Source": { "$type": "button", "Button": "A" }, "Target": 7 },
            { "Source": { "$type": "button", "Button": "B" }, "Target": { "$type": "key", "Key": "Enter" } }
          ]
        }
        """;

        Assert.Throws<JsonException>(() => ProfileSerializer.Deserialize(json));

        var load = ProfileSerializer.Load(json);

        Assert.Single(load.Problems);
        Assert.Equal(GamepadKey.Enter, Assert.IsType<KeyPressTarget>(Assert.Single(load.Profile.Mappings).Target).Key);
    }

    [Fact]
    public void A_Profile_That_Loads_Cleanly_Reports_No_Problems()
    {
        var load = ProfileSerializer.Load(ProfileSerializer.Serialize(ProfileWith(new KeyPressTarget(GamepadKey.A))));

        Assert.False(load.HasProblems);
        Assert.Empty(load.Problems);
        Assert.Equal("resilience", load.Profile.Name);
    }
}
