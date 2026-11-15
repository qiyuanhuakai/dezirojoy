using DeziroJoy.App.Services;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

public sealed class SchemeImportRecoveryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "dezirojoy-recovery-store", Guid.NewGuid().ToString("N"));

    public SchemeImportRecoveryStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void File_Store_Round_Trips_Nullable_Slots_Polymorphic_Targets_And_Rules()
    {
        var path = Path.Combine(_directory, "recovery.json");
        var store = new FileSchemeImportRecoveryStore(path);
        var recovery = new SchemeImportRecovery
        {
            DeviceId = "xinput:0",
            SchemeName = "完整快照",
            ActiveSlot = 3,
            Slots = new Dictionary<int, GamepadProfile?>
            {
                [1] = new GamepadProfile
                {
                    Name = "主配置",
                    Mappings =
                    [
                        new MappingEntry
                        {
                            Source = new ButtonBinding(GamepadButton.X)
                            {
                                Gesture = new ButtonGesture
                                {
                                    DoubleClickWindow = 0.25f,
                                    DoubleClickAction = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C),
                                },
                            },
                            Target = new KeyPressTarget(GamepadKey.Space),
                        },
                    ],
                },
                [3] = null,
            },
            ForegroundRules = new ForegroundSettings
            {
                Rules = [new ForegroundRule { ProcessName = "chrome", Slot = 2 }],
            },
        };

        store.Save(recovery);
        var loaded = new FileSchemeImportRecoveryStore(path).Load();

        Assert.Equivalent(recovery, loaded);
        Assert.IsType<KeyComboTarget>(Assert.IsType<ButtonBinding>(
            loaded!.Slots[1]!.Mappings[0].Source).Gesture!.DoubleClickAction);
        Assert.Null(loaded.Slots[3]);
        Assert.False(File.Exists(path + ".tmp"));

        store.Clear();
        Assert.False(File.Exists(path));
        Assert.Null(store.Load());
    }

    [Fact]
    public void Null_Slot_Dictionary_Is_Reported_As_A_Damaged_Recovery_Point()
    {
        var path = Path.Combine(_directory, "null-slots.json");
        File.WriteAllText(path, """
        {
          "SchemaVersion": 1,
          "DeviceId": "xinput:0",
          "SchemeName": "损坏的快照",
          "Slots": null,
          "ActiveSlot": 1
        }
        """);
        var store = new FileSchemeImportRecoveryStore(path);

        Assert.Null(store.Load());
        Assert.Contains("恢复点无法读取", store.LastError);
    }
}
