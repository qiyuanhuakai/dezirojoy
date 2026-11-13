using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Mapping;

/// <summary>
/// v1 -&gt; v2 -&gt; v3 profile migration. v1 stored the precision button inside each
/// mouse-move mapping's settings; v2 lifts it into one deduped special-key mapping per
/// distinct (button, mode) pair - defaults included, so migrating the stock LeftStick/Hold
/// setup preserves behavior byte-for-byte. v3 merges the layer key into the leader, so the
/// separate layer target becomes a leader that switches the one temporary layer.
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

    /// <summary>
    /// A v2 profile: the separate layer key on RB, a base X and the layer's own X. This is
    /// the shape the merge replaced.
    /// </summary>
    private const string V2LayerJson = """
    {
      "SchemaVersion": 2,
      "Name": "legacy-layer",
      "Mappings": [
        {
          "Id": "00000000-0000-0000-0000-000000000001",
          "Source": { "$type": "button", "Button": "RightBumper" },
          "Target": { "$type": "specialKey", "Kind": "Layer", "SwitchMode": "Hold", "LayerId": 1 }
        },
        {
          "Id": "00000000-0000-0000-0000-000000000002",
          "Source": { "$type": "button", "Button": "X" },
          "Target": { "$type": "key", "Key": "C" }
        },
        {
          "Id": "00000000-0000-0000-0000-000000000003",
          "Source": { "$type": "button", "Button": "X" },
          "Target": { "$type": "key", "Key": "V" },
          "Layer": 1
        }
      ]
    }
    """;

    private static GamepadProfile LoadV2Layer() => ProfileSerializer.Deserialize(V2LayerJson);

    [Fact]
    public void V1_Profile_Migrates_To_V2_With_Deduped_SpecialKey_Entries()
    {
        var migrated = ProfileMigration.Migrate(LoadV1());

        // The upgrade is a chain: the profile comes out on the current schema, which is
        // where the v2 -> v3 step leaves it.
        Assert.Equal(GamepadProfile.CurrentSchemaVersion, migrated.SchemaVersion);
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

    [Fact]
    public void A_Layer_Key_Becomes_A_Leader_That_Switches_The_Temporary_Layer()
    {
        var migrated = ProfileMigration.Migrate(LoadV2Layer());

        Assert.Equal(GamepadProfile.CurrentSchemaVersion, migrated.SchemaVersion);

        var leader = Assert.Single(migrated.Mappings, m => m.Target is SpecialKeyTarget);
        var target = Assert.IsType<SpecialKeyTarget>(leader.Target);

        Assert.Equal(SpecialKeyKind.Leader, target.Kind);
        Assert.Equal(LeaderAction.Layer, target.Action);
        // The layer key was hold-only, and the merged leader keeps the switch mode the
        // profile stored rather than inventing one.
        Assert.Equal(LeaderSwitchMode.Hold, target.SwitchMode);
        Assert.Equal(GamepadButton.RightBumper, Assert.IsType<ButtonBinding>(leader.Source).Button);
    }

    [Fact]
    public void Every_Layer_Above_The_First_Folds_Into_The_One_Temporary_Layer()
    {
        const string json = """
        {
          "SchemaVersion": 2,
          "Name": "legacy-multi-layer",
          "Mappings": [
            {
              "Id": "00000000-0000-0000-0000-000000000001",
              "Source": { "$type": "button", "Button": "RightBumper" },
              "Target": { "$type": "specialKey", "Kind": "Layer", "SwitchMode": "Hold", "LayerId": 2 }
            },
            {
              "Id": "00000000-0000-0000-0000-000000000002",
              "Source": { "$type": "button", "Button": "X" },
              "Target": { "$type": "key", "Key": "V" },
              "Layer": 2
            },
            {
              "Id": "00000000-0000-0000-0000-000000000003",
              "Source": { "$type": "button", "Button": "Y" },
              "Target": { "$type": "key", "Key": "B" },
              "Layer": 3
            },
            {
              "Id": "00000000-0000-0000-0000-000000000004",
              "Source": { "$type": "button", "Button": "A" },
              "Target": { "$type": "key", "Key": "Space" }
            }
          ]
        }
        """;

        var migrated = ProfileMigration.Migrate(ProfileSerializer.Deserialize(json));

        Assert.Equal([0, 1, 1, 0], migrated.Mappings.Select(mapping => mapping.Layer));
        Assert.Equal(LeaderAction.Layer, Assert.IsType<SpecialKeyTarget>(migrated.Mappings[0].Target).Action);
    }

    [Fact]
    public void A_Migrated_Layer_Profile_Still_Runs_Its_Temporary_Layer()
    {
        // The fold is behavior-preserving: the engine still enters the layer the old
        // profile described, and the layer still overrides the base mapping it names.
        var engine = new MappingEngine(ProfileMigration.Migrate(LoadV2Layer()));

        Assert.Empty(engine.Tick(Held(GamepadButton.RightBumper), 1f / 64f));
        Assert.Equal(1, engine.ActiveLayerId);
        Assert.Equal(
            new KeyDownCommand(GamepadKey.V, KeyModifiers.None),
            Assert.Single(engine.Tick(Held(GamepadButton.RightBumper, GamepadButton.X), 1f / 64f)));

        // Releasing the leader with X still held hands the button back to the base mapping,
        // exactly as it did when the layer key stood on its own.
        Assert.Equal(
            [
                new KeyUpCommand(GamepadKey.V, KeyModifiers.None),
                new KeyDownCommand(GamepadKey.C, KeyModifiers.None),
            ],
            engine.Tick(Held(GamepadButton.X), 1f / 64f));
        Assert.Equal(0, engine.ActiveLayerId);
    }

    [Fact]
    public void A_Profile_With_No_Layer_Shape_Is_Only_Stamped_With_The_New_Schema()
    {
        var profile = ProfileSerializer.Deserialize(V2LayerJson) with
        {
            SchemaVersion = 2,
            Mappings = [new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.A),
                Target = new KeyPressTarget(GamepadKey.Space),
            }],
        };

        var migrated = ProfileMigration.Migrate(profile);

        Assert.Equal(GamepadProfile.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Single(migrated.Mappings);
        Assert.Equal(0, migrated.Mappings[0].Layer);
    }

    private static GamepadState Held(params GamepadButton[] buttons)
    {
        var mask = GamepadButtonMask.None;
        foreach (var button in buttons)
        {
            mask |= button.ToMask();
        }

        return GamepadState.Create(mask, 0, 0, 0, 0);
    }
}
