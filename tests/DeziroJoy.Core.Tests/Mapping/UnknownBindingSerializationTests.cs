using System.Text.Json;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Mapping;

/// <summary>
/// The source-side twin of the unknown-target tolerance. A profile a newer build wrote names
/// a <c>$type</c> discriminator no source type in this build claims; refusing it took the
/// whole profile down, so the mapping behind it could neither be shown nor deleted. The
/// discriminator and the body are kept verbatim instead.
/// </summary>
public class UnknownBindingSerializationTests
{
    private const string UnknownSourceJson = """
    {
      "SchemaVersion": 2,
      "Name": "from-a-newer-build",
      "Mappings": [
        {
          "Source": { "$type": "wand", "Wave": 3 },
          "Target": { "$type": "key", "Key": "F4" },
          "Enabled": true
        }
      ]
    }
    """;

    [Fact]
    public void An_Unknown_Source_Discriminator_Loads_As_An_Unknown_Binding()
    {
        var profile = ProfileSerializer.Deserialize(UnknownSourceJson);
        var source = Assert.IsType<UnknownBinding>(Assert.Single(profile.Mappings).Source);

        Assert.Equal("wand", source.Discriminator);
        Assert.Contains("\"Wave\": 3", source.RawJson);
    }

    [Fact]
    public void An_Unknown_Source_Is_Written_Back_Byte_For_Byte()
    {
        var profile = ProfileSerializer.Deserialize(UnknownSourceJson);

        var json = ProfileSerializer.Serialize(profile);

        Assert.Contains("\"$type\": \"wand\"", json);
        Assert.Contains("\"Wave\": 3", json);
    }

    [Fact]
    public void The_Mapping_Behind_An_Unknown_Source_Still_Shows_Its_Target()
    {
        var profile = ProfileSerializer.Deserialize(UnknownSourceJson);

        // The target is untouched, so the row is readable and deletable rather than blank.
        Assert.Equal(new KeyPressTarget(GamepadKey.F4), Assert.Single(profile.Mappings).Target);
    }

    [Fact]
    public void An_Unknown_Source_Reads_As_An_Unsupported_Input()
    {
        Assert.Equal("不支持的输入", new UnknownBinding(string.Empty, "{}").ToString());
        Assert.Equal("不支持的输入（wand）", new UnknownBinding("wand", "{}").ToString());
    }

    [Fact]
    public void A_Source_Without_A_Discriminator_Is_Preserved_Rather_Than_Rejected()
    {
        const string json = """
        {
          "SchemaVersion": 2,
          "Name": "no-discriminator",
          "Mappings": [
            {
              "Source": { "Button": "A" },
              "Target": { "$type": "key", "Key": "F4" },
              "Enabled": true
            }
          ]
        }
        """;

        var profile = ProfileSerializer.Deserialize(json);

        Assert.IsType<UnknownBinding>(Assert.Single(profile.Mappings).Source);
    }

    [Fact]
    public void A_Known_Source_Still_Round_Trips_Exactly_As_It_Did()
    {
        var profile = new GamepadProfile
        {
            Name = "round-trip",
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.LeftBumper)
                    {
                        Gesture = new ButtonGesture { LongPressThreshold = 0.5f },
                    },
                    Target = new KeyPressTarget(GamepadKey.F4),
                },
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Left) { Direction = InputDirection.Up },
                    Target = new KeyPressTarget(GamepadKey.Space),
                },
            ],
        };

        var json = ProfileSerializer.Serialize(profile);
        var back = ProfileSerializer.Deserialize(json);

        Assert.Equal(profile.Mappings, back.Mappings);
        Assert.Contains("\"$type\": \"button\"", json);
        Assert.Contains("\"$type\": \"stick\"", json);

        // The default-omitting attributes still hold: a plain button writes no Code, and the
        // stick's own Direction is the one the mapping carries.
        Assert.DoesNotContain("\"Code\"", json);
        Assert.Contains("\"Direction\": \"Up\"", json);
    }

    [Fact]
    public void A_Damaged_Source_Body_Is_Still_A_Hard_Failure()
    {
        // An object that is not a source at all is damage, not a newer build's type.
        const string json = """
        {
          "SchemaVersion": 2,
          "Name": "damaged",
          "Mappings": [
            {
              "Source": 42,
              "Target": { "$type": "key", "Key": "F4" },
              "Enabled": true
            }
          ]
        }
        """;

        Assert.Throws<JsonException>(() => ProfileSerializer.Deserialize(json));
    }
}
