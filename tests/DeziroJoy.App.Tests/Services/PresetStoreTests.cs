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
    public void KeyFor_Rejects_A_Slot_Below_One(int slot)
    {
        var store = new PresetStore(new InMemoryProfileStore());

        Assert.Throws<ArgumentOutOfRangeException>(() => PresetStore.KeyFor("pad-1", slot));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(12)]
    public void KeyFor_Accepts_A_Slot_Beyond_The_Initial_Four(int slot)
    {
        Assert.Equal($"preset{slot}:pad-1", PresetStore.KeyFor("pad-1", slot));
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
    [InlineData(-3)]
    public void LoadSlot_And_SaveSlot_Reject_A_Slot_Below_One(int slot)
    {
        var store = new PresetStore(new InMemoryProfileStore());

        Assert.Throws<ArgumentOutOfRangeException>(() => store.LoadSlot("pad-1", slot));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SaveSlot("pad-1", slot, SlotProfile("x", "pad-1")));
    }

    [Fact]
    public void SaveSlot_And_LoadSlot_Round_Trip_A_Slot_Beyond_The_Initial_Four()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);

        store.SaveSlot("pad-1", 7, SlotProfile("第七套", "pad-1"));

        Assert.Equal("第七套", store.LoadSlot("pad-1", 7)!.Name);
        Assert.Equal("preset7:pad-1", inner.Load("preset7:pad-1")!.DeviceId);
    }

    [Fact]
    public void SlotsInUse_Returns_The_Stored_Slots_In_Ascending_Order()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        store.SaveSlot("pad-1", 1, SlotProfile("主配置", "pad-1"));
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));
        store.SaveSlot("pad-1", 4, SlotProfile("第四套", "pad-1"));

        Assert.Equal([1, 2, 4], store.SlotsInUse("pad-1"));
    }

    [Fact]
    public void SlotsInUse_Finds_Slots_Beyond_The_Initial_Four()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        store.SaveSlot("pad-1", 1, SlotProfile("主配置", "pad-1"));
        store.SaveSlot("pad-1", 6, SlotProfile("第六套", "pad-1"));
        store.SaveSlot("pad-1", 11, SlotProfile("第十一套", "pad-1"));

        Assert.Equal([1, 6, 11], store.SlotsInUse("pad-1"));
    }

    [Fact]
    public void SlotsInUse_Handles_A_Device_Id_That_Contains_A_Colon()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        store.SaveSlot("xinput:0", 1, SlotProfile("主配置", "xinput:0"));
        store.SaveSlot("xinput:0", 5, SlotProfile("第五套", "xinput:0"));

        Assert.Equal([1, 5], store.SlotsInUse("xinput:0"));
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

    // ---- Deleting a preset slot ---------------------------------------------

    private static GamepadProfile LeaderProfile(string name, string deviceId, int slotA, int slotB) => new()
    {
        Name = name,
        DeviceId = deviceId,
        Layout = GamepadLayout.Xbox,
        Mappings =
        [
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Back),
                Target = new SpecialKeyTarget(
                    SpecialKeyKind.Leader,
                    PrecisionMode.Toggle,
                    LeaderSwitchMode.Press,
                    SlotA: slotA,
                    SlotB: slotB),
                Enabled = true,
            },
        ],
    };

    private static SpecialKeyTarget? LeaderTarget(GamepadProfile profile)
        => profile.Mappings
            .Select(mapping => mapping.Target)
            .OfType<SpecialKeyTarget>()
            .FirstOrDefault(target => target.Kind == SpecialKeyKind.Leader);

    [Fact]
    public void DeleteSlot_Removes_The_Slot_From_The_Store_And_The_Slot_Set()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        store.SaveSlot("pad-1", 1, SlotProfile("主配置", "pad-1"));
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));
        store.SaveSlot("pad-1", 3, SlotProfile("第三套", "pad-1"));

        var outcome = store.DeleteSlot("pad-1", 2);

        Assert.Equal(PresetDeleteOutcome.Deleted, outcome);
        Assert.Equal([1, 3], store.SlotsInUse("pad-1"));
        Assert.Null(store.LoadSlot("pad-1", 2));
        Assert.Null(inner.Load("preset2:pad-1"));
        // The surviving slots are untouched.
        Assert.Equal("主配置", store.LoadSlot("pad-1", 1)!.Name);
        Assert.Equal("第三套", store.LoadSlot("pad-1", 3)!.Name);
    }

    [Fact]
    public void DeleteSlot_Refuses_The_Primary_Slot_And_Keeps_It()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        store.SaveSlot("pad-1", 1, SlotProfile("主配置", "pad-1"));
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));

        var outcome = store.DeleteSlot("pad-1", 1);

        Assert.Equal(PresetDeleteOutcome.PrimarySlot, outcome);
        Assert.Equal([1, 2], store.SlotsInUse("pad-1"));
        Assert.NotNull(store.LoadSlot("pad-1", 1));
    }

    [Fact]
    public void DeleteSlot_Refuses_To_Leave_The_Device_Preset_Less()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        store.SaveSlot("pad-1", 1, SlotProfile("主配置", "pad-1"));

        // The only slot is the protected primary; a delete of any other number is refused as
        // the last remaining preset rather than emptying the device.
        var outcome = store.DeleteSlot("pad-1", 2);

        Assert.Equal(PresetDeleteOutcome.LastRemaining, outcome);
        Assert.Equal([1], store.SlotsInUse("pad-1"));
    }

    [Fact]
    public void DeleteSlot_Reports_Nothing_Stored_For_An_Empty_Slot()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        store.SaveSlot("pad-1", 1, SlotProfile("主配置", "pad-1"));
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));

        var outcome = store.DeleteSlot("pad-1", 5);

        Assert.Equal(PresetDeleteOutcome.NothingStored, outcome);
        Assert.Equal([1, 2], store.SlotsInUse("pad-1"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void DeleteSlot_Rejects_A_Slot_Below_One(int slot)
    {
        var store = new PresetStore(new InMemoryProfileStore());
        store.SaveSlot("pad-1", 1, SlotProfile("主配置", "pad-1"));

        Assert.Throws<ArgumentOutOfRangeException>(() => store.DeleteSlot("pad-1", slot));
    }

    [Fact]
    public void DeleteSlot_Retargets_A_Leader_That_Named_The_Deleted_Slot()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        store.SaveSlot("pad-1", 1, SlotProfile("主配置", "pad-1"));
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));
        // Slot 4 flips between slot 2 and slot 4; deleting slot 2 must retarget the slot-2 end.
        store.SaveSlot("pad-1", 4, LeaderProfile("第四套", "pad-1", slotA: 2, slotB: 4));

        var outcome = store.DeleteSlot("pad-1", 2);

        Assert.Equal(PresetDeleteOutcome.Deleted, outcome);
        var leader = LeaderTarget(store.LoadSlot("pad-1", 4)!);
        Assert.NotNull(leader);
        // The deleted end moves to the lowest surviving slot other than the surviving end (4).
        Assert.Equal(1, leader!.SlotA);
        Assert.Equal(4, leader.SlotB);
    }

    [Fact]
    public void DeleteSlot_Drops_A_Leader_That_Has_No_Distinct_Survivor()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        // Slot 1 flips between slot 1 and slot 2; deleting slot 2 leaves only slot 1, so the
        // leader is dropped rather than written as a slot flipping against itself.
        store.SaveSlot("pad-1", 1, LeaderProfile("主配置", "pad-1", slotA: 1, slotB: 2));
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));

        var outcome = store.DeleteSlot("pad-1", 2);

        Assert.Equal(PresetDeleteOutcome.Deleted, outcome);
        Assert.Null(LeaderTarget(store.LoadSlot("pad-1", 1)!));
    }

    [Fact]
    public void DeleteSlot_Leaves_A_Leader_That_Never_Named_The_Deleted_Slot_Alone()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        store.SaveSlot("pad-1", 1, SlotProfile("主配置", "pad-1"));
        store.SaveSlot("pad-1", 2, LeaderProfile("第二套", "pad-1", slotA: 1, slotB: 2));
        store.SaveSlot("pad-1", 3, SlotProfile("第三套", "pad-1"));

        store.DeleteSlot("pad-1", 3);

        // The slot 1<->2 leader is untouched by deleting slot 3.
        var leader = LeaderTarget(store.LoadSlot("pad-1", 2)!);
        Assert.NotNull(leader);
        Assert.Equal(1, leader!.SlotA);
        Assert.Equal(2, leader.SlotB);
    }

    [Fact]
    public void DeleteSlot_Only_Touches_The_Named_Device()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        store.SaveSlot("pad-1", 1, SlotProfile("主配置", "pad-1"));
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));
        store.SaveSlot("pad-2", 2, SlotProfile("别的设备", "pad-2"));

        store.DeleteSlot("pad-1", 2);

        Assert.Equal([1], store.SlotsInUse("pad-1"));
        Assert.Equal([2], store.SlotsInUse("pad-2"));
        Assert.NotNull(store.LoadSlot("pad-2", 2));
    }
}
