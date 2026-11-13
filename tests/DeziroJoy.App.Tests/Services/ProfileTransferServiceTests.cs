using DeziroJoy.App.Services;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// Export of a scheme bundle - the single import/export design. The contract under test is
/// that the file format is exactly <see cref="SchemeBundleCodec"/>'s output (no second codec),
/// that the extension is normalized for the user, and that a failed write is reported instead
/// of thrown.
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

    private static SchemeBundle SampleBundle()
    {
        var profile = new GamepadProfile
        {
            Name = "测试配置",
            DeviceId = "xinput:0",
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B),
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
            ],
        };

        return new SchemeBundle
        {
            Name = "测试方案",
            AppVersion = "9.9.9-test",
            Notes = "使用说明：先试主配置。",
            Presets =
            [
                new SchemePreset
                {
                    Slot = 1,
                    Profile = profile,
                    ActionNames = SchemeBundleFactory.ActionNamesOf(profile),
                },
            ],
        };
    }

    private string TempPath(string name) => Path.Combine(_directory, name);

    [Fact]
    public void ExportScheme_Writes_Exactly_What_The_Codec_Produces()
    {
        var bundle = SampleBundle();
        var path = TempPath("export.json");

        var result = ProfileTransferService.ExportScheme(bundle, path);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(path));
        Assert.Equal(SchemeBundleCodec.Serialize(bundle), File.ReadAllText(path));
        Assert.Contains("方案已导出", result.Message);
    }

    [Fact]
    public void ExportScheme_Then_Load_Roundtrips_The_Whole_Bundle()
    {
        var bundle = SampleBundle();
        var path = TempPath("roundtrip.json");

        ProfileTransferService.ExportScheme(bundle, path);
        var loaded = SchemeBundleCodec.Load(path);

        Assert.True(loaded.Succeeded, loaded.Message);
        Assert.Equal(bundle.Name, loaded.Bundle!.Name);
        Assert.Equal(bundle.AppVersion, loaded.Bundle.AppVersion);
        Assert.Equal(bundle.Notes, loaded.Bundle.Notes);
        Assert.Equal(bundle.Presets.Count, loaded.Bundle.Presets.Count);
        Assert.Equal(
            bundle.Presets[0].Profile.Mappings,
            loaded.Bundle.Presets[0].Profile.Mappings);
    }

    [Fact]
    public void ExportScheme_Appends_The_Extension_When_The_User_Omits_It()
    {
        var result = ProfileTransferService.ExportScheme(SampleBundle(), TempPath("no-extension"));

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(TempPath("no-extension.json")));
    }

    [Fact]
    public void ExportScheme_To_An_Unwritable_Location_Fails_With_A_Message()
    {
        var result = ProfileTransferService.ExportScheme(
            SampleBundle(), Path.Combine(_directory, "no", "such", "dir", "p.json"));

        Assert.False(result.Succeeded);
        Assert.Contains("导出失败", result.Message);
    }

    [Fact]
    public void ExportScheme_Without_A_Destination_Fails_With_A_Message()
    {
        var result = ProfileTransferService.ExportScheme(SampleBundle(), "   ");

        Assert.False(result.Succeeded);
        Assert.Contains("导出失败", result.Message);
    }

    [Fact]
    public void The_Picker_Filter_Only_Admits_Json_Files()
    {
        Assert.Equal(".json", ProfileTransferService.SchemeExtension);
        Assert.All(ProfileTransferService.SchemePatterns, pattern => Assert.EndsWith(".json", pattern));
        Assert.False(string.IsNullOrWhiteSpace(ProfileTransferService.SchemeTypeName));
    }
}
