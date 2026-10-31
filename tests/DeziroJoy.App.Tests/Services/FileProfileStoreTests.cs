using DeziroJoy.App.Services;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

public class FileProfileStoreTests : IDisposable
{
    private readonly string _directory;

    public FileProfileStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-store-tests", Guid.NewGuid().ToString("N"));
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

    private static GamepadProfile SampleProfile(string deviceId) => new()
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
        ],
    };

    [Fact]
    public void Save_Then_Load_Roundtrips_The_Profile()
    {
        var store = new FileProfileStore(_directory);
        var profile = SampleProfile("xinput:0");

        store.Save(profile);
        var loaded = store.Load("xinput:0");

        Assert.NotNull(loaded);
        Assert.Equal("测试配置", loaded.Name);
        Assert.Equal(GamepadLayout.Xbox, loaded.Layout);
        Assert.Single(loaded.Mappings);
    }

    [Fact]
    public void Load_Unknown_Device_Returns_Null()
    {
        var store = new FileProfileStore(_directory);

        Assert.Null(store.Load("xinput:9"));
    }

    [Fact]
    public void Save_Overwrites_The_Previous_Profile()
    {
        var store = new FileProfileStore(_directory);
        store.Save(SampleProfile("dev-1"));

        store.Save(SampleProfile("dev-1") with { Name = "更新后的配置" });

        Assert.Equal("更新后的配置", store.Load("dev-1")!.Name);
    }

    [Fact]
    public void Delete_Removes_The_Stored_Profile()
    {
        var store = new FileProfileStore(_directory);
        store.Save(SampleProfile("dev-2"));

        Assert.True(store.Delete("dev-2"));
        Assert.Null(store.Load("dev-2"));
        Assert.False(store.Delete("dev-2"));
    }

    [Fact]
    public void LoadAll_Returns_Every_Stored_Profile()
    {
        var store = new FileProfileStore(_directory);
        store.Save(SampleProfile("dev-a"));
        store.Save(SampleProfile("dev-b"));

        Assert.Equal(2, store.LoadAll().Count);
    }

    [Theory]
    [InlineData("xinput:0")]
    [InlineData("/dev/input/event3")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j")]
    public void Device_Ids_With_Special_Characters_Are_Sanitized(string deviceId)
    {
        var store = new FileProfileStore(_directory);
        store.Save(SampleProfile(deviceId));

        Assert.NotNull(store.Load(deviceId));
    }

    [Fact]
    public void Profiles_Directory_Is_Created_On_Demand()
    {
        var nested = Path.Combine(_directory, "nested", "profiles");

        var store = new FileProfileStore(nested);

        Assert.True(Directory.Exists(nested));
        Assert.Equal(nested, store.ProfilesDirectory);
    }
}
