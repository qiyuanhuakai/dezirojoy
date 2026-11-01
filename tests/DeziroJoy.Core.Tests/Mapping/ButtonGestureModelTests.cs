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
    public void Gesture_Actions_Roundtrip_Through_Json()
    {
        var profile = new GamepadProfile
        {
            Name = "gesture-actions",
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
                            DoubleClickAction = new KeyPressTarget(GamepadKey.Space),
                            LongPressAction = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.W),
                        },
                    },
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
            ],
        };

        var json = ProfileSerializer.Serialize(profile);

        Assert.Contains("\"DoubleClickAction\"", json);
        Assert.Contains("\"$type\": \"key\"", json);
        Assert.Contains("\"LongPressAction\"", json);

        var back = ProfileSerializer.Deserialize(json);
        var gesture = Assert.IsType<ButtonGesture>(Assert.IsType<ButtonBinding>(back.Mappings[0].Source).Gesture);

        Assert.Equal(new KeyPressTarget(GamepadKey.Space), gesture.DoubleClickAction);
        Assert.Equal(new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.W), gesture.LongPressAction);
        Assert.Equal(json, ProfileSerializer.Serialize(back));
    }

    [Fact]
    public void Gestures_Without_Actions_Stay_Free_Of_The_Action_Fields()
    {
        var profile = new GamepadProfile
        {
            Name = "timing-only",
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B)
                    {
                        Gesture = new ButtonGesture { DoubleClickWindow = 0.3f },
                    },
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
            ],
        };

        var json = ProfileSerializer.Serialize(profile);

        Assert.Contains("\"Gesture\"", json);
        Assert.DoesNotContain("DoubleClickAction", json);
        Assert.DoesNotContain("LongPressAction", json);
    }

    [Fact]
    public void Old_Profiles_Without_Gesture_Actions_Load_With_Null_Actions()
    {
        const string json = """
        {
          "SchemaVersion": 1,
          "Name": "old-gesture",
          "Layout": "Generic",
          "Mappings": [
            {
              "Id": "00000000-0000-0000-0000-000000000001",
              "Source": {
                "$type": "button",
                "Button": "B",
                "Gesture": {
                  "DoubleClickWindow": 0.25,
                  "LongPressThreshold": 0.4
                }
              },
              "Target": { "$type": "key", "Key": "Enter" },
              "Enabled": true
            }
          ]
        }
        """;

        var back = ProfileSerializer.Deserialize(json);
        var gesture = Assert.IsType<ButtonGesture>(Assert.IsType<ButtonBinding>(back.Mappings[0].Source).Gesture);

        Assert.Null(gesture.DoubleClickAction);
        Assert.Null(gesture.LongPressAction);

        // With no action configured the gesture keeps re-firing the mapping's own target,
        // exactly as it did before the action fields existed.
        var engine = new MappingEngine(back);

        Assert.Empty(engine.Tick(GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0), 0.05f));
        Assert.Empty(engine.Tick(GamepadState.Neutral, 0.05f));

        var second = engine.Tick(GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0), 0.05f);
        Assert.Equal(2, second.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), second[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None), second[1]);
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

    [Fact]
    public void Fixed_Default_Timings_Are_Pinned()
    {
        // The user chose fixed timings over adaptive/learned ones. The fields keep their 0
        // defaults for profile compatibility; these constants are the fixed values the UI
        // applies when it enables a gesture.
        Assert.Equal(0.25f, ButtonGesture.DefaultDoubleClickWindow);
        Assert.Equal(0.50f, ButtonGesture.DefaultLongPressThreshold);
        Assert.Equal(0.10f, ButtonGesture.DefaultClickRepeatInterval);
    }

    [Fact]
    public void HasClickRepeat_Reflects_The_Interval()
    {
        Assert.False(new ButtonGesture().HasClickRepeat);
        Assert.True(new ButtonGesture { ClickRepeatInterval = 0.1f }.HasClickRepeat);
    }

    [Fact]
    public void HasEffectiveLongPress_Is_Inert_While_Click_Repeat_Is_On()
    {
        // Long-press alone is effective.
        Assert.True(new ButtonGesture { LongPressThreshold = 0.5f }.HasEffectiveLongPress);

        // Click auto-repeat wins: the long-press becomes inert.
        Assert.False(new ButtonGesture { LongPressThreshold = 0.5f, ClickRepeatInterval = 0.1f }.HasEffectiveLongPress);

        // No long-press configured at all.
        Assert.False(new ButtonGesture { ClickRepeatInterval = 0.1f }.HasEffectiveLongPress);
    }

    [Fact]
    public void HasLongPressFunction_Is_False_For_NoneTarget()
    {
        Assert.False(new ButtonGesture().HasLongPressFunction);
        Assert.False(new ButtonGesture { LongPressAction = new NoneTarget() }.HasLongPressFunction);
        Assert.True(new ButtonGesture { LongPressAction = new KeyPressTarget(GamepadKey.Space) }.HasLongPressFunction);
    }

    [Fact]
    public void Click_Repeat_Roundtrips_Through_Json()
    {
        var profile = new GamepadProfile
        {
            Name = "click-repeat",
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B)
                    {
                        Gesture = new ButtonGesture { ClickRepeatInterval = 0.1f },
                    },
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
            ],
        };

        var json = ProfileSerializer.Serialize(profile);
        Assert.Contains("\"ClickRepeatInterval\": 0.1", json);

        var back = ProfileSerializer.Deserialize(json);
        var gesture = Assert.IsType<ButtonGesture>(Assert.IsType<ButtonBinding>(back.Mappings[0].Source).Gesture);
        Assert.Equal(0.1f, gesture.ClickRepeatInterval);
        Assert.Equal(json, ProfileSerializer.Serialize(back));
    }

    [Fact]
    public void Old_Shaped_Gesture_Profile_Roundtrips_Unchanged_Without_Click_Repeat()
    {
        // A profile written before ClickRepeatInterval existed must round-trip byte-for-byte
        // and must not grow the new field.
        var profile = new GamepadProfile
        {
            Name = "old-gesture",
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B)
                    {
                        Gesture = new ButtonGesture
                        {
                            DoubleClickWindow = 0.25f,
                            LongPressThreshold = 0.4f,
                            LongPressRepeatInterval = 0.1f,
                        },
                    },
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
            ],
        };

        var json = ProfileSerializer.Serialize(profile);
        Assert.DoesNotContain("ClickRepeatInterval", json);

        var back = ProfileSerializer.Deserialize(json);
        var gesture = Assert.IsType<ButtonGesture>(Assert.IsType<ButtonBinding>(back.Mappings[0].Source).Gesture);
        Assert.Equal(0f, gesture.ClickRepeatInterval);
        Assert.Equal(json, ProfileSerializer.Serialize(back));
    }
}
