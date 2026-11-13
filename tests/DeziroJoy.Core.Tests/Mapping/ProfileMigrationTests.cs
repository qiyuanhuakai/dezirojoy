using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Mapping;

/// <summary>
/// v1 -&gt; v2 profile migration. v1 stored the precision button inside each mouse-move
/// mapping's settings; v2 lifts it into one deduped special-key mapping per distinct
/// (button, mode) pair - defaults included, so migrating the stock LeftStick/Hold setup
/// preserves behavior byte-for-byte.
/// </summary>
public class ProfileMigrationTests
{
    private const string V1Json = """
    {
      "SchemaVersion": 1,
      "Name": "legacy",
      "Layout": "Generic",
      "Mappings": [
        {
          "Id": "00000000-0000-0000-0000-000000000001",
          "Source": { "$type": "stick", "Stick": "Right" },
          "Target": {
            "$type": "mouseMove",
            "Settings": { "PrecisionButton": "RightBumper", "PrecisionMode": "Toggle" }
          },
          "Enabled": true
        },
        {
          "Id": "00000000-0000-0000-0000-000000000002",
          "Source": { "$type": "stick", "Stick": "Left" },
          "Target": {
            "$type": "mouseMove",
            "Settings": { "PrecisionButton": "RightBumper", "PrecisionMode": "Toggle" }
          },
          "Enabled": true
        },
        {
          "Id": "00000000-0000-0000-0000-000000000003",
          "Source": { "$type": "stick", "Stick": "Right" },
          "Target": {
            "$type": "mouseMove",
            "Settings": { "PrecisionButton": "LeftBumper", "PrecisionMode": "Toggle" }
          },
          "Enabled": false
        }
      ]
    }
    """;

    private static GamepadProfile LoadV1() => ProfileSerializer.Deserialize(V1Json);

    private const string V1StrengthJson = """
    {
      "SchemaVersion": 1,
      "Name": "legacy-strength",
      "Mappings": [
        {
          "Id": "00000000-0000-0000-0000-000000000001",
          "Source": { "$type": "stick", "Stick": "Right" },
          "Target": { "$type": "mouseMove", "Settings": { "CurveStrength": 1.75 } },
          "Enabled": true
        },
        {
          "Id": "00000000-0000-0000-0000-000000000002",
          "Source": { "$type": "stick", "Stick": "Left" },
          "Target": { "$type": "wheel", "Settings": { "CurveStrength": 2.5 } },
          "Enabled": true
        }
      ]
    }
    """;

    private static GamepadProfile LoadV1Strength() => ProfileSerializer.Deserialize(V1StrengthJson);

    [Fact]
    public void V1_Profile_Migrates_To_V2_With_Deduped_SpecialKey_Entries()
    {
        var migrated = ProfileMigration.Migrate(LoadV1());

        Assert.Equal(2, migrated.SchemaVersion);
        Assert.Equal(4, migrated.Mappings.Count);

        var special = Assert.Single(migrated.Mappings, m => m.Target is SpecialKeyTarget);
        var target = Assert.IsType<SpecialKeyTarget>(special.Target);
        Assert.Equal(SpecialKeyKind.Precision, target.Kind);
        Assert.Equal(PrecisionMode.Toggle, target.PrecisionMode);
        Assert.Equal(GamepadButton.RightBumper, Assert.IsType<ButtonBinding>(special.Source).Button);
    }

    [Fact]
    public void Migration_Resets_The_Extracted_Settings_Fields_To_Defaults()
    {
        var migrated = ProfileMigration.Migrate(LoadV1());

        var extracted = migrated.Mappings
            .Where(m => m.Enabled)
            .Select(m => m.Target)
            .OfType<MouseMoveTarget>();

        foreach (var mouse in extracted)
        {
            Assert.Equal(GamepadButton.LeftStick, mouse.Settings.PrecisionButton);
            Assert.Equal(PrecisionMode.Hold, mouse.Settings.PrecisionMode);
        }
    }

    [Fact]
    public void Disabled_Mouse_Mappings_Are_Not_Extracted_Or_Reset()
    {
        var migrated = ProfileMigration.Migrate(LoadV1());

        var disabled = migrated.Mappings[2];
        var mouse = Assert.IsType<MouseMoveTarget>(disabled.Target);

        Assert.False(disabled.Enabled);
        Assert.Equal(GamepadButton.LeftBumper, mouse.Settings.PrecisionButton);
        Assert.Equal(PrecisionMode.Toggle, mouse.Settings.PrecisionMode);
    }

    [Fact]
    public void Second_Load_Of_The_Same_File_Is_Idempotent()
    {
        var first = ProfileMigration.Migrate(LoadV1());
        var second = ProfileMigration.Migrate(LoadV1());

        Assert.Equal(first.Mappings.Count, second.Mappings.Count);
        Assert.Single(second.Mappings, m => m.Target is SpecialKeyTarget);
    }

    [Fact]
    public void Migrating_An_Already_Migrated_Profile_Is_A_NoOp()
    {
        var once = ProfileMigration.Migrate(LoadV1());

        Assert.Same(once, ProfileMigration.Migrate(once));
    }

    [Fact]
    public void Default_MouseMove_Settings_Migrate_Too()
    {
        const string json = """
        {
          "SchemaVersion": 1,
          "Name": "defaults",
          "Mappings": [
            {
              "Id": "00000000-0000-0000-0000-000000000001",
              "Source": { "$type": "stick", "Stick": "Right" },
              "Target": { "$type": "mouseMove", "Settings": {} },
              "Enabled": true
            }
          ]
        }
        """;

        var migrated = ProfileMigration.Migrate(ProfileSerializer.Deserialize(json));

        var special = Assert.Single(migrated.Mappings, m => m.Target is SpecialKeyTarget);
        var target = Assert.IsType<SpecialKeyTarget>(special.Target);

        Assert.Equal(SpecialKeyKind.Precision, target.Kind);
        Assert.Equal(PrecisionMode.Hold, target.PrecisionMode);
        Assert.Equal(GamepadButton.LeftStick, Assert.IsType<ButtonBinding>(special.Source).Button);
    }

    [Fact]
    public void Migration_Clamps_CurveStrength_Back_Into_Unit_Range()
    {
        // v1 profiles could legally store strength > 1, which extrapolated the curve and
        // reversed direction or left [0,1]. Migration now normalizes it onto the legal
        // [0,1] range for every stick target it carries.
        var migrated = ProfileMigration.Migrate(LoadV1Strength());

        var mouse = Assert.IsType<MouseMoveTarget>(
            migrated.Mappings.Single(m => m.Target is MouseMoveTarget).Target);
        Assert.Equal(1f, mouse.Settings.CurveStrength);

        var wheel = Assert.IsType<WheelScrollTarget>(
            migrated.Mappings.Single(m => m.Target is WheelScrollTarget).Target);
        Assert.Equal(1f, wheel.Settings.CurveStrength);
    }
}
