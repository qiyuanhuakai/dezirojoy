using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Engine;
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

        // The leader is device-global, so the base profile in slot 1 gains the very same
        // entry rather than staying leader-less; its own mapping is untouched.
        var primary = store.LoadSlot("pad-1", 1)!;
        Assert.Equal("主配置", primary.Name);
        Assert.Equal(2, primary.Mappings.Count);
        Assert.Contains(primary.Mappings, mapping => mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader });
        Assert.Contains(
            primary.Mappings,
            mapping => mapping.Target is KeyPressTarget { Key: GamepadKey.Enter });
    }

    [Fact]
    public void Migrate_Propagates_The_Leader_To_Every_Slot_Of_The_Device()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        inner.Save(SlotProfile("主配置", "pad-1"));
        store.SaveSlot("pad-1", 3, SlotProfile("第三套", "pad-1"));
        store.SaveSlot("pad-1", 4, SlotProfile("第四套", "pad-1"));

        store.MigrateLegacyLeader("pad-1", GamepadButton.Back, LeaderMode.Press, null);

        // Every slot carries the one leader entry, so the key flips presets identically
        // whichever slot is active.
        foreach (var slot in new[] { 1, 2, 3, 4 })
        {
            var leader = LeaderTarget(store.LoadSlot("pad-1", slot)!);
            Assert.NotNull(leader);
            Assert.Equal(1, leader!.SlotA);
            Assert.Equal(2, leader.SlotB);
        }
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

    // ---- One-time default materialization ----------------------------------

    [Fact]
    public void EnsureDefaults_Does_Not_Resurrect_A_Deleted_Slot()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        var descriptor = new GamepadDescriptor("pad-1", "Xbox Wireless Controller");
        store.EnsureDefaults(descriptor);
        Assert.Equal([1, 2, 3, 4], store.SlotsInUse("pad-1"));

        store.DeleteSlot("pad-1", 2);
        Assert.Equal([1, 3, 4], store.SlotsInUse("pad-1"));

        // A reconnect re-runs EnsureDefaults through EngineHost.EnsureRuntime; it must not
        // re-materialize the deleted default slot, only ever ensure the primary anchor.
        store.EnsureDefaults(descriptor);
        Assert.Equal([1, 3, 4], store.SlotsInUse("pad-1"));
        Assert.Null(store.LoadSlot("pad-1", 2));
    }

    [Fact]
    public void EnsureDefaults_Still_Materializes_The_Initial_Set_For_A_Fresh_Device()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        var descriptor = new GamepadDescriptor("pad-1", "Xbox Wireless Controller");

        store.EnsureDefaults(descriptor);

        Assert.Equal([1, 2, 3, 4], store.SlotsInUse("pad-1"));
    }

    // ---- Leader repair across target and gesture actions -------------------

    private static GamepadProfile GestureLeaderProfile(string name, string deviceId, int slotA, int slotB, bool longPress)
    {
        var action = new SpecialKeyTarget(SpecialKeyKind.Leader, PrecisionMode.Toggle, LeaderSwitchMode.Press, SlotA: slotA, SlotB: slotB);
        var gesture = longPress
            ? new ButtonGesture { LongPressThreshold = ButtonGesture.DefaultLongPressThreshold, LongPressAction = action }
            : new ButtonGesture { DoubleClickWindow = ButtonGesture.DefaultDoubleClickWindow, DoubleClickAction = action };

        return new GamepadProfile
        {
            Name = name,
            DeviceId = deviceId,
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B) { Gesture = gesture },
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
            ],
        };
    }

    private static SpecialKeyTarget? GestureLeaderAction(GamepadProfile profile, bool longPress)
        => profile.Mappings
            .Select(mapping => mapping.Source)
            .OfType<ButtonBinding>()
            .Select(binding => binding.Gesture)
            .Where(gesture => gesture is not null)
            .Select(gesture => longPress ? gesture!.LongPressAction : gesture!.DoubleClickAction)
            .OfType<SpecialKeyTarget>()
            .FirstOrDefault(target => target.Kind == SpecialKeyKind.Leader);

    [Fact]
    public void DeleteSlot_Repairs_A_Leader_In_A_Double_Click_Action()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        store.SaveSlot("pad-1", 1, SlotProfile("主配置", "pad-1"));
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));
        store.SaveSlot("pad-1", 3, GestureLeaderProfile("第三套", "pad-1", slotA: 2, slotB: 3, longPress: false));

        store.DeleteSlot("pad-1", 2);

        var leader = GestureLeaderAction(store.LoadSlot("pad-1", 3)!, longPress: false);
        Assert.NotNull(leader);
        Assert.Equal(1, leader!.SlotA);
        Assert.Equal(3, leader.SlotB);
    }

    [Fact]
    public void DeleteSlot_Repairs_A_Leader_In_A_Long_Press_Action()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        store.SaveSlot("pad-1", 1, SlotProfile("主配置", "pad-1"));
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));
        store.SaveSlot("pad-1", 3, GestureLeaderProfile("第三套", "pad-1", slotA: 2, slotB: 3, longPress: true));

        store.DeleteSlot("pad-1", 2);

        var leader = GestureLeaderAction(store.LoadSlot("pad-1", 3)!, longPress: true);
        Assert.NotNull(leader);
        Assert.Equal(1, leader!.SlotA);
        Assert.Equal(3, leader.SlotB);
    }

    [Fact]
    public void DeleteSlot_Drops_A_Gesture_Leader_With_No_Distinct_Survivor()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        // Slot 1's double-click flips between slot 1 and slot 2; deleting slot 2 leaves only slot 1.
        store.SaveSlot("pad-1", 1, GestureLeaderProfile("主配置", "pad-1", slotA: 1, slotB: 2, longPress: false));
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));

        store.DeleteSlot("pad-1", 2);

        // The leader action is dropped, but the gesture's timing and its re-fire-the-click
        // fallback survive untouched.
        Assert.Null(GestureLeaderAction(store.LoadSlot("pad-1", 1)!, longPress: false));
        var gesture = store.LoadSlot("pad-1", 1)!.Mappings
            .Select(mapping => mapping.Source).OfType<ButtonBinding>().Single().Gesture;
        Assert.NotNull(gesture);
        Assert.True(gesture!.HasDoubleClick);
        Assert.Null(gesture.DoubleClickAction);
    }

    [Fact]
    public void DeleteSlot_Repairs_A_Target_Leader_And_A_Gesture_Leader_Together()
    {
        var store = new PresetStore(new InMemoryProfileStore());
        store.SaveSlot("pad-1", 1, SlotProfile("主配置", "pad-1"));
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));
        var both = SlotProfile("第三套", "pad-1") with
        {
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.Back),
                    Target = new SpecialKeyTarget(SpecialKeyKind.Leader, PrecisionMode.Toggle, LeaderSwitchMode.Press, SlotA: 2, SlotB: 3),
                    Enabled = true,
                },
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B)
                    {
                        Gesture = new ButtonGesture
                        {
                            DoubleClickWindow = ButtonGesture.DefaultDoubleClickWindow,
                            DoubleClickAction = new SpecialKeyTarget(SpecialKeyKind.Leader, PrecisionMode.Toggle, LeaderSwitchMode.Press, SlotA: 2, SlotB: 3),
                        },
                    },
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
            ],
        };
        store.SaveSlot("pad-1", 3, both);

        store.DeleteSlot("pad-1", 2);

        var profile = store.LoadSlot("pad-1", 3)!;
        var targetLeader = profile.Mappings.Select(mapping => mapping.Target)
            .OfType<SpecialKeyTarget>().Single(target => target.Kind == SpecialKeyKind.Leader);
        Assert.Equal(1, targetLeader.SlotA);
        Assert.Equal(3, targetLeader.SlotB);

        var gestureLeader = GestureLeaderAction(profile, longPress: false);
        Assert.NotNull(gestureLeader);
        Assert.Equal(1, gestureLeader!.SlotA);
        Assert.Equal(3, gestureLeader.SlotB);
    }

    // ---- Device-global special keys -------------------------------------------

    private static GamepadProfile SpecialSlotProfile(
        string name,
        string deviceId,
        SpecialKeyTarget? precision = null,
        SpecialKeyTarget? leader = null,
        GamepadButton precisionButton = GamepadButton.RightBumper,
        GamepadButton leaderButton = GamepadButton.Back)
    {
        var mappings = new List<MappingEntry>
        {
            new()
            {
                Source = new ButtonBinding(GamepadButton.B),
                Target = new KeyPressTarget(GamepadKey.Enter),
            },
        };

        if (precision is not null)
        {
            mappings.Add(new MappingEntry
            {
                Source = new ButtonBinding(precisionButton),
                Target = precision,
                Enabled = true,
            });
        }

        if (leader is not null)
        {
            mappings.Add(new MappingEntry
            {
                Source = new ButtonBinding(leaderButton),
                Target = leader,
                Enabled = true,
            });
        }

        return new GamepadProfile
        {
            Name = name,
            DeviceId = deviceId,
            Layout = GamepadLayout.Xbox,
            Mappings = mappings,
        };
    }

    private static readonly SpecialKeyTarget PrecisionKey = new(SpecialKeyKind.Precision, PrecisionMode.Toggle);

    private static readonly SpecialKeyTarget LeaderKey =
        new(SpecialKeyKind.Leader, PrecisionMode.Toggle, LeaderSwitchMode.Press, SlotA: 1, SlotB: 2);

    [Fact]
    public void SyncSpecialKeys_Replaces_The_Subset_In_Every_Slot_Leaving_Other_Mappings_And_Ids_Untouched()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        var canonical = SpecialSlotProfile(
            "主配置",
            "pad-1",
            precision: PrecisionKey with { PrecisionMode = PrecisionMode.Hold },
            leader: LeaderKey);
        store.SaveSlot("pad-1", 1, canonical);

        // Slot 2 keeps its own click mapping and a stale leader, and carries the same ids.
        var slotTwoBefore = SpecialSlotProfile(
            "第二套",
            "pad-1",
            precision: PrecisionKey,
            leader: LeaderKey with { SlotB = 3 });
        var idsBefore = slotTwoBefore.Mappings.Select(mapping => mapping.Id).ToArray();
        store.SaveSlot("pad-1", 2, slotTwoBefore);

        store.SyncSpecialKeys("pad-1", canonical);

        var slotTwo = store.LoadSlot("pad-1", 2)!;
        Assert.Equal("第二套", slotTwo.Name);

        // Every special key now reads exactly as the canonical profile's do.
        Assert.Equal(
            PrecisionKey with { PrecisionMode = PrecisionMode.Hold },
            slotTwo.Mappings.Single(m => m.Source is ButtonBinding { Button: GamepadButton.RightBumper }).Target);
        Assert.Equal(LeaderKey, slotTwo.Mappings.Single(m => m.Source is ButtonBinding { Button: GamepadButton.Back }).Target);

        // The slot's own mapping and every entry id survived untouched.
        Assert.Contains(
            slotTwo.Mappings,
            mapping => mapping.Target is KeyPressTarget { Key: GamepadKey.Enter });
        Assert.Equal(idsBefore, slotTwo.Mappings.Select(mapping => mapping.Id).ToArray());
    }

    [Fact]
    public void SyncSpecialKeys_Removes_A_Special_Key_Every_Slot_No_Longer_Carries()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        store.SaveSlot("pad-1", 1, SpecialSlotProfile("主配置", "pad-1", precision: PrecisionKey, leader: LeaderKey));
        store.SaveSlot("pad-1", 2, SpecialSlotProfile("第二套", "pad-1", precision: PrecisionKey, leader: LeaderKey));
        store.SaveSlot("pad-1", 3, SpecialSlotProfile("第三套", "pad-1", precision: PrecisionKey, leader: LeaderKey));

        // The leader was deleted from the active slot; the fan-out removes it everywhere.
        store.SyncSpecialKeys("pad-1", SpecialSlotProfile("主配置", "pad-1", precision: PrecisionKey));

        foreach (var slot in new[] { 1, 2, 3 })
        {
            var profile = store.LoadSlot("pad-1", slot)!;
            Assert.Null(LeaderTarget(profile));
            Assert.NotNull(profile.Mappings.Single(m => m.Source is ButtonBinding { Button: GamepadButton.RightBumper }).Target as SpecialKeyTarget);
        }
    }

    [Fact]
    public void SyncSpecialKeys_Adds_A_Missing_Special_Key_To_Every_Slot()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        var canonical = SpecialSlotProfile("主配置", "pad-1", leader: LeaderKey);
        store.SaveSlot("pad-1", 1, canonical);
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));
        store.SaveSlot("pad-1", 3, SlotProfile("第三套", "pad-1"));

        store.SyncSpecialKeys("pad-1", canonical);

        foreach (var slot in new[] { 1, 2, 3 })
        {
            Assert.Equal(LeaderKey, LeaderTarget(store.LoadSlot("pad-1", slot)!));
        }

        // The added entry leaves the slots' own mappings - and their ids - alone.
        var slotTwo = store.LoadSlot("pad-1", 2)!;
        Assert.Equal(2, slotTwo.Mappings.Count);
        Assert.Contains(slotTwo.Mappings, mapping => mapping.Target is KeyPressTarget { Key: GamepadKey.Enter });
    }

    [Fact]
    public void SyncSpecialKeys_Is_Idempotent()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        var canonical = SpecialSlotProfile("主配置", "pad-1", precision: PrecisionKey, leader: LeaderKey);
        store.SaveSlot("pad-1", 1, canonical);
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));

        store.SyncSpecialKeys("pad-1", canonical);
        var afterFirst = ProfileSerializer.Serialize(store.LoadSlot("pad-1", 2)!);

        store.SyncSpecialKeys("pad-1", canonical);

        Assert.Equal(afterFirst, ProfileSerializer.Serialize(store.LoadSlot("pad-1", 2)!));
    }

    [Fact]
    public void SyncSpecialKeys_Syncs_A_Gesture_Action_Without_Touching_The_Gesture_Timing()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);

        // The canonical profile carries a leader on B's long press at its own timing.
        var canonical = SlotProfile("主配置", "pad-1") with
        {
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B)
                    {
                        Gesture = new ButtonGesture
                        {
                            LongPressThreshold = 0.75f,
                            LongPressAction = LeaderKey,
                        },
                    },
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
            ],
        };
        store.SaveSlot("pad-1", 1, canonical);

        // Slot 2 chose its own (shorter) long-press timing for the same button.
        var slotTwo = SlotProfile("第二套", "pad-1") with
        {
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B)
                    {
                        Gesture = new ButtonGesture
                        {
                            LongPressThreshold = 0.25f,
                            LongPressAction = LeaderKey with { SlotB = 4 },
                        },
                    },
                    Target = new KeyPressTarget(GamepadKey.Space),
                },
            ],
        };
        store.SaveSlot("pad-1", 2, slotTwo);

        store.SyncSpecialKeys("pad-1", canonical);

        var synced = store.LoadSlot("pad-1", 2)!;
        var gesture = synced.Mappings.Single().Source is ButtonBinding button ? button.Gesture : null;
        Assert.NotNull(gesture);
        Assert.Equal(LeaderKey, gesture!.LongPressAction);

        // The slot's own timing and its own click target are untouched.
        Assert.Equal(0.25f, gesture.LongPressThreshold);
        Assert.IsType<KeyPressTarget>(synced.Mappings.Single().Target);
    }

    [Fact]
    public void SyncSpecialKeys_Materializes_A_Gesture_The_Slot_Does_Not_Have()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        var canonical = SlotProfile("主配置", "pad-1") with
        {
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B)
                    {
                        Gesture = new ButtonGesture
                        {
                            DoubleClickWindow = ButtonGesture.DefaultDoubleClickWindow,
                            DoubleClickAction = LeaderKey,
                        },
                    },
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
            ],
        };
        store.SaveSlot("pad-1", 1, canonical);

        // Slot 2 maps the same button with no gesture at all, so the shared key could not be
        // expressed there: the fan-out materializes the position at the pinned default.
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));

        store.SyncSpecialKeys("pad-1", canonical);

        var synced = store.LoadSlot("pad-1", 2)!;
        var gesture = synced.Mappings.Single(m => m.Source is ButtonBinding { Button: GamepadButton.B })
            .Source is ButtonBinding button ? button.Gesture : null;
        Assert.NotNull(gesture);
        Assert.Equal(LeaderKey, gesture!.DoubleClickAction);
        Assert.Equal(ButtonGesture.DefaultDoubleClickWindow, gesture.DoubleClickWindow);

        // The slot's own click target is untouched.
        Assert.IsType<KeyPressTarget>(synced.Mappings.Single(m => m.Source is ButtonBinding { Button: GamepadButton.B }).Target);
    }

    [Fact]
    public void SyncSpecialKeys_Only_Touches_The_Named_Device()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        var canonical = SpecialSlotProfile("主配置", "pad-1", leader: LeaderKey);
        store.SaveSlot("pad-1", 1, canonical);
        store.SaveSlot("pad-2", 1, SlotProfile("别的设备", "pad-2"));
        var otherBefore = ProfileSerializer.Serialize(store.LoadSlot("pad-2", 1)!);

        store.SyncSpecialKeys("pad-1", canonical);

        Assert.Equal(otherBefore, ProfileSerializer.Serialize(store.LoadSlot("pad-2", 1)!));
        Assert.Null(LeaderTarget(store.LoadSlot("pad-2", 1)!));
    }

    [Fact]
    public void SyncSpecialKeys_Keeps_A_Gesture_Key_Riding_On_An_Entry_Whose_Own_Target_Is_Another_Key()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);

        // One button is a precision key on its click and a leader on its long press: two
        // shared keys, two positions, one entry.
        var canonical = SlotProfile("主配置", "pad-1") with
        {
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B)
                    {
                        Gesture = new ButtonGesture
                        {
                            LongPressThreshold = ButtonGesture.DefaultLongPressThreshold,
                            LongPressAction = LeaderKey,
                        },
                    },
                    Target = PrecisionKey,
                    Enabled = true,
                },
            ],
        };
        store.SaveSlot("pad-1", 1, canonical);

        var slotTwo = SlotProfile("第二套", "pad-1") with
        {
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B)
                    {
                        Gesture = new ButtonGesture
                        {
                            LongPressThreshold = 0.9f,
                            LongPressAction = LeaderKey with { SlotB = 3 },
                        },
                    },
                    Target = PrecisionKey,
                    Enabled = true,
                },
            ],
        };
        store.SaveSlot("pad-1", 2, slotTwo);

        store.SyncSpecialKeys("pad-1", canonical);

        // Exactly one entry: both keys were updated in place rather than the gesture key being
        // appended as a second entry for the same button.
        var entry = Assert.Single(store.LoadSlot("pad-1", 2)!.Mappings);
        Assert.Equal(PrecisionKey, entry.Target);
        var gesture = Assert.IsType<ButtonBinding>(entry.Source).Gesture;
        Assert.NotNull(gesture);
        Assert.Equal(LeaderKey, gesture!.LongPressAction);

        // The slot's own gesture timing survives.
        Assert.Equal(0.9f, gesture.LongPressThreshold);
    }

    [Fact]
    public void SyncSpecialKeys_Keeps_A_Gesture_Key_When_The_Entry_Own_Target_Key_Is_Removed()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);

        // The shared set keeps only the long-press leader; the click's precision key is gone.
        var canonical = SlotProfile("主配置", "pad-1") with
        {
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B)
                    {
                        Gesture = new ButtonGesture
                        {
                            LongPressThreshold = ButtonGesture.DefaultLongPressThreshold,
                            LongPressAction = LeaderKey,
                        },
                    },
                    Target = new NoneTarget(),
                    Enabled = true,
                },
            ],
        };
        store.SaveSlot("pad-1", 1, canonical);

        var slotTwo = SlotProfile("第二套", "pad-1") with
        {
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B)
                    {
                        Gesture = new ButtonGesture
                        {
                            LongPressThreshold = ButtonGesture.DefaultLongPressThreshold,
                            LongPressAction = LeaderKey,
                        },
                    },
                    Target = PrecisionKey,
                    Enabled = true,
                },
            ],
        };
        store.SaveSlot("pad-1", 2, slotTwo);

        store.SyncSpecialKeys("pad-1", canonical);

        // The entry stays as a bare gesture whose click does nothing, so the shared leader it
        // carries is not dropped with the removed precision key.
        var entry = Assert.Single(store.LoadSlot("pad-1", 2)!.Mappings);
        Assert.IsType<NoneTarget>(entry.Target);
        var gesture = Assert.IsType<ButtonBinding>(entry.Source).Gesture;
        Assert.Equal(LeaderKey, gesture!.LongPressAction);
    }

    [Fact]
    public void SyncSpecialKeys_Matches_Extra_Buttons_By_Their_Raw_Code()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        var canonical = new GamepadProfile
        {
            Name = "主配置",
            DeviceId = "pad-1",
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.Extra, Code: 42),
                    Target = PrecisionKey,
                    Enabled = true,
                },
            ],
        };
        store.SaveSlot("pad-1", 1, canonical);
        store.SaveSlot("pad-1", 2, new GamepadProfile
        {
            Name = "第二套",
            DeviceId = "pad-1",
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.Extra, Code: 7),
                    Target = PrecisionKey,
                    Enabled = true,
                },
            ],
        });

        store.SyncSpecialKeys("pad-1", canonical);

        var synced = store.LoadSlot("pad-1", 2)!;
        var buttons = synced.Mappings
            .Select(mapping => mapping.Source)
            .OfType<ButtonBinding>()
            .ToArray();
        // The other extra button's precision key is gone, and the shared one took its place.
        Assert.Single(buttons);
        Assert.Equal(42, buttons[0].Code);
    }

    [Fact]
    public void CanonicalizeSpecialKeys_Prefers_The_Primary_Slot_And_Merges_Non_Primary_Only_Keys()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        store.SaveSlot("pad-1", 1, SpecialSlotProfile("主配置", "pad-1", leader: LeaderKey));
        // Slot 2 disagrees: a leader between other slots, and a precision key no other slot has.
        store.SaveSlot("pad-1", 2, SpecialSlotProfile(
            "第二套",
            "pad-1",
            precision: PrecisionKey,
            leader: LeaderKey with { SlotA = 2, SlotB = 3 }));
        store.SaveSlot("pad-1", 3, SlotProfile("第三套", "pad-1"));

        var changed = store.CanonicalizeSpecialKeys("pad-1");

        Assert.True(changed);
        foreach (var slot in new[] { 1, 2, 3 })
        {
            var profile = store.LoadSlot("pad-1", slot)!;
            // Slot 1's leader wins the conflict everywhere.
            Assert.Equal(LeaderKey, LeaderTarget(profile));
            // The precision key only slot 2 had is merged in, not dropped.
            Assert.NotNull(profile.Mappings.Single(m => m.Source is ButtonBinding { Button: GamepadButton.RightBumper }).Target as SpecialKeyTarget);
        }
    }

    [Fact]
    public void CanonicalizeSpecialKeys_Is_Idempotent()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        store.SaveSlot("pad-1", 1, SpecialSlotProfile("主配置", "pad-1", leader: LeaderKey));
        store.SaveSlot("pad-1", 2, SpecialSlotProfile("第二套", "pad-1", leader: LeaderKey with { SlotB = 3 }));

        Assert.True(store.CanonicalizeSpecialKeys("pad-1"));
        var afterFirst = store.SlotsInUse("pad-1")
            .ToDictionary(slot => slot, slot => ProfileSerializer.Serialize(store.LoadSlot("pad-1", slot)!));

        Assert.False(store.CanonicalizeSpecialKeys("pad-1"));
        foreach (var (slot, expected) in afterFirst)
        {
            Assert.Equal(expected, ProfileSerializer.Serialize(store.LoadSlot("pad-1", slot)!));
        }
    }

    [Fact]
    public void EnsureDefaults_Converges_Divergent_Slots_On_Reconnect()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        var descriptor = new GamepadDescriptor("pad-1", "Xbox Wireless Controller");
        store.SaveSlot("pad-1", 1, SpecialSlotProfile("主配置", "pad-1", leader: LeaderKey));
        store.SaveSlot("pad-1", 2, SlotProfile("第二套", "pad-1"));
        store.SaveSlot("pad-1", 3, SpecialSlotProfile("第三套", "pad-1", leader: LeaderKey with { SlotB = 4 }));

        var active = store.EnsureDefaults(descriptor, activeSlot: 2);

        // The runtime is handed the converged profile, and every slot agrees.
        Assert.Equal(LeaderKey, LeaderTarget(active));
        foreach (var slot in new[] { 1, 2, 3 })
        {
            Assert.Equal(LeaderKey, LeaderTarget(store.LoadSlot("pad-1", slot)!));
        }

        // A second reconnect rewrites nothing.
        var before = store.SlotsInUse("pad-1")
            .ToDictionary(slot => slot, slot => ProfileSerializer.Serialize(store.LoadSlot("pad-1", slot)!));
        store.EnsureDefaults(descriptor, activeSlot: 2);
        foreach (var (slot, expected) in before)
        {
            Assert.Equal(expected, ProfileSerializer.Serialize(store.LoadSlot("pad-1", slot)!));
        }
    }

    [Fact]
    public void Migrate_Merges_A_Special_Key_Only_The_Alternate_Profile_Had()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        inner.Save(SlotProfile("主配置", "pad-1"));
        var alternate = SpecialSlotProfile("第二套", "leader-alternate:pad-1", precision: PrecisionKey);

        store.MigrateLegacyLeader("pad-1", GamepadButton.Back, LeaderMode.Press, alternate);

        // The alternate's precision key is merged into every slot rather than dropped, and
        // the base profile's own mapping is still there.
        foreach (var slot in new[] { 1, 2 })
        {
            var profile = store.LoadSlot("pad-1", slot)!;
            Assert.NotNull(profile.Mappings.Single(m => m.Source is ButtonBinding { Button: GamepadButton.RightBumper }).Target as SpecialKeyTarget);
            Assert.NotNull(LeaderTarget(profile));
        }
    }

    [Fact]
    public void DeleteSlot_Keeps_The_Shared_Special_Keys_Identical_Across_Survivors()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        var leader = LeaderKey with { SlotA = 1, SlotB = 3 };
        foreach (var slot in new[] { 1, 2, 3 })
        {
            store.SaveSlot("pad-1", slot, SpecialSlotProfile($"槽 {slot}", "pad-1", leader: leader));
        }

        store.DeleteSlot("pad-1", 2);

        // The leader named slots 1 and 3, so the delete repairs nothing - and the shared key
        // stays byte-identical across the survivors rather than drifting per slot.
        foreach (var slot in new[] { 1, 3 })
        {
            Assert.Equal(leader, LeaderTarget(store.LoadSlot("pad-1", slot)!));
        }
    }

    [Fact]
    public void DeleteSlot_Repairs_The_Shared_Leader_Identically_In_Every_Survivor()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        var leader = LeaderKey with { SlotA = 2, SlotB = 3 };
        foreach (var slot in new[] { 1, 2, 3 })
        {
            store.SaveSlot("pad-1", slot, SpecialSlotProfile($"槽 {slot}", "pad-1", leader: leader));
        }

        // Deleting slot 2 retargets the leader in every surviving slot, identically.
        store.DeleteSlot("pad-1", 2);

        var repaired = LeaderKey with { SlotA = 1, SlotB = 3 };
        foreach (var slot in new[] { 1, 3 })
        {
            Assert.Equal(repaired, LeaderTarget(store.LoadSlot("pad-1", slot)!));
        }
    }

    // ---- Complex shortcuts must never be persisted into an engine-rejected shape ----

    private static MappingEntry V1ComplexShortcutEntry(GamepadButton button, float threshold = 0.50f)
        => new()
        {
            Source = new ButtonBinding(button)
            {
                Gesture = new ButtonGesture { LongPressThreshold = threshold },
            },
            Target = new ComplexShortcutTarget { Script = "hold Alt\nevery 1s tap Tab" },
            Enabled = true,
        };

    private static MappingEntry CurrentComplexShortcutEntry(GamepadButton button, float threshold = 0.50f)
        => new()
        {
            Source = new ButtonBinding(button)
            {
                Gesture = new ButtonGesture
                {
                    LongPressThreshold = threshold,
                    LongPressAction = new ComplexShortcutTarget { Script = "hold Alt\nevery 1s tap Tab" },
                },
            },
            Target = new NoneTarget(),
            Enabled = true,
        };

    private static MappingEntry LongPressLeaderEntry(GamepadButton button, float threshold = 0.50f)
        => new()
        {
            Source = new ButtonBinding(button)
            {
                Gesture = new ButtonGesture { LongPressThreshold = threshold, LongPressAction = LeaderKey },
            },
            Target = new NoneTarget(),
            Enabled = true,
        };

    private static GamepadProfile ComplexShortcutSlotProfile(string name, string deviceId, MappingEntry complex)
        => new()
        {
            Name = name,
            DeviceId = deviceId,
            Layout = GamepadLayout.Xbox,
            Mappings = [complex],
        };

    private static float LongPressThresholdOf(GamepadProfile profile)
        => Assert.IsType<ButtonBinding>(Assert.Single(profile.Mappings).Source).Gesture!.LongPressThreshold;

    [Fact]
    public void SyncSpecialKeys_Over_A_V1_Complex_Shortcut_Persists_A_Shape_Every_Slot_Can_Build()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        var canonical = ComplexShortcutSlotProfile("主配置", "pad-1", LongPressLeaderEntry(GamepadButton.Back));
        store.SaveSlot("pad-1", 1, canonical);

        // Slot 2 still stores the v1 shape: the program as the mapping's own target with a
        // bare long press. The engine normalizes that in memory and never writes it back, so
        // the fan-out has to keep the persisted result engine-valid itself.
        store.SaveSlot("pad-1", 2, ComplexShortcutSlotProfile(
            "第二套", "pad-1", V1ComplexShortcutEntry(GamepadButton.Back)));

        store.SyncSpecialKeys("pad-1", canonical);

        foreach (var slot in new[] { 1, 2 })
        {
            var profile = store.LoadSlot("pad-1", slot)!;

            // The whole point: what landed in the store is a shape the engine builds.
            _ = new MappingEngine(profile);

            var entry = Assert.Single(profile.Mappings);
            Assert.Equal(LeaderKey, Assert.IsType<ButtonBinding>(entry.Source).Gesture!.LongPressAction);
            Assert.IsType<NoneTarget>(entry.Target);
        }
    }

    [Fact]
    public void CanonicalizeSpecialKeys_Over_A_V1_Complex_Shortcut_Persists_A_Shape_Every_Slot_Can_Build()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        store.SaveSlot("pad-1", 1, ComplexShortcutSlotProfile(
            "主配置", "pad-1", LongPressLeaderEntry(GamepadButton.Back)));
        store.SaveSlot("pad-1", 2, ComplexShortcutSlotProfile(
            "第二套", "pad-1", V1ComplexShortcutEntry(GamepadButton.Back)));
        store.SaveSlot("pad-1", 3, ComplexShortcutSlotProfile(
            "第三套", "pad-1", V1ComplexShortcutEntry(GamepadButton.Back, threshold: 0.75f)));

        // The reconnect path: no new edit, the divergence is healed on its own.
        Assert.True(store.CanonicalizeSpecialKeys("pad-1"));

        foreach (var slot in new[] { 1, 2, 3 })
        {
            var profile = store.LoadSlot("pad-1", slot)!;
            _ = new MappingEngine(profile);
            Assert.Equal(
                LeaderKey,
                Assert.IsType<ButtonBinding>(Assert.Single(profile.Mappings).Source).Gesture!.LongPressAction);
        }

        // The slots kept their own gesture timing - only the action at the shared position
        // is the device-global one.
        Assert.Equal(0.50f, LongPressThresholdOf(store.LoadSlot("pad-1", 2)!));
        Assert.Equal(0.75f, LongPressThresholdOf(store.LoadSlot("pad-1", 3)!));
    }

    [Fact]
    public void SyncSpecialKeys_Displaces_A_Current_Shape_Complex_Shortcut_From_The_Claimed_Position()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);
        var canonical = ComplexShortcutSlotProfile("主配置", "pad-1", LongPressLeaderEntry(GamepadButton.Back));
        store.SaveSlot("pad-1", 1, canonical);

        // The current shape: the program is already the long press's own action, so the
        // position holds an ordinary action the shared key displaces.
        store.SaveSlot("pad-1", 2, ComplexShortcutSlotProfile(
            "第二套", "pad-1", CurrentComplexShortcutEntry(GamepadButton.Back, threshold: 0.90f)));

        store.SyncSpecialKeys("pad-1", canonical);

        var synced = store.LoadSlot("pad-1", 2)!;
        _ = new MappingEngine(synced);

        var entry = Assert.Single(synced.Mappings);
        var gesture = Assert.IsType<ButtonBinding>(entry.Source).Gesture!;
        Assert.Equal(LeaderKey, gesture.LongPressAction);
        Assert.IsType<NoneTarget>(entry.Target);

        // The slot's own timing survives the displacement.
        Assert.Equal(0.90f, gesture.LongPressThreshold);
    }

    [Fact]
    public void SyncSpecialKeys_Leaves_A_Complex_Shortcut_Alone_When_No_Key_Claims_Its_Position()
    {
        var inner = new InMemoryProfileStore();
        var store = new PresetStore(inner);

        // The shared set claims only the click position of a different button, so the v1
        // program's long press is untouched - and the stored v1 shape stays as it was.
        var canonical = SpecialSlotProfile("主配置", "pad-1", leader: LeaderKey);
        store.SaveSlot("pad-1", 1, canonical);
        var slotTwo = ComplexShortcutSlotProfile("第二套", "pad-1", V1ComplexShortcutEntry(GamepadButton.Back));
        store.SaveSlot("pad-1", 2, slotTwo);

        store.SyncSpecialKeys("pad-1", canonical);

        var synced = store.LoadSlot("pad-1", 2)!;
        var complex = Assert.Single(synced.Mappings, m => m.Target is ComplexShortcutTarget);
        Assert.Null(Assert.IsType<ButtonBinding>(complex.Source).Gesture!.LongPressAction);

        // The engine still builds it, exactly as it did before the fan-out.
        _ = new MappingEngine(synced);
    }
}
