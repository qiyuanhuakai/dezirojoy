using System.Text.Json;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Mapping;

public class MappingModelTests
{
    private static GamepadProfile BuildFullProfile() => new()
    {
        Name = "Xbox Default",
        DeviceId = "dev-1",
        Layout = GamepadLayout.Xbox,
        Mappings =
        [
            new MappingEntry { Source = new ButtonBinding(GamepadButton.B), Target = new KeyPressTarget(GamepadKey.Enter) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = new KeyPressTarget(GamepadKey.Escape) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.X), Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.Y), Target = new KeyComboTarget(KeyModifiers.Ctrl | KeyModifiers.Shift, GamepadKey.V) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.DPadUp), Target = new KeyPressTarget(GamepadKey.ArrowUp) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.DPadDown), Target = new KeyPressTarget(GamepadKey.ArrowDown) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.DPadLeft), Target = new KeyPressTarget(GamepadKey.ArrowLeft) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.DPadRight), Target = new KeyPressTarget(GamepadKey.ArrowRight) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.RightBumper), Target = new MouseButtonTarget(MouseButton.Right) },
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
        ],
    };

    [Fact]
    public void Profile_Roundtrips_Through_Json_With_Stable_Canonical_Form()
    {
        var profile = BuildFullProfile();
        var json = ProfileSerializer.Serialize(profile);

        var back = ProfileSerializer.Deserialize(json);

        Assert.Equal(json, ProfileSerializer.Serialize(back));
    }

    [Fact]
    public void Deserialized_Profile_Preserves_Header_And_Mapping_Count()
    {
        var back = ProfileSerializer.Deserialize(ProfileSerializer.Serialize(BuildFullProfile()));

        Assert.Equal("Xbox Default", back.Name);
        Assert.Equal("dev-1", back.DeviceId);
        Assert.Equal(GamepadLayout.Xbox, back.Layout);
        Assert.Equal(11, back.Mappings.Count);
    }

    [Fact]
    public void Deserialized_Button_Mappings_Preserve_Targets()
    {
        var back = ProfileSerializer.Deserialize(ProfileSerializer.Serialize(BuildFullProfile()));

        var b = Assert.IsType<KeyPressTarget>(back.Mappings[0].Target);
        Assert.Equal(GamepadKey.Enter, b.Key);
        Assert.IsType<ButtonBinding>(back.Mappings[0].Source);

        var combo = Assert.IsType<KeyComboTarget>(back.Mappings[3].Target);
        Assert.Equal(KeyModifiers.Ctrl | KeyModifiers.Shift, combo.Modifiers);
        Assert.Equal(GamepadKey.V, combo.Key);
    }

    [Fact]
    public void Deserialized_Stick_Mappings_Preserve_Settings()
    {
        var back = ProfileSerializer.Deserialize(ProfileSerializer.Serialize(BuildFullProfile()));

        var mouse = Assert.IsType<MouseMoveTarget>(back.Mappings[9].Target);
        Assert.Equal(2000f, mouse.Settings.MaxSpeed);
        Assert.Equal(0.2f, mouse.Settings.DeadZone);
        Assert.True(mouse.Settings.InvertY);
        Assert.IsType<StickBinding>(back.Mappings[9].Source);

        var wheel = Assert.IsType<WheelScrollTarget>(back.Mappings[10].Target);
        Assert.Equal(15f, wheel.Settings.MaxTicksPerSecond);
        Assert.False(wheel.Settings.EnableHorizontal);
    }

    [Fact]
    public void MouseStickSettings_Have_Modern_Defaults()
    {
        var settings = new MouseStickSettings();

        Assert.Equal(0.15f, settings.DeadZone);
        Assert.Equal(1600f, settings.MaxSpeed);
        Assert.Equal(ResponseCurve.Precise, settings.Curve);
        Assert.Equal(0.08f, settings.ResponseTime);
        Assert.Equal(0.35f, settings.PrecisionScale);
        Assert.Equal(GamepadButton.LeftStick, settings.PrecisionButton);
        Assert.True(settings.InvertY);
    }

    [Fact]
    public void ScrollStickSettings_Have_Sensible_Defaults()
    {
        var settings = new ScrollStickSettings();

        Assert.Equal(0.2f, settings.DeadZone);
        Assert.Equal(12f, settings.MaxTicksPerSecond);
        Assert.True(settings.EnableHorizontal);
        Assert.False(settings.InvertY);
    }

    [Fact]
    public void Serialized_Json_Uses_Readable_Enums_And_Type_Discriminators()
    {
        var json = ProfileSerializer.Serialize(BuildFullProfile());

        Assert.Contains("\"$type\": \"button\"", json);
        Assert.Contains("\"$type\": \"combo\"", json);
        Assert.Contains("\"$type\": \"mouseMove\"", json);
        Assert.Contains("\"$type\": \"wheel\"", json);
        Assert.Contains("\"Layout\": \"Xbox\"", json);
        Assert.Contains("\"Key\": \"Enter\"", json);
    }

    [Fact]
    public void Deserialize_Rejects_Unknown_Target_Discriminator()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "name": "broken",
          "layout": "Generic",
          "mappings": [
            {
              "id": "00000000-0000-0000-0000-000000000001",
              "source": { "$type": "button", "button": "A" },
              "target": { "$type": "nope" },
              "enabled": true
            }
          ]
        }
        """;

        Assert.Throws<JsonException>(() => ProfileSerializer.Deserialize(json));
    }

    [Theory]
    [InlineData("Xbox Wireless Controller", GamepadLayout.Xbox)]
    [InlineData("Controller (Xbox One For Windows)", GamepadLayout.Xbox)]
    [InlineData("XInput STANDARD GAMEPAD", GamepadLayout.Xbox)]
    [InlineData("Sony DualSense Wireless Controller", GamepadLayout.DualShock)]
    [InlineData("PLAYSTATION(R)3 Controller", GamepadLayout.DualShock)]
    [InlineData("PS4 Controller", GamepadLayout.DualShock)]
    [InlineData("DualShock 4", GamepadLayout.DualShock)]
    [InlineData("Generic USB Gamepad", GamepadLayout.Generic)]
    [InlineData("", GamepadLayout.Generic)]
    public void Layout_Detection_Matches_Known_Vendors(string deviceName, GamepadLayout expected)
    {
        Assert.Equal(expected, GamepadLayoutDetector.Detect(deviceName));
    }

    [Fact]
    public void MappingEntry_Defaults_To_Enabled_With_Stable_Id()
    {
        var entry = new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = new KeyPressTarget(GamepadKey.Space) };

        Assert.True(entry.Enabled);
        Assert.NotEqual(Guid.Empty, entry.Id);
    }
}
