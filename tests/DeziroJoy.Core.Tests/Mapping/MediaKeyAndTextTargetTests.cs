using System.Text.Json;
using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Mapping;

/// <summary>
/// The media / system key vocabulary and the text target. Media keys are ordinary keys as
/// far as the engine is concerned - press down, release up - so what needs pinning is that
/// they exist in the portable vocabulary at all and round-trip through a profile; the text
/// target is the one target that is a tap by nature and emits a
/// <see cref="TextCommand"/> instead of a key pair.
/// </summary>
public class MediaKeyAndTextTargetTests
{
    private const float Tick = 1f / 64f;

    private static GamepadProfile Profile(params MappingEntry[] mappings) => new()
    {
        Name = "media-text-profile",
        Mappings = mappings,
    };

    [Fact]
    public void A_Text_Target_Types_On_The_Press_Edge_And_Emits_Nothing_On_Release()
    {
        var engine = new MappingEngine(Profile(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.A),
            Target = new TextTarget("hello 世界"),
        }));

        var press = engine.Tick(GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0), Tick);
        Assert.Equal(new TextCommand("hello 世界"), Assert.Single(press));

        // Text is not a hold: releasing it has nothing to end, and holding it repeats
        // nothing, so a second held tick is silent.
        Assert.Empty(engine.Tick(GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0), Tick));
        Assert.Empty(engine.Tick(GamepadState.Neutral, Tick));
    }

    [Fact]
    public void A_Text_Target_Round_Trips_Through_The_Profile_With_Its_Own_Discriminator()
    {
        var profile = Profile(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.A),
            Target = new TextTarget("search term"),
        });

        var json = ProfileSerializer.Serialize(profile);

        // The discriminator is the target's own, so a profile written by this build reads
        // back through the same path it always did.
        Assert.Contains("\"$type\": \"text\"", json);
        Assert.Contains("search term", json);

        var reloaded = ProfileSerializer.Deserialize(json);
        Assert.Equal(new TextTarget("search term"), Assert.Single(reloaded.Mappings).Target);
    }

    [Fact]
    public void A_Text_Target_Reads_As_输入文本_Rather_Than_As_The_Record_Dump()
    {
        Assert.Equal("输入文本", new TextTarget(string.Empty).ToString());
        Assert.Equal("输入文本「abc」", new TextTarget("abc").ToString());
    }

    [Fact]
    public void A_Directionless_Stick_Cannot_Drive_A_Text_Target()
    {
        var exception = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Left),
            Target = new TextTarget("x"),
        })));

        Assert.Contains("Direction", exception.Message);
    }

    [Fact]
    public void A_Stick_Deflection_Types_The_Text_Once_Each_Way()
    {
        var engine = new MappingEngine(Profile(new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Left) { Direction = InputDirection.Up },
            Target = new TextTarget("go"),
        }));

        var push = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 1, 0, 0), Tick);
        Assert.Equal(new TextCommand("go"), Assert.Single(push));

        Assert.Empty(engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 1, 0, 0), Tick));

        var release = engine.Tick(GamepadState.Neutral, Tick);
        Assert.Empty(release);
    }

    [Theory]
    [InlineData(GamepadKey.VolumeUp)]
    [InlineData(GamepadKey.VolumeDown)]
    [InlineData(GamepadKey.VolumeMute)]
    [InlineData(GamepadKey.MediaPlayPause)]
    [InlineData(GamepadKey.MediaStop)]
    [InlineData(GamepadKey.MediaPrevTrack)]
    [InlineData(GamepadKey.MediaNextTrack)]
    [InlineData(GamepadKey.BrowserBack)]
    [InlineData(GamepadKey.BrowserForward)]
    [InlineData(GamepadKey.BrowserRefresh)]
    [InlineData(GamepadKey.BrowserHome)]
    [InlineData(GamepadKey.LaunchMail)]
    [InlineData(GamepadKey.Sleep)]
    [InlineData(GamepadKey.PrintScreen)]
    public void A_Media_Key_Holds_Like_Any_Other_Key(GamepadKey key)
    {
        var engine = new MappingEngine(Profile(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.LeftBumper),
            Target = new KeyPressTarget(key),
        }));

        Assert.Equal(
            new KeyDownCommand(key, KeyModifiers.None),
            Assert.Single(engine.Tick(GamepadState.Create(GamepadButtonMask.LeftBumper, 0, 0, 0, 0), Tick)));
        Assert.Equal(
            new KeyUpCommand(key, KeyModifiers.None),
            Assert.Single(engine.Tick(GamepadState.Neutral, Tick)));
    }

    [Fact]
    public void A_Media_Key_Round_Trips_Through_The_Profile()
    {
        var profile = Profile(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.LeftBumper),
            Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.VolumeUp),
        });

        var reloaded = ProfileSerializer.Deserialize(ProfileSerializer.Serialize(profile));

        Assert.Equal(
            new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.VolumeUp),
            Assert.Single(reloaded.Mappings).Target);
    }

    [Fact]
    public void An_Old_Profile_Still_Loads_Unchanged_Beside_The_New_Keys()
    {
        // The new keys are appended to the enum, so a profile that carries the numeric
        // value of an existing key must still read back as that key.
        const string json = """
        {
          "SchemaVersion": 2,
          "Name": "old",
          "Mappings": [
            {
              "Source": { "$type": "button", "Button": "A" },
              "Target": { "$type": "key", "Key": "F4" }
            }
          ]
        }
        """;

        var reloaded = ProfileSerializer.Deserialize(json);

        Assert.Equal(new KeyPressTarget(GamepadKey.F4), Assert.Single(reloaded.Mappings).Target);
    }

    [Fact]
    public void A_Text_Target_Carries_No_Default_Length_Limit_Into_An_Old_Profile()
    {
        // The bound is the editor's, not the model's: a profile a newer build wrote with a
        // long string must still load rather than be refused by this one.
        var longText = new string('x', TextTarget.MaxLength * 4);
        var reloaded = ProfileSerializer.Deserialize(ProfileSerializer.Serialize(Profile(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.A),
            Target = new TextTarget(longText),
        })));

        Assert.Equal(new TextTarget(longText), Assert.Single(reloaded.Mappings).Target);
    }

    [Fact]
    public void Every_Media_Key_Is_A_Named_Enum_Value_The_Profile_Can_Carry()
    {
        // The profile writes enums as names, so every new member has to survive the string
        // round trip rather than falling back to a number.
        var json = ProfileSerializer.Serialize(Profile(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.A),
            Target = new KeyPressTarget(GamepadKey.MediaPlayPause),
        }));

        Assert.Contains("MediaPlayPause", json);
        Assert.DoesNotContain("\"Key\": 1", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            "MediaPlayPause",
            document.RootElement.GetProperty("Mappings")[0].GetProperty("Target").GetProperty("Key").GetString());
    }
}
