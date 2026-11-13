using DeziroJoy.App.Services;
using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The scheme-bundle format: one file carrying a device's whole preset set, its device-global
/// special keys, its temporary layers, readable action names, the exporting version and usage
/// notes. The contract under test is that a round trip loses none of that, that a plain
/// single-profile file - what the old single-profile export wrote - is wrapped as a one-preset
/// scheme instead of refused, that a body which is neither is refused with guidance rather than
/// parsed into an empty scheme, and that the built-in Windows template is a complete,
/// engine-valid, mutually-consistent scheme.
/// </summary>
public class SchemeBundleTests : IDisposable
{
    private readonly string _directory;

    public SchemeBundleTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-scheme-bundle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private string TempPath(string name) => Path.Combine(_directory, name);

    /// <summary>
    /// A two-slot device whose slots disagree about the special keys the way a store edited by
    /// hand does: the leader sits only in slot 2, the precision key in both. The canonical
    /// subset merges them, so the export carries both.
    /// </summary>
    private static (PresetStore Presets, string DeviceId) DeviceWithScheme()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        const string deviceId = "pad-01";

        presets.SaveSlot(deviceId, 1, new GamepadProfile
        {
            Name = "桌面",
            DeviceId = deviceId,
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Left),
                    Target = new WheelScrollTarget(new ScrollStickSettings { MaxTicksPerSecond = 20f }),
                },
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.LeftStick),
                    Target = new SpecialKeyTarget(SpecialKeyKind.Precision, PrecisionMode.Toggle),
                },
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.X),
                    Target = new KeyPressTarget(GamepadKey.C),
                    Layer = 1,
                },
            ],
        });

        presets.SaveSlot(deviceId, 2, new GamepadProfile
        {
            Name = "游戏",
            DeviceId = deviceId,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right),
                    Target = new MouseMoveTarget(new MouseStickSettings { MaxSpeed = 2600f }),
                },
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.Back),
                    Target = new SpecialKeyTarget(SpecialKeyKind.Leader, SlotA: 1, SlotB: 2),
                },
            ],
        });

        return (presets, deviceId);
    }

    [Fact]
    public void Export_Then_Load_Round_Trips_Base_Layers_Special_Keys_Names_Version_And_Notes()
    {
        var (presets, deviceId) = DeviceWithScheme();
        var bundle = SchemeBundleFactory.FromDevice(
            presets, deviceId, "整套方案", "先切到桌面预设，再按住 RB 进入临时层。", "9.9.9-test");

        var path = TempPath("scheme.json");
        File.WriteAllText(path, SchemeBundleCodec.Serialize(bundle));
        var loaded = SchemeBundleCodec.Load(path);

        Assert.True(loaded.Succeeded, loaded.Message);
        var read = loaded.Bundle!;

        Assert.Equal("整套方案", read.Name);
        Assert.Equal("9.9.9-test", read.AppVersion);
        Assert.Equal("先切到桌面预设，再按住 RB 进入临时层。", read.Notes);
        Assert.Equal(2, read.Presets.Count);

        // The base config: every mapping, its settings and its layer tag.
        var baseProfile = read.Presets[0].Profile;
        Assert.Equal("桌面", baseProfile.Name);
        Assert.Equal(3, baseProfile.Mappings.Count);
        var wheel = Assert.IsType<WheelScrollTarget>(
            baseProfile.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Left }).Target);
        Assert.Equal(20f, wheel.Settings.MaxTicksPerSecond);
        Assert.Contains(baseProfile.Mappings, m => m.Layer == 1 && m.Target is KeyPressTarget { Key: GamepadKey.C });

        // The second preset travels with its own name and mappings.
        Assert.Equal("游戏", read.Presets[1].Profile.Name);
        Assert.Equal(2, read.Presets[1].Profile.Mappings.Count);

        // Temporary layers are named per preset, with what each of their buttons does.
        var layer = Assert.Single(read.Layers);
        Assert.Equal(1, layer.LayerId);
        Assert.Equal(1, layer.PresetSlot);
        Assert.Contains(layer.Actions, action => action.Source == "X 键" && action.Action == "C");

        // The device-global special keys: the merged subset, both of them, in profile order.
        Assert.Equal(2, read.SpecialKeys!.Count);
        Assert.Contains(read.SpecialKeys, key =>
            key.Kind == SpecialKeyKind.Precision && key.Button == GamepadButton.LeftStick);
        Assert.Contains(read.SpecialKeys, key =>
            key.Kind == SpecialKeyKind.Leader && key.Button == GamepadButton.Back);

        // Action names travel for every mapping, so the file reads as behaviour.
        Assert.Contains(read.Presets[0].ActionNames, action => action.Action == "滚轮滚动");
        Assert.Contains(read.Presets[1].ActionNames, action => action.Action == "鼠标移动");
    }

    [Fact]
    public void Serialize_Writes_The_Profiles_Through_The_One_Profile_Codec()
    {
        var (presets, deviceId) = DeviceWithScheme();
        var bundle = SchemeBundleFactory.FromDevice(presets, deviceId, "整套方案", null, "9.9.9-test");

        var json = SchemeBundleCodec.Serialize(bundle);

        // No second codec: the embedded profile body reads back through the profile
        // serializer into exactly the profile that went in.
        var embedded = System.Text.Json.JsonDocument.Parse(json)
            .RootElement.GetProperty("Presets")[0].GetProperty("Profile");
        var reloaded = ProfileSerializer.Deserialize(embedded.GetRawText());

        Assert.Equal(bundle.Presets[0].Profile.Name, reloaded.Name);
        Assert.Equal(bundle.Presets[0].Profile.Mappings, reloaded.Mappings);
    }

    [Fact]
    public void Load_Of_A_Malformed_File_Fails_With_A_Message_And_No_Bundle()
    {
        var path = TempPath("broken.json");
        File.WriteAllText(path, "{ definitely not a bundle");

        var result = SchemeBundleCodec.Load(path);

        Assert.False(result.Succeeded);
        Assert.Null(result.Bundle);
        Assert.Contains("导入失败", result.Message);
    }

    [Fact]
    public void Load_Of_A_Missing_File_Fails_Instead_Of_Throwing()
    {
        var result = SchemeBundleCodec.Load(TempPath("gone.json"));

        Assert.False(result.Succeeded);
        Assert.Null(result.Bundle);
        Assert.Contains("导入失败", result.Message);
    }

    [Fact]
    public void Load_Of_A_Plain_Profile_File_Wraps_It_As_A_One_Preset_Scheme()
    {
        // What the removed single-profile export wrote - or any profile file a user kept -
        // is still readable: it arrives as a one-preset scheme carrying that profile.
        var profile = new GamepadProfile
        {
            Name = "旧版单个配置",
            Layout = GamepadLayout.DualShock,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B),
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.LeftStick),
                    Target = new SpecialKeyTarget(SpecialKeyKind.Precision, PrecisionMode.Toggle),
                },
            ],
        };
        var path = TempPath("single-profile.json");
        File.WriteAllText(path, ProfileSerializer.Serialize(profile));

        var result = SchemeBundleCodec.Load(path);

        Assert.True(result.Succeeded, result.Message);
        var bundle = result.Bundle!;

        Assert.Equal("旧版单个配置", bundle.Name);
        var preset = Assert.Single(bundle.Presets);
        Assert.Equal(1, preset.Slot);
        Assert.Equal("旧版单个配置", preset.Profile.Name);
        Assert.Equal(profile.Mappings, preset.Profile.Mappings);
        Assert.Contains(preset.ActionNames, action => action.Action == "Enter");

        // The bundle does not name the special keys, so the import takes the subset the
        // profile itself carries - the same rule a bundle without the section follows.
        Assert.Null(bundle.SpecialKeys);
    }

    [Fact]
    public void Load_Of_A_Plain_Profile_Newer_Than_This_Build_Is_Refused()
    {
        var path = TempPath("future-profile.json");
        File.WriteAllText(path, "{\"SchemaVersion\":99,\"Name\":\"来自未来\",\"Mappings\":[]}");

        var result = SchemeBundleCodec.Load(path);

        Assert.False(result.Succeeded);
        Assert.Null(result.Bundle);
        Assert.Contains("版本过新", result.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":1}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Load_Of_A_Body_That_Is_Neither_A_Bundle_Nor_A_Profile_Is_Refused(string content)
    {
        var path = TempPath("neither.json");
        File.WriteAllText(path, content);

        var result = SchemeBundleCodec.Load(path);

        Assert.False(result.Succeeded);
        Assert.Null(result.Bundle);
        Assert.Contains("导入失败", result.Message);
    }

    [Fact]
    public void Load_Of_A_Newer_Schema_Version_Is_Refused()
    {
        var path = TempPath("future.json");
        File.WriteAllText(path, """
        {
          "SchemaVersion": 99,
          "Name": "来自未来",
          "Presets": [ { "Slot": 1, "Profile": { "Name": "x", "Mappings": [] } } ]
        }
        """);

        var result = SchemeBundleCodec.Load(path);

        Assert.False(result.Succeeded);
        Assert.Null(result.Bundle);
        Assert.Contains("版本过新", result.Message);
    }

    [Fact]
    public void Load_Of_A_Profile_Newer_Than_This_Build_Is_Refused()
    {
        var (presets, deviceId) = DeviceWithScheme();
        var bundle = SchemeBundleFactory.FromDevice(presets, deviceId, "整套方案", null, "9.9.9-test") with
        {
            Presets =
            [
                new SchemePreset
                {
                    Slot = 1,
                    Profile = new GamepadProfile { Name = "未来", SchemaVersion = 99, Mappings = [] },
                },
            ],
        };

        var path = TempPath("future-profile.json");
        File.WriteAllText(path, SchemeBundleCodec.Serialize(bundle));

        var result = SchemeBundleCodec.Load(path);

        Assert.False(result.Succeeded);
        Assert.Contains("版本过新", result.Message);
    }

    [Fact]
    public void Load_Refuses_A_Bundle_Whose_Presets_Name_The_Same_Slot_Twice()
    {
        var path = TempPath("duplicate-slot.json");
        File.WriteAllText(path, """
        {
          "SchemaVersion": 1,
          "Name": "重复槽位",
          "Presets": [
            { "Slot": 1, "Profile": { "Name": "a", "Mappings": [] } },
            { "Slot": 1, "Profile": { "Name": "b", "Mappings": [] } }
          ]
        }
        """);

        var result = SchemeBundleCodec.Load(path);

        Assert.False(result.Succeeded);
        Assert.Contains("槽位", result.Message);
    }

    [Fact]
    public void The_Windows_Template_Is_A_Complete_Two_Preset_Scheme()
    {
        var template = BuiltInSchemeTemplates.Windows;

        Assert.Equal(2, template.Presets.Count);
        Assert.Equal(1, template.Presets[0].Slot);
        Assert.Equal(2, template.Presets[1].Slot);
        Assert.False(string.IsNullOrWhiteSpace(template.Name));
        Assert.False(string.IsNullOrWhiteSpace(template.Notes));
        Assert.False(string.IsNullOrWhiteSpace(template.AppVersion));
        Assert.NotEmpty(template.Presets[0].Profile.Mappings);
        Assert.NotEmpty(template.Presets[1].Profile.Mappings);
    }

    [Fact]
    public void The_Windows_Template_Profiles_Build_In_The_Engine()
    {
        foreach (var preset in BuiltInSchemeTemplates.Windows.Presets)
        {
            // The engine is the strictest reader a profile has: a shape it rejects is a shape
            // the imported device could never run.
            var engine = new MappingEngine(preset.Profile);
            Assert.NotNull(engine);
        }
    }

    [Fact]
    public void The_Windows_Template_Is_Mutually_Consistent_Across_Its_Presets()
    {
        var template = BuiltInSchemeTemplates.Windows;

        // Every preset carries the same device-global special keys - the leader flips between
        // the two presets the bundle actually ships, and the layer leader switches the one
        // temporary layer both presets populate.
        foreach (var preset in template.Presets)
        {
            var kinds = preset.Profile.Mappings
                .Where(m => m.Target is SpecialKeyTarget)
                .Select(m => (SpecialKeyTarget)m.Target)
                .ToList();

            Assert.Contains(kinds, key => key.Kind == SpecialKeyKind.Precision);
            Assert.Contains(kinds, key => key.Kind == SpecialKeyKind.Leader && key.Action == LeaderAction.Layer);
            var presetLeader = Assert.Single(
                kinds, key => key.Kind == SpecialKeyKind.Leader && key.Action is LeaderAction.Preset);
            Assert.Equal(1, presetLeader.SlotA);
            Assert.Equal(2, presetLeader.SlotB);

            // The layer the layer leader enters is populated in this very preset, so holding
            // the key does something in both of them.
            Assert.Contains(preset.Profile.Mappings, m => m.Layer == 1);
        }

        // The bundle's own special-key section is the same subset, stated once.
        Assert.Equal(3, template.SpecialKeys!.Count);
        Assert.Contains(template.SpecialKeys, key => key.Kind == SpecialKeyKind.Precision);
        Assert.Contains(template.SpecialKeys, key => key.Kind == SpecialKeyKind.Leader);
        Assert.Contains(template.SpecialKeys, key =>
            key.Target is SpecialKeyTarget { Action: LeaderAction.Layer });
    }

    [Fact]
    public void The_Windows_Template_Round_Trips_Through_The_Codec()
    {
        var path = TempPath("windows-template.json");
        File.WriteAllText(path, SchemeBundleCodec.Serialize(BuiltInSchemeTemplates.Windows));

        var result = SchemeBundleCodec.Load(path);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(BuiltInSchemeTemplates.Windows.Name, result.Bundle!.Name);
        Assert.Equal(2, result.Bundle.Presets.Count);
        Assert.Equal(BuiltInSchemeTemplates.Windows.Notes, result.Bundle.Notes);
        Assert.Equal(3, result.Bundle.SpecialKeys!.Count);

        // Element-wise: the deserializer hands back a List where the template held an array,
        // so record equality would compare container types rather than content.
        Assert.Equal(
            BuiltInSchemeTemplates.Windows.Presets[0].Profile.Mappings,
            result.Bundle.Presets[0].Profile.Mappings);
        Assert.Equal(
            BuiltInSchemeTemplates.Windows.Presets[1].Profile.Mappings,
            result.Bundle.Presets[1].Profile.Mappings);
    }
}
