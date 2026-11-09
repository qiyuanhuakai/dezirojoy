using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The preset-slot service layer: the per-slot store keys, slot load/save, the slots a
/// device has in use, and the one-time migration of the legacy leader pair into slots
/// 1 and 2. Wire shape only - the backing store is the in-memory double.
/// </summary>
public class PresetStoreTests
{
    private static GamepadProfile SlotProfile(string name, string deviceId) => new()
    {
        Name = name,
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
    public void KeyFor_Slot_One_Is_The_Device_Id_Itself()
    {
        var store = new PresetStore(new InMemoryProfileStore());

        Assert.Equal("pad-1", PresetStore.KeyFor("pad-1", 1));
    }

    [Fact]
    public void KeyFor_Other_Slots_Are_Prefixed_With_The_Slot_Number()
    {
        var store = new PresetStore(new InMemoryProfileStore());

        Assert.Equal("preset2:pad-1", PresetStore.KeyFor("pad-1", 2));
        Assert.Equal("preset3:pad-1", PresetStore.KeyFor("pad-1", 3));
        Assert.Equal("preset4:pad-1", PresetStore.KeyFor("pad-1", 4));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    public void KeyFor_Rejects_A_Slot_Outside_One_To_Four(int slot)
    {
        var store = new PresetStore(new InMemoryProfileStore());

        Assert.Throws<ArgumentOutOfRangeException>(() => PresetStore.KeyFor("pad-1", slot));
    }

    [Fact]
    public void LoadSlot_Returns_Null_When_The_Slot_Has_No_Stored_Profile()
    {
        var store = new PresetStore(new InMemoryProfileStore());

        Assert.Null(store.LoadSlot("pad-1", 1));
        Assert.Null(store.LoadSlot("pad-1", 2));
    }

    [Fact]
    public void SaveSlot_Stores_Under_The_Slot_Key_And_LoadSlot_Round_Trips()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);

        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));
        var loaded = store.LoadSlot("pad-1", 2);

        Assert.NotNull(loaded);
        Assert.Equal("第二套", loaded.Name);
        Assert.Equal("preset2:pad-1", loaded.DeviceId);
        Assert.NotNull(inner.Load("preset2:pad-1"));
        Assert.Null(inner.Load("pad-1"));
    }

    [Fact]
    public void SaveSlot_Slot_One_Stores_Under_The_Device_Id()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);

        store.SaveSlot("pad-1", 1, SlotProfile("主配置", "whatever"));

        Assert.Equal("主配置", inner.Load("pad-1")!.Name);
        Assert.Equal("pad-1", inner.Load("pad-1")!.DeviceId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void LoadSlot_And_SaveSlot_Reject_A_Slot_Outside_One_To_Four(int slot)
    {
        var store = new PresetStore(new InMemoryProfileStore());

        Assert.Throws<ArgumentOutOfRangeException>(() => store.LoadSlot("pad-1", slot));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SaveSlot("pad-1", slot, SlotProfile("x", "pad-1")));
    }

    [Fact]
    public void SlotsInUse_Scans_One_To_Four_In_Order()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        store.SaveSlot("pad-1", 1, SlotProfile("主配置", "pad-1"));
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));
        store.SaveSlot("pad-1", 4, SlotProfile("第四套", "pad-1"));

        Assert.Equal([1, 2, 4], store.SlotsInUse("pad-1"));
    }

    [Fact]
    public void SlotsInUse_Is_Empty_For_An_Unknown_Device()
    {
        var store = new PresetStore(new InMemoryProfileStore());

        Assert.Empty(store.SlotsInUse("pad-9"));
    }

    [Fact]
    public void SlotsInUse_Ignores_Another_Devices_Slots()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));
        store.SaveSlot("pad-2", 2, SlotProfile("别的设备", "pad-2"));

        Assert.Equal([2], store.SlotsInUse("pad-1"));
    }

    [Fact]
    public void EnsureDefaults_Initializes_Four_Independent_Slots_On_First_Use()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        var descriptor = new GamepadDescriptor("pad-1", "Xbox Wireless Controller");

        var active = store.EnsureDefaults(descriptor, activeSlot: 3);

        Assert.Equal([1, 2, 3, 4], store.SlotsInUse("pad-1"));
        Assert.Equal("preset3:pad-1", active.DeviceId);
        for (var slot = 2; slot <= 4; slot++)
        {
            var profile = store.LoadSlot("pad-1", slot)!;
            Assert.Equal(PresetStore.KeyFor("pad-1", slot), profile.DeviceId);
            Assert.Equal(store.LoadSlot("pad-1", 1)!.Mappings.Count, profile.Mappings.Count);
        }
    }

    [Fact]
    public void EnsureDefaults_Preserves_Customized_Slots()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        var descriptor = new GamepadDescriptor("pad-1", "DualShock 4");
        store.SaveSlot("pad-1", 2, SlotProfile("Custom", "pad-1"));

        store.EnsureDefaults(descriptor);

        Assert.Equal("Custom", store.LoadSlot("pad-1", 2)!.Name);
        Assert.Equal([1, 2, 3, 4], store.SlotsInUse("pad-1"));
    }

    // ---- Legacy leader migration ------------------------------------------

    [Fact]
    public void Migrate_Without_A_Leader_Button_Is_A_No_Op()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        inner.Save(SlotProfile("主配置", "pad-1"));

        var migrated = store.MigrateLegacyLeader("pad-1", null, LeaderMode.Press, SlotProfile("第二套", "pad-1"));

        Assert.Null(migrated);
        Assert.Null(store.LoadSlot("pad-1", 2));
    }

    [Fact]
    public void Migrate_Clones_The_Base_Profile_Into_Slot_Two_With_One_Leader_Entry()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        inner.Save(SlotProfile("主配置", "pad-1"));

        var migrated = store.MigrateLegacyLeader("pad-1", GamepadButton.Back, LeaderMode.Press, null);

        Assert.NotNull(migrated);
        Assert.Equal("preset2:pad-1", migrated.DeviceId);
        Assert.Equal(2, migrated.Mappings.Count);
        var leader = migrated.Mappings.Single(mapping => mapping.Target is SpecialKeyTarget);
        Assert.Equal(new ButtonBinding(GamepadButton.Back), leader.Source);
        Assert.Equal(
            new SpecialKeyTarget(SpecialKeyKind.Leader, PrecisionMode.Toggle, LeaderSwitchMode.Press, SlotA: 1, SlotB: 2),
            leader.Target);
        Assert.True(leader.Enabled);
        Assert.Equal(migrated, store.LoadSlot("pad-1", 2));

        // The base profile stays in slot 1, untouched.
        Assert.Equal("主配置", store.LoadSlot("pad-1", 1)!.Name);
        Assert.Single(store.LoadSlot("pad-1", 1)!.Mappings);
    }

    [Fact]
    public void Migrate_Uses_The_Given_Alternate_Profile_For_Slot_Two()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        inner.Save(SlotProfile("主配置", "pad-1"));
        var alternate = new GamepadProfile
        {
            Name = "第二套",
            DeviceId = "leader-alternate:pad-1",
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
                    Source = new ButtonBinding(GamepadButton.A),
                    Target = new KeyPressTarget(GamepadKey.Space),
                },
            ],
        };

        var migrated = store.MigrateLegacyLeader("pad-1", GamepadButton.Back, LeaderMode.Press, alternate);

        Assert.NotNull(migrated);
        Assert.Equal("第二套", migrated.Name);
        Assert.Equal("preset2:pad-1", migrated.DeviceId);
        Assert.Equal(3, migrated.Mappings.Count);
        Assert.Equal(migrated, store.LoadSlot("pad-1", 2));
    }

    [Fact]
    public void Migrate_Hold_Mode_Maps_To_The_Hold_Switch_Mode()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        inner.Save(SlotProfile("主配置", "pad-1"));

        var migrated = store.MigrateLegacyLeader("pad-1", GamepadButton.Back, LeaderMode.Hold, null);

        var leader = migrated!.Mappings.Single(mapping => mapping.Target is SpecialKeyTarget);
        Assert.Equal(
            new SpecialKeyTarget(SpecialKeyKind.Leader, PrecisionMode.Toggle, LeaderSwitchMode.Hold, SlotA: 1, SlotB: 2),
            leader.Target);
    }

    [Fact]
    public void Migrate_Is_Idempotent_When_Run_Twice()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        inner.Save(SlotProfile("主配置", "pad-1"));

        var first = store.MigrateLegacyLeader("pad-1", GamepadButton.Back, LeaderMode.Press, null);
        var second = store.MigrateLegacyLeader("pad-1", GamepadButton.Back, LeaderMode.Press, null);

        Assert.Equal(first!.Mappings.Count, second!.Mappings.Count);
        Assert.Equal(2, second.Mappings.Count);
        Assert.Single(second.Mappings, mapping => mapping.Target is SpecialKeyTarget);
    }

    [Fact]
    public void Migrate_Is_Idempotent_When_Handed_The_Already_Migrated_Profile_Back()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        inner.Save(SlotProfile("主配置", "pad-1"));

        var first = store.MigrateLegacyLeader("pad-1", GamepadButton.Back, LeaderMode.Press, null);
        var second = store.MigrateLegacyLeader("pad-1", GamepadButton.Back, LeaderMode.Press, first);

        Assert.Equal(first!.Mappings.Count, second!.Mappings.Count);
        Assert.Single(second!.Mappings, mapping => mapping.Target is SpecialKeyTarget);
    }

    [Fact]
    public void Migrate_With_No_Base_And_No_Alternate_Returns_Null()
    {
        var store = new PresetStore(new InMemoryProfileStore());

        var migrated = store.MigrateLegacyLeader("pad-1", GamepadButton.Back, LeaderMode.Press, null);

        Assert.Null(migrated);
    }
}
