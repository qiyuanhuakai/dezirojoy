using System.Text.Json;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Mapping;

/// <summary>
/// Profile JSON for the complex-shortcut target: the polymorphic "$type" discriminator and
/// the hand-editable script text must survive a round trip byte-for-byte, and a hand-written
/// profile must load the same way the editor would write it.
/// </summary>
public class ProfileSerializerTests
{
    private static GamepadProfile ComplexProfile(string script) => new()
    {
        Name = "alt-tab",
        Mappings =
        [
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.LeftBumper)
                {
                    Gesture = new ButtonGesture { LongPressThreshold = 0.5f },
                },
                Target = new ComplexShortcutTarget { Script = script },
            },
        ],
    };

    [Fact]
    public void Complex_Shortcut_Roundtrips_With_Its_Discriminator_And_Script()
    {
        var profile = ComplexProfile("hold Alt\nevery 1s tap Tab");
        var json = ProfileSerializer.Serialize(profile);

        Assert.Contains("\"$type\": \"complexShortcut\"", json);
        Assert.Contains("hold Alt", json);

        var back = ProfileSerializer.Deserialize(json);
        var target = Assert.IsType<ComplexShortcutTarget>(back.Mappings[0].Target);

        Assert.Equal("hold Alt\nevery 1s tap Tab", target.Script);
        Assert.Equal(json, ProfileSerializer.Serialize(back));
    }

    [Fact]
    public void Complex_Shortcut_Script_Survives_Quotes_Comments_And_Unicode()
    {
        const string script = "tap [ # 括号注释\ntap '";
        var profile = ComplexProfile(script);

        var back = ProfileSerializer.Deserialize(ProfileSerializer.Serialize(profile));

        Assert.Equal(script, Assert.IsType<ComplexShortcutTarget>(back.Mappings[0].Target).Script);
    }

    [Fact]
    public void Complex_Shortcut_Loads_From_A_Hand_Written_Profile()
    {
        const string json = """
        {
          "SchemaVersion": 2,
          "Name": "hand-written",
          "Mappings": [
            {
              "Source": {
                "$type": "button",
                "Button": "LeftBumper",
                "Gesture": { "LongPressThreshold": 0.5 }
              },
              "Target": {
                "$type": "complexShortcut",
                "Script": "hold Alt\nevery 1s tap Tab"
              },
              "Enabled": true
            }
          ]
        }
        """;

        var back = ProfileSerializer.Deserialize(json);
        var target = Assert.IsType<ComplexShortcutTarget>(back.Mappings[0].Target);

        Assert.Equal("hold Alt\nevery 1s tap Tab", target.Script);
    }

    [Fact]
    public void A_Complex_Shortcut_Without_A_Script_Is_Rejected()
    {
        const string json = """
        {
          "SchemaVersion": 2,
          "Name": "broken",
          "Mappings": [
            {
              "Source": { "$type": "button", "Button": "LeftBumper" },
              "Target": { "$type": "complexShortcut" },
              "Enabled": true
            }
          ]
        }
        """;

        Assert.Throws<JsonException>(() => ProfileSerializer.Deserialize(json));
    }
}
