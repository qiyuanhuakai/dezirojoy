using DeziroJoy.App.Services;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// Import/export of a single profile file. The contract under test is that the file format is
/// exactly <see cref="ProfileSerializer"/>'s output (no second codec) and that a malformed file
/// is reported instead of thrown.
/// </summary>
public class ProfileTransferServiceTests : IDisposable
{
    private readonly string _directory;

    public ProfileTransferServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-transfer-tests", Guid.NewGuid().ToString("N"));
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

    private static GamepadProfile SampleProfile(string deviceId = "xinput:0") => new()
    {
        Name = "测试配置",
        DeviceId = deviceId,
        Layout = GamepadLayout.Xbox,
        Mappings =
        [
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.B),
                Target = new KeyPressTarget(GamepadKey.Enter),
            },
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Right),
                Target = new MouseMoveTarget(new MouseStickSettings { MaxSpeed = 2400f, DeadZone = 0.2f }),
            },
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Left),
                Target = new WheelScrollTarget(new ScrollStickSettings { MaxTicksPerSecond = 20f }),
            },
        ],
    };

    private string TempPath(string name) => Path.Combine(_directory, name);

    [Fact]
    public void Export_Writes_Exactly_What_ProfileSerializer_Produces()
    {
        var profile = SampleProfile();
        var path = TempPath("export.json");

        var result = ProfileTransferService.Export(profile, path);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(path));
        Assert.Equal(
            ProfileSerializer.Serialize(profile with { SchemaVersion = GamepadProfile.CurrentSchemaVersion }),
            File.ReadAllText(path));
    }

    [Fact]
    public void Export_Appends_The_Extension_When_The_User_Omits_It()
    {
        var result = ProfileTransferService.Export(SampleProfile(), TempPath("no-extension"));

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(TempPath("no-extension.json")));
    }

    [Fact]
    public void Export_Then_Import_Roundtrips_The_Whole_Profile()
    {
        var profile = SampleProfile();
        var path = TempPath("roundtrip.json");

        ProfileTransferService.Export(profile, path);
        var result = ProfileTransferService.Import(path);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(result.Profile);
        Assert.Equal(profile.Name, result.Profile!.Name);
        Assert.Equal(profile.DeviceId, result.Profile.DeviceId);
        Assert.Equal(profile.Layout, result.Profile.Layout);
        Assert.Equal(profile.Mappings.Count, result.Profile.Mappings.Count);

        // Element-wise: the deserializer hands back a List where the source held a read-only
        // array, so record equality would compare container types rather than content.
        Assert.Equal(profile.Mappings, result.Profile.Mappings);
    }

    [Fact]
    public void Import_Preserves_The_Sensitivity_Settings_Of_Stick_Mappings()
    {
        var path = TempPath("settings.json");

        ProfileTransferService.Export(SampleProfile(), path);
        var result = ProfileTransferService.Import(path);

        var mouse = Assert.IsType<MouseMoveTarget>(
            result.Profile!.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Right }).Target);
        var wheel = Assert.IsType<WheelScrollTarget>(
            result.Profile.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Left }).Target);

        Assert.Equal(2400f, mouse.Settings.MaxSpeed);
        Assert.Equal(0.2f, mouse.Settings.DeadZone);
        Assert.Equal(20f, wheel.Settings.MaxTicksPerSecond);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"SchemaVersion\":1}")]
    [InlineData("{\"Name\":\"x\",\"Mappings\":\"not-a-list\"}")]
    public void Import_Of_A_Malformed_File_Fails_With_A_Message_And_No_Profile(string content)
    {
        var path = TempPath("malformed.json");
        File.WriteAllText(path, content);

        var result = ProfileTransferService.Import(path);

        Assert.False(result.Succeeded);
        Assert.Null(result.Profile);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Contains("导入失败", result.Message);
    }

    [Fact]
    public void Import_Of_A_Missing_File_Fails_Instead_Of_Throwing()
    {
        var result = ProfileTransferService.Import(TempPath("does-not-exist.json"));

        Assert.False(result.Succeeded);
        Assert.Null(result.Profile);
        Assert.Contains("导入失败", result.Message);
    }

    [Fact]
    public void Import_Of_A_Newer_Schema_Version_Is_Refused()
    {
        var path = TempPath("future.json");
        File.WriteAllText(path, "{\"SchemaVersion\":99,\"Name\":\"来自未来\",\"Mappings\":[]}");

        var result = ProfileTransferService.Import(path);

        Assert.False(result.Succeeded);
        Assert.Null(result.Profile);
        Assert.Contains("版本过新", result.Message);
    }

    [Fact]
    public void Import_Of_An_Empty_Mapping_List_Succeeds()
    {
        var path = TempPath("empty-mappings.json");
        File.WriteAllText(path, "{\"SchemaVersion\":1,\"Name\":\"空配置\",\"Mappings\":[]}");

        var result = ProfileTransferService.Import(path);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(result.Profile);
        Assert.Empty(result.Profile!.Mappings);
    }

    [Fact]
    public void Export_To_An_Unwritable_Location_Fails_With_A_Message()
    {
        var result = ProfileTransferService.Export(SampleProfile(), Path.Combine(_directory, "no", "such", "dir", "p.json"));

        Assert.False(result.Succeeded);
        Assert.Contains("导出失败", result.Message);
    }

    [Fact]
    public void Export_Without_A_Destination_Fails_With_A_Message()
    {
        var result = ProfileTransferService.Export(SampleProfile(), "   ");

        Assert.False(result.Succeeded);
        Assert.Contains("导出失败", result.Message);
    }

    [Fact]
    public void The_Picker_Filter_Only_Admits_Json_Files()
    {
        Assert.Equal(".json", ProfileTransferService.Extension);
        Assert.All(ProfileTransferService.Patterns, pattern => Assert.EndsWith(".json", pattern));
        Assert.False(string.IsNullOrWhiteSpace(ProfileTransferService.TypeName));
    }
}
