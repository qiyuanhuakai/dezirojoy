using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Mapping;

/// <summary>
/// Model and serialization contract for the gesture settings and the source directions.
/// Backward compatibility is the point: a profile written before these fields existed must
/// load unchanged and behave identically, and a profile that does not use them must not grow
/// the fields either.
/// </summary>
public class ButtonGestureModelTests
{
    [Fact]
    public void Button_Gesture_Defaults_To_Nothing_Enabled()
    {
        var gesture = new ButtonGesture();

        Assert.False(gesture.HasDoubleClick);
        Assert.False(gesture.HasLongPress);
        Assert.False(gesture.HasAnyGesture);
    }

    [Fact]
    public void Gesture_Settings_Roundtrip_Through_Json()
    {
        var profile = new GamepadProfile
        {
            Name = "gestures",
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B)
                    {
                        Gesture = new ButtonGesture
                        {
                            DoubleClickWindow = 0.3f,
                            LongPressThreshold = 0.5f,
                            LongPressRepeatInterval = 0.1f,
                        },
                    },
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
            ],
        };

        var json = ProfileSerializer.Serialize(profile);

        Assert.Contains("\"Gesture\"", json);
        Assert.Contains("\"DoubleClickWindow\": 0.3", json);

        var back = ProfileSerializer.Deserialize(json);
        var gesture = Assert.IsType<ButtonGesture>(Assert.IsType<ButtonBinding>(back.Mappings[0].Source).Gesture);

        Assert.Equal(0.3f, gesture.DoubleClickWindow);
        Assert.Equal(0.5f, gesture.LongPressThreshold);
        Assert.Equal(0.1f, gesture.LongPressRepeatInterval);
        Assert.Equal(json, ProfileSerializer.Serialize(back));
    }

    [Fact]
    public void Profiles_Without_Gestures_Stay_Free_Of_The_Field()
    {
        var profile = new GamepadProfile
        {
            Name = "plain",
            Mappings = [new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = new KeyPressTarget(GamepadKey.Escape) }],
        };

        Assert.DoesNotContain("Gesture", ProfileSerializer.Serialize(profile));
    }

    [Fact]
    public void Old_Profiles_Without_A_Gesture_Field_Load_With_No_Gesture_And_Zero_Latency()
    {
        const string json = """
        {
          "SchemaVersion": 1,
          "Name": "old",
          "Layout": "Generic",
          "Mappings": [
            {
              "Id": "00000000-0000-0000-0000-000000000001",
              "Source": { "$type": "button", "Button": "A" },
              "Target": { "$type": "key", "Key": "Escape" },
              "Enabled": true
            }
          ]
        }
        """;

        var back = ProfileSerializer.Deserialize(json);
        var binding = Assert.IsType<ButtonBinding>(back.Mappings[0].Source);

        Assert.Null(binding.Gesture);
        Assert.Null(binding.Direction);

        var engine = new MappingEngine(back);
        var press = engine.Tick(GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0), 0.016f);

        Assert.Equal(new KeyDownCommand(GamepadKey.Escape, KeyModifiers.None), Assert.Single(press));
    }

    [Fact]
    public void Stick_Direction_Roundtrips_Through_Json()
    {
        var profile = new GamepadProfile
        {
            Name = "stick-keys",
            Mappings =
            [
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Left) { Direction = InputDirection.Up },
                    Target = new KeyPressTarget(GamepadKey.W),
                },
            ],
        };

        var json = ProfileSerializer.Serialize(profile);

        Assert.Contains("\"Direction\": \"Up\"", json);

        var back = ProfileSerializer.Deserialize(json);
        var binding = Assert.IsType<StickBinding>(back.Mappings[0].Source);

        Assert.Equal(InputDirection.Up, binding.Direction);
        Assert.Equal(json, ProfileSerializer.Serialize(back));
    }

    [Fact]
    public void Stick_Bindings_Without_A_Direction_Stay_Free_Of_The_Field()
    {
        var profile = new GamepadProfile
        {
            Name = "plain-stick",
            Mappings =
            [
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right),
                    Target = new MouseMoveTarget(new MouseStickSettings()),
                },
            ],
        };

        Assert.DoesNotContain("Direction", ProfileSerializer.Serialize(profile));
    }

    [Fact]
    public void Button_Direction_Roundtrips_Through_Json()
    {
        var profile = new GamepadProfile
        {
            Name = "button-mouse",
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.A) { Direction = InputDirection.Right },
                    Target = new MouseMoveTarget(new MouseStickSettings()),
                },
            ],
        };

        var json = ProfileSerializer.Serialize(profile);
        var back = ProfileSerializer.Deserialize(json);

        Assert.Equal(InputDirection.Right, Assert.IsType<ButtonBinding>(back.Mappings[0].Source).Direction);
        Assert.Equal(json, ProfileSerializer.Serialize(back));
    }
}
