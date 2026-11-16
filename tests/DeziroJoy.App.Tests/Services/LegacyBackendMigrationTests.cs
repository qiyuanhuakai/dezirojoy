using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.Tests.Ipc;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Profiles;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The legacy-configuration migration. Switching the input backend changes the device
/// identity profiles are keyed by - XInput's <c>xinput:N</c>, evdev's <c>/dev/input/...</c>
/// path, SDL3's <c>sdl:{guid}...</c> - so without this the old configuration stayed
/// orphaned on disk while the new device materialized the library defaults, which is what
/// users read as "my presets were overwritten by the base preset". These tests pin the
/// conservative bridge: the old files are never touched, nothing is overwritten that the
/// user configured under SDL, an ambiguous or clearly-mismatched pairing does nothing, the
/// completion record makes a second run a no-op so deletions and resets survive reconnects
/// and restarts, and a damaged record declines rather than risk an overwrite.
/// </summary>
public sealed class LegacyBackendMigrationTests : IDisposable
{
    private const string LegacyId = "/dev/input/event7";

    private const string SdlId = "sdl:03000000abc";

    private readonly string _directory;

    public LegacyBackendMigrationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-legacy-migration", Guid.NewGuid().ToString("N"));
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
    }

    [Fact]
    public void A_V020_Custom_Profile_Is_Adopted_By_The_Sdl_Device_And_The_Old_Files_Stay_Put()
    {
        var store = new FileProfileStore(_directory);
        var legacyFiles = SeedLegacyStore(store, LegacySlotOne(), LegacySlotTwo());
        var before = Snapshot(legacyFiles);

        using var host = HostWith(store, SdlId);
        host.RefreshPresence();

        // The old backend's files are the evidence the user's configuration still exists:
        // the migration copies across, it never moves or rewrites them.
        Assert.Equal(before, Snapshot(legacyFiles));

        var presets = new PresetStore(store);
        var adopted = presets.LoadSlot(SdlId, 1);
        Assert.NotNull(adopted);
        Assert.Equal("我的 evdev 配置", adopted.Name);

        // A plain key, a gesture carrying a complex shortcut, a device-global special key
        // and an old multi-layer mapping all survive the copy; the layer itself is folded
        // into the one temporary layer by the store's own migration on read.
        Assert.Contains(adopted.Mappings, m => m.Target is KeyPressTarget { Key: GamepadKey.F13 });
        var gesture = Assert.Single(
            adopted.Mappings,
            m => m.Source is ButtonBinding { Button: GamepadButton.B });
        Assert.NotNull(gesture.Source as ButtonBinding);
        var longPress = (gesture.Source as ButtonBinding)!.Gesture!.LongPressAction;
        var script = Assert.IsType<ComplexShortcutTarget>(longPress);
        Assert.Contains("hold F23", script.Script);
        Assert.Contains(
            adopted.Mappings,
            m => m.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Precision });
        Assert.All(
            adopted.Mappings.Where(m => m.Source is ButtonBinding { Button: GamepadButton.X }),
            m => Assert.Equal(1, m.Layer));

        var second = presets.LoadSlot(SdlId, 2);
        Assert.NotNull(second);
        Assert.Equal("我的 evdev 配置 · 预设 2", second.Name);
        Assert.Contains(second.Mappings, m => m.Target is KeyPressTarget { Key: GamepadKey.F14 });

        var report = Assert.Single(host.LegacyMigrations);
        Assert.True(report.Migrated);
        Assert.Equal(ExpectedSlots, report.CopiedSlots);
        Assert.Equal(LegacyId, report.LegacyDeviceId);
    }

    [Fact]
    public void A_First_Sdl_Device_Without_Legacy_Config_Still_Gets_The_Library_Defaults()
    {
        var store = new FileProfileStore(_directory);

        using var host = HostWith(store, SdlId);
        host.RefreshPresence();

        Assert.Empty(host.LegacyMigrations);
        var runtime = Assert.Single(host.Controllers);
        Assert.Equal(DefaultProfileLibrary.CreateDefault(runtime.Descriptor).Mappings.Count, runtime.Profile.Mappings.Count);
    }

    [Fact]
    public void A_Restart_After_The_Migration_Copies_Nothing_Again()
    {
        var store = new FileProfileStore(_directory);
        SeedLegacyStore(store, LegacySlotOne(), LegacySlotTwo());

        using (var first = HostWith(store, SdlId))
        {
            first.RefreshPresence();
        }

        var afterFirst = Snapshot(Directory.GetFiles(_directory, "*.json"));

        using var second = HostWith(store, SdlId);
        second.RefreshPresence();

        // Idempotent: the copied slots are no longer the untouched default, so a second
        // host - a daemon restart - neither rewrites them nor invents a second copy.
        Assert.Equal(afterFirst, Snapshot(Directory.GetFiles(_directory, "*.json")));
        var report = Assert.Single(second.LegacyMigrations);
        Assert.False(report.Migrated);
        Assert.Contains(report.Notes, note => note.Contains("已是自定义配置"));
    }

    [Fact]
    public void An_Sdl_Slot_The_User_Already_Customized_Is_Never_Overwritten()
    {
        var store = new FileProfileStore(_directory);
        SeedLegacyStore(store, LegacySlotOne(), LegacySlotTwo());

        var presets = new PresetStore(store);
        presets.SaveSlot(SdlId, 1, new GamepadProfile
        {
            Name = "我的 SDL 配置",
            DeviceId = PresetStore.KeyFor(SdlId, 1),
            Mappings = [new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = new KeyPressTarget(GamepadKey.F20) }],
        });

        using var host = HostWith(store, SdlId);
        host.RefreshPresence();

        // Records compare their mapping lists by reference, so the round-trip through the
        // store is asserted field by field: the customized slot kept its own configuration,
        // and the legacy body for it was left on disk rather than copied over the top.
        var after = presets.LoadSlot(SdlId, 1);
        Assert.Equal("我的 SDL 配置", after!.Name);
        Assert.Contains(after.Mappings, m => m.Target is KeyPressTarget { Key: GamepadKey.F20 });
        Assert.DoesNotContain(after.Mappings, m => m.Target is KeyPressTarget { Key: GamepadKey.F13 });

        // A slot the SDL device does not have yet is still filled from the legacy device:
        // the rule is never to overwrite, not to refuse the rest of the user's own presets.
        var second = presets.LoadSlot(SdlId, 2);
        Assert.Contains(second!.Mappings, m => m.Target is KeyPressTarget { Key: GamepadKey.F14 });

        var report = Assert.Single(host.LegacyMigrations);
        Assert.Equal(2, Assert.Single(report.CopiedSlots));
        Assert.Contains(report.Notes, note => note.Contains("预设 1") && note.Contains("保留未动"));
    }

    [Fact]
    public void Auto_Generated_Sdl_Defaults_Are_Replaced_But_A_Modified_Slot_Is_Not()
    {
        var store = new FileProfileStore(_directory);

        // The alpha.1 state: the SDL device already materialized the library defaults on
        // its first run, before the user's old configuration was anywhere near it.
        var descriptor = new GamepadDescriptor(SdlId, "Xbox Wireless Controller");
        using (var warmup = HostWith(store, SdlId))
        {
            warmup.RefreshPresence();
        }

        var presets = new PresetStore(store);
        Assert.True(LegacyBackendMigration.MatchesAutoGenerated(presets.LoadSlot(SdlId, 1)!, 1, descriptor.Layout));
        Assert.True(LegacyBackendMigration.MatchesAutoGenerated(presets.LoadSlot(SdlId, 2)!, 2, descriptor.Layout));

        // Slot 3 was customized under SDL, so only it must survive the migration.
        presets.SaveSlot(SdlId, 3, new GamepadProfile
        {
            Name = "我的 SDL 配置 · 预设 3",
            DeviceId = PresetStore.KeyFor(SdlId, 3),
            PresetSlot = 3,
            Mappings = [new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = new KeyPressTarget(GamepadKey.F21) }],
        });

        SeedLegacyStore(store, LegacySlotOne(), LegacySlotTwo(), LegacySlotThree(), LegacySlotFour());

        using var host = HostWith(store, SdlId);
        host.RefreshPresence();

        // The untouched defaults were replaced by the user's own configuration...
        Assert.Contains(presets.LoadSlot(SdlId, 1)!.Mappings, m => m.Target is KeyPressTarget { Key: GamepadKey.F13 });
        Assert.Contains(presets.LoadSlot(SdlId, 2)!.Mappings, m => m.Target is KeyPressTarget { Key: GamepadKey.F14 });
        Assert.Contains(presets.LoadSlot(SdlId, 4)!.Mappings, m => m.Target is KeyPressTarget { Key: GamepadKey.F16 });

        // ...while the slot the user had already customized under SDL kept their work, and
        // the legacy body for that slot stayed on disk instead of being copied over it.
        Assert.Contains(presets.LoadSlot(SdlId, 3)!.Mappings, m => m.Target is KeyPressTarget { Key: GamepadKey.F21 });
        Assert.Contains(
            Assert.Single(host.LegacyMigrations).Notes,
            note => note.Contains("预设 3") && note.Contains("保留未动"));
    }

    [Fact]
    public void The_Auto_Generated_Proof_Rejects_Anything_The_User_Touched()
    {
        var descriptor = new GamepadDescriptor(SdlId, "Xbox Wireless Controller");
        var auto = new PresetStore(new FileProfileStore(_directory)).EnsureDefaults(descriptor);

        Assert.True(LegacyBackendMigration.MatchesAutoGenerated(auto, 1, descriptor.Layout));

        var renamed = auto with { Name = "我的配置" };
        Assert.False(LegacyBackendMigration.MatchesAutoGenerated(renamed, 1, descriptor.Layout));

        var edited = auto with
        {
            Mappings = [.. auto.Mappings, new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = new KeyPressTarget(GamepadKey.F13) }],
        };
        Assert.False(LegacyBackendMigration.MatchesAutoGenerated(edited, 1, descriptor.Layout));

        // The slot-1 shape is not the slot-2 shape: the derived name is part of the proof,
        // so a base profile never passes for a preset copy.
        Assert.False(LegacyBackendMigration.MatchesAutoGenerated(auto, 2, descriptor.Layout));

        // The slot-2 copy is the default in either of the two shapes it appears in: as
        // EnsureDefaults stores it (the primary's slot tag) and as the running daemon
        // carries it (tagged with the slot it loaded).
        var slotTwo = auto with
        {
            Name = $"{auto.Name} · 预设 2",
            DeviceId = PresetStore.KeyFor(SdlId, 2),
        };
        Assert.True(LegacyBackendMigration.MatchesAutoGenerated(slotTwo, 2, descriptor.Layout));
        Assert.True(LegacyBackendMigration.MatchesAutoGenerated(slotTwo with { PresetSlot = 2 }, 2, descriptor.Layout));

        // A slot-2 body that is not the default still fails the proof.
        Assert.False(LegacyBackendMigration.MatchesAutoGenerated(
            slotTwo with { PresetSlot = 2, Mappings = [.. slotTwo.Mappings, new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = new KeyPressTarget(GamepadKey.F13) }] },
            2,
            descriptor.Layout));
    }

    [Fact]
    public void Several_Devices_Without_Pairing_Evidence_Migrate_Nothing_And_Say_Why()
    {
        var store = new FileProfileStore(_directory);
        var legacyFiles = SeedLegacyStore(
            store,
            LegacySlotOne("/dev/input/event3"),
            LegacySlotOne("/dev/input/event7"));
        var before = Snapshot(legacyFiles);

        using var host = HostWith(store, "sdl:pad-a", "sdl:pad-b");
        host.RefreshPresence();

        // Two evdev pads and two SDL pads: nothing in either identity says which SDL device
        // is which physical pad, so nothing is copied and both reasons are reported.
        Assert.Equal(before, Snapshot(legacyFiles));
        var presets = new PresetStore(store);
        Assert.All(host.Controllers, runtime => Assert.DoesNotContain(
            presets.LoadSlot(runtime.Descriptor.DeviceId, 1)!.Mappings,
            m => m.Target is KeyPressTarget { Key: GamepadKey.F13 }));
        Assert.Contains(host.LegacyMigrations.SelectMany(report => report.Notes),
            note => note.Contains("未迁移"));
    }

    [Fact]
    public void Several_Devices_Are_Never_Paired_By_Player_Index()
    {
        var store = new FileProfileStore(_directory);
        var legacyFiles = SeedLegacyStore(
            store,
            LegacySlotOne("xinput:0", "xinput-0"),
            LegacySlotOne("xinput:1", "xinput-1"));
        var before = Snapshot(legacyFiles);

        // Two XInput slots and two SDL pads. A player index is a runtime assignment, not an
        // identity: nothing says which SDL device is which physical pad, so nothing is
        // copied - even though both sides "look" like they line up by index.
        using var host = HostWith(store, "sdl:pad-a", "sdl:pad-b");
        host.RefreshPresence();

        Assert.Equal(before, Snapshot(legacyFiles));
        var presets = new PresetStore(store);
        Assert.All(host.Controllers, runtime => Assert.DoesNotContain(
            presets.LoadSlot(runtime.Descriptor.DeviceId, 1)!.Mappings,
            m => m.Target is KeyPressTarget { Key: GamepadKey.F13 }));
        Assert.Contains(
            host.LegacyMigrations.SelectMany(report => report.Notes),
            note => note.Contains("无法可靠判断"));
    }

    [Fact]
    public void One_Legacy_Device_And_Several_Sdl_Devices_Are_Not_Paired_Either()
    {
        var store = new FileProfileStore(_directory);
        SeedLegacyStore(store, LegacySlotOne("/dev/input/event7"));

        using var host = HostWith(store, "sdl:pad-a", "sdl:pad-b");
        host.RefreshPresence();

        var presets = new PresetStore(store);
        Assert.All(host.Controllers, runtime => Assert.DoesNotContain(
            presets.LoadSlot(runtime.Descriptor.DeviceId, 1)!.Mappings,
            m => m.Target is KeyPressTarget { Key: GamepadKey.F13 }));
        Assert.Contains(
            host.LegacyMigrations.SelectMany(report => report.Notes),
            note => note.Contains("无法可靠判断"));
    }

    [Fact]
    public void A_Single_Pair_Whose_Layouts_Clearly_Differ_Is_Not_Migrated()
    {
        var store = new FileProfileStore(_directory);
        var legacyFiles = SeedLegacyStore(store, LegacySlotOne(layout: GamepadLayout.DualShock));
        var before = Snapshot(legacyFiles);

        // One legacy DualShock configuration and one Xbox-layout SDL pad: the only case
        // where even a single pair is provably not the same controller.
        var markers = new InMemoryLegacyMigrationMarkers();
        using var host = HostWith(store, markers, SdlId);
        host.RefreshPresence();

        Assert.Equal(before, Snapshot(legacyFiles));
        var presets = new PresetStore(store);
        Assert.DoesNotContain(
            presets.LoadSlot(SdlId, 1)!.Mappings,
            m => m.Target is KeyPressTarget { Key: GamepadKey.F13 });
        var report = Assert.Single(host.LegacyMigrations);
        Assert.False(report.Migrated);
        Assert.Contains(report.Notes, note => note.Contains("布局明确不同"));

        // The pair was processed once, so a restart does not re-open the question.
        using var restarted = HostWith(store, markers, SdlId);
        restarted.RefreshPresence();

        Assert.Empty(restarted.LegacyMigrations);
        Assert.DoesNotContain(
            presets.LoadSlot(SdlId, 1)!.Mappings,
            m => m.Target is KeyPressTarget { Key: GamepadKey.F13 });
    }

    [Fact]
    public void A_Generic_Layout_On_Either_Side_Still_Migrates()
    {
        // A generic layout is an unknown, not a difference: the legacy side cannot tell what
        // the pad is, so the configuration still moves across.
        var unknownStore = new FileProfileStore(_directory);
        SeedLegacyStore(unknownStore, LegacySlotOne(layout: GamepadLayout.Generic));

        using var unknown = HostWith(unknownStore, SdlId);
        unknown.RefreshPresence();

        Assert.Contains(
            new PresetStore(unknownStore).LoadSlot(SdlId, 1)!.Mappings,
            m => m.Target is KeyPressTarget { Key: GamepadKey.F13 });

        // The other direction: the SDL pad reports no layout either, so a specific legacy
        // layout is not evidence of a different controller.
        var genericStore = new FileProfileStore(_directory);
        SeedLegacyStore(genericStore, LegacySlotOne(layout: GamepadLayout.DualShock));

        var provider = new FakeGamepadProvider();
        provider.Add(SdlId, "Gamepad");
        using var generic = new EngineHost(
            provider,
            new RecordingOutputSink(),
            genericStore,
            legacyMigration: new LegacyBackendMigration(new InMemoryLegacyMigrationMarkers()));
        generic.RefreshPresence();

        Assert.Contains(
            new PresetStore(genericStore).LoadSlot(SdlId, 1)!.Mappings,
            m => m.Target is KeyPressTarget { Key: GamepadKey.F13 });
    }

    [Fact]
    public void A_Non_Sdl_Device_Is_Never_Migrated_Onto()
    {
        var store = new FileProfileStore(_directory);
        SeedLegacyStore(store, LegacySlotOne("/dev/input/event3"));

        // The same store under the old backend: the evdev device keeps its own key and the
        // migration has nothing to say about it - the other pad's configuration is not
        // quietly adopted by whichever pad happens to connect.
        using var host = HostWith(store, "/dev/input/event7");
        host.RefreshPresence();

        Assert.Empty(host.LegacyMigrations);
        var runtime = Assert.Single(host.Controllers);
        Assert.DoesNotContain(runtime.Profile.Mappings, m => m.Target is KeyPressTarget { Key: GamepadKey.F13 });
    }

    [Fact]
    public void The_Daemon_State_Restore_Refuses_To_Write_A_Default_Body_Back()
    {
        var store = new InMemoryProfileStore();
        var host = new FakeDaemonHost();
        host.AddDevice(SdlId);
        var statePath = Path.Combine(_directory, "daemon-state.json");

        // A daemon that started on a never-configured device persists the library defaults
        // as its desired body; restoring that must not land on the device's configuration.
        new DaemonStateStore(statePath).Save(new DaemonState(
            MappingEnabled: false,
            ProfileName: "Xbox 布局默认配置",
            ProfileJson: ProfileSerializer.Serialize(
                DefaultProfileLibrary.CreateDefault(new GamepadDescriptor(SdlId, "Xbox Wireless Controller"))),
            Revision: 0));

        var server = new DaemonServer(host, store, new DaemonStateStore(statePath));
        server.ResumeDesiredState();

        Assert.Null(new PresetStore(store).LoadSlot(SdlId, 1));
    }

    [Fact]
    public void The_Daemon_State_Restore_Still_Writes_A_Real_Configuration_Back()
    {
        var store = new InMemoryProfileStore();
        var host = new FakeDaemonHost();
        host.AddDevice(SdlId);
        var statePath = Path.Combine(_directory, "daemon-state.json");

        var custom = new GamepadProfile
        {
            Name = "我的配置",
            DeviceId = SdlId,
            Mappings = [new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = new KeyPressTarget(GamepadKey.F13) }],
        };
        new DaemonStateStore(statePath).Save(new DaemonState(
            MappingEnabled: false,
            ProfileName: custom.Name,
            ProfileJson: ProfileSerializer.Serialize(custom),
            Revision: 0));

        var server = new DaemonServer(host, store, new DaemonStateStore(statePath));
        server.ResumeDesiredState();

        // An empty slot with a real body behind it is exactly what the restore is for.
        Assert.Contains(
            new PresetStore(store).LoadSlot(SdlId, 1)!.Mappings,
            m => m.Target is KeyPressTarget { Key: GamepadKey.F13 });
    }

    private static EngineHost HostWith(
        FileProfileStore store,
        params string[] deviceIds)
        => HostWith(store, markers: null, deviceIds);

    private static EngineHost HostWith(
        FileProfileStore store,
        ILegacyMigrationMarkers? markers,
        params string[] deviceIds)
    {
        var provider = new FakeGamepadProvider();
        foreach (var deviceId in deviceIds)
        {
            provider.Add(deviceId, "Xbox Wireless Controller");
        }

        return new EngineHost(
            provider,
            new RecordingOutputSink(),
            store,
            legacyMigration: new LegacyBackendMigration(markers ?? new InMemoryLegacyMigrationMarkers()));
    }

    [Fact]
    public void A_Deleted_Slot_Stays_Deleted_Across_Reconnect_And_Restart()
    {
        var store = new FileProfileStore(_directory);
        SeedLegacyStore(store, LegacySlotOne(), LegacySlotTwo());
        var markers = new InMemoryLegacyMigrationMarkers();

        using (var first = HostWith(store, markers, SdlId))
        {
            first.RefreshPresence();
        }

        var presets = new PresetStore(store);
        Assert.Contains(presets.LoadSlot(SdlId, 2)!.Mappings, m => m.Target is KeyPressTarget { Key: GamepadKey.F14 });

        // The user deletes the second preset: it is their configuration to prune.
        Assert.Equal(PresetDeleteOutcome.Deleted, presets.DeleteSlot(SdlId, 2));

        // A reconnect and a restart must not resurrect it from the old configuration.
        using (var reconnect = HostWith(store, markers, SdlId))
        {
            reconnect.RefreshPresence();
        }

        using (var restart = HostWith(store, markers, SdlId))
        {
            restart.RefreshPresence();
        }

        Assert.Null(presets.LoadSlot(SdlId, 2));
        Assert.Contains(presets.LoadSlot(SdlId, 1)!.Mappings, m => m.Target is KeyPressTarget { Key: GamepadKey.F13 });
    }

    [Fact]
    public void A_Slot_Reset_To_The_Defaults_Is_Not_Replaced_By_The_Legacy_Configuration()
    {
        var store = new FileProfileStore(_directory);

        // A legacy configuration with no device-global special keys, so the reset stands on
        // its own: the special-key sharing rule would otherwise converge another slot's keys
        // back into this one, which is that rule working as designed rather than the
        // migration repeating itself.
        SeedLegacyStore(store, LegacyPlainSlotOne());
        var markers = new InMemoryLegacyMigrationMarkers();

        using (var first = HostWith(store, markers, SdlId))
        {
            first.RefreshPresence();
        }

        var presets = new PresetStore(store);
        Assert.Contains(presets.LoadSlot(SdlId, 1)!.Mappings, m => m.Target is KeyPressTarget { Key: GamepadKey.F13 });

        // The user explicitly puts the slot back to the library defaults.
        presets.SaveSlot(
            SdlId,
            1,
            DefaultProfileLibrary.CreateDefault(new GamepadDescriptor(SdlId, "Xbox Wireless Controller")));

        using (var restart = HostWith(store, markers, SdlId))
        {
            restart.RefreshPresence();
        }

        // The reset stands: the pair was processed once, so the old configuration does not
        // quietly take the slot back.
        Assert.DoesNotContain(
            presets.LoadSlot(SdlId, 1)!.Mappings,
            m => m.Target is KeyPressTarget { Key: GamepadKey.F13 });
        Assert.Equal(10, presets.LoadSlot(SdlId, 1)!.Mappings.Count);
    }

    [Fact]
    public void The_Record_Skips_A_Second_Run_Entirely()
    {
        var store = new FileProfileStore(_directory);
        SeedLegacyStore(store, LegacySlotOne(), LegacySlotTwo());
        var markers = new InMemoryLegacyMigrationMarkers();

        using (var first = HostWith(store, markers, SdlId))
        {
            first.RefreshPresence();
        }

        var after = Snapshot(Directory.GetFiles(_directory, "*.json"));

        // Same record, new host: the pair is known, so the migration says nothing at all
        // rather than re-reporting a decision it already made and wrote down.
        using (var second = HostWith(store, markers, SdlId))
        {
            second.RefreshPresence();

            Assert.Empty(second.LegacyMigrations);
        }

        Assert.Equal(after, Snapshot(Directory.GetFiles(_directory, "*.json")));
    }

    [Fact]
    public void A_Damaged_Record_Declines_The_Migration_Instead_Of_Overwriting()
    {
        var store = new FileProfileStore(_directory);
        SeedLegacyStore(store, LegacySlotOne(), LegacySlotTwo());
        var markerPath = Path.Combine(_directory, "..", "legacy-migration.json");
        File.WriteAllText(markerPath, "this is not a migration record");
        var markers = new LegacyMigrationMarkerFile(markerPath);

        using var host = HostWith(store, markers, SdlId);
        host.RefreshPresence();

        // Damage proves nothing about what was processed, so nothing is copied - the safe
        // direction is to leave the user's configuration alone.
        var presets = new PresetStore(store);
        Assert.DoesNotContain(
            presets.LoadSlot(SdlId, 1)!.Mappings,
            m => m.Target is KeyPressTarget { Key: GamepadKey.F13 });
        var report = Assert.Single(host.LegacyMigrations);
        Assert.False(report.Migrated);
        Assert.Contains(report.Notes, note => note.Contains("记录无法读取"));

        // And a second run declines the same way rather than flipping its answer.
        using var restarted = HostWith(store, markers, SdlId);
        restarted.RefreshPresence();

        Assert.Contains(
            Assert.Single(restarted.LegacyMigrations).Notes,
            note => note.Contains("记录无法读取"));
        Assert.Equal("this is not a migration record", File.ReadAllText(markerPath));
    }

    [Fact]
    public void A_Null_Record_Body_Is_Damage_Not_An_Empty_Record()
    {
        var store = new FileProfileStore(_directory);
        SeedLegacyStore(store, LegacySlotOne(), LegacySlotTwo());
        var markerPath = Path.Combine(_directory, "..", "legacy-migration.json");
        File.WriteAllText(markerPath, "null");
        var markers = new LegacyMigrationMarkerFile(markerPath);

        // A JSON null deserializes without throwing, so it has to be refused explicitly:
        // reading it as "nothing processed" would let the migration repeat.
        Assert.Null(markers.Load());

        using var host = HostWith(store, markers, SdlId);
        host.RefreshPresence();

        var presets = new PresetStore(store);
        Assert.DoesNotContain(
            presets.LoadSlot(SdlId, 1)!.Mappings,
            m => m.Target is KeyPressTarget { Key: GamepadKey.F13 });
        Assert.Contains(
            Assert.Single(host.LegacyMigrations).Notes,
            note => note.Contains("记录无法读取"));
    }

    [Fact]
    public void An_Empty_Object_Record_Is_The_Empty_Record()
    {
        var markerPath = Path.Combine(_directory, "legacy-migration.json");
        File.WriteAllText(markerPath, "{}");
        var markers = new LegacyMigrationMarkerFile(markerPath);

        // The first run's shape: a record that names no processed pair yet.
        Assert.NotNull(markers.Load());
        Assert.Empty(markers.Load()!);
    }

    [Fact]
    public void The_Record_File_Lives_In_The_Configuration_Root_Not_The_Profiles_Directory()
    {
        var path = new LegacyMigrationMarkerFile().FilePath;

        // FileProfileStore enumerates the profiles directory, so a record inside it would be
        // read back as a device's profile.
        Assert.EndsWith(Path.Combine("DeziroJoy", LegacyMigrationMarkerFile.FileName), path);
        Assert.DoesNotContain("profiles", path);
    }

    [Fact]
    public void The_Daemon_State_Guard_Refuses_A_Default_Body_For_Any_Slot()
    {
        // The slot-2 shape a running daemon persists: the derived name, the slot key and the
        // slot tag the runtime carries.
        var baseDefault = DefaultProfileLibrary.CreateForLayout(GamepadLayout.Xbox);
        var slotTwoBody = baseDefault with
        {
            Name = $"{baseDefault.Name} · 预设 2",
            DeviceId = PresetStore.KeyFor(SdlId, 2),
            PresetSlot = 2,
        };

        var store = new InMemoryProfileStore();
        var host = new FakeDaemonHost();
        host.AddDevice(SdlId);
        var statePath = Path.Combine(_directory, "daemon-state-slot2.json");

        new DaemonStateStore(statePath).Save(new DaemonState(
            MappingEnabled: false,
            ProfileName: slotTwoBody.Name,
            ProfileJson: ProfileSerializer.Serialize(slotTwoBody),
            Revision: 0,
            ActivePresetSlot: 2));

        var server = new DaemonServer(host, store, new DaemonStateStore(statePath));
        server.ResumeDesiredState();

        // A default body is not a configuration, whichever slot it belongs to.
        Assert.Null(new PresetStore(store).LoadSlot(SdlId, 2));
    }

    [Fact]
    public void The_Daemon_State_Restore_Still_Writes_A_Real_Slot_Two_Configuration_Back()
    {
        var custom = new GamepadProfile
        {
            Name = "我的配置 · 预设 2",
            DeviceId = PresetStore.KeyFor(SdlId, 2),
            PresetSlot = 2,
            Mappings = [new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = new KeyPressTarget(GamepadKey.F13) }],
        };

        var store = new InMemoryProfileStore();
        var host = new FakeDaemonHost();
        host.AddDevice(SdlId);
        var statePath = Path.Combine(_directory, "daemon-state-slot2-custom.json");

        new DaemonStateStore(statePath).Save(new DaemonState(
            MappingEnabled: false,
            ProfileName: custom.Name,
            ProfileJson: ProfileSerializer.Serialize(custom),
            Revision: 0,
            ActivePresetSlot: 2));

        var server = new DaemonServer(host, store, new DaemonStateStore(statePath));
        server.ResumeDesiredState();

        Assert.Contains(
            new PresetStore(store).LoadSlot(SdlId, 2)!.Mappings,
            m => m.Target is KeyPressTarget { Key: GamepadKey.F13 });
    }

    /// <summary>
    /// Seeds the store with legacy files the way the version that used them left them:
    /// written under the old key, carrying an older schema version so the store's own
    /// forward migration runs on read.
    /// </summary>
    private List<string> SeedLegacyStore(FileProfileStore store, params GamepadProfile[] profiles)
    {
        foreach (var profile in profiles)
        {
            store.Save(profile);
        }

        var files = new List<string>(Directory.GetFiles(_directory, "*.json"));
        foreach (var file in files)
        {
            var body = ProfileSerializer.Deserialize(File.ReadAllText(file));
            File.WriteAllText(file, ProfileSerializer.Serialize(body with { SchemaVersion = 2 }));
        }

        return files;
    }

    private static GamepadProfile LegacySlotOne(
        string? deviceId = null,
        string? name = null,
        GamepadLayout layout = GamepadLayout.Generic) => new()
        {
            SchemaVersion = 2,
            Name = name is null ? "我的 evdev 配置" : $"我的 {name} 配置",
            DeviceId = deviceId ?? LegacyId,
            Layout = layout,
            Mappings =
        [
            new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = new KeyPressTarget(GamepadKey.F13) },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.B)
                {
                    Gesture = new ButtonGesture
                    {
                        LongPressThreshold = 0.25f,
                        LongPressAction = new ComplexShortcutTarget { Script = "hold F23\nevery 50ms tap F24" },
                    },
                },
                Target = new KeyPressTarget(GamepadKey.F14),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.LeftStick),
                Target = new SpecialKeyTarget(SpecialKeyKind.Precision, PrecisionMode.Hold),
            },
            // The v0.0.2 layer key and one mapping that lived on the second temporary layer:
            // the store's own migration turns the key into a layer leader and folds the
            // layer into the one that remains, so the copy has to carry both shapes.
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Back),
                Target = new SpecialKeyTarget(SpecialKeyKind.Layer, PrecisionMode.Toggle, LeaderSwitchMode.Hold),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.X),
                Target = new KeyPressTarget(GamepadKey.F15),
                Layer = 2,
            },
        ],
        };

    /// <summary>A legacy primary with no special keys: plain key mappings only.</summary>
    private static GamepadProfile LegacyPlainSlotOne() => new()
    {
        SchemaVersion = 2,
        Name = "我的 evdev 配置",
        DeviceId = LegacyId,
        Layout = GamepadLayout.Xbox,
        Mappings =
        [
            new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = new KeyPressTarget(GamepadKey.F13) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.Y), Target = new KeyPressTarget(GamepadKey.F14) },
        ],
    };

    private static GamepadProfile LegacySlotTwo() => new()
    {
        SchemaVersion = 2,
        Name = "我的 evdev 配置 · 预设 2",
        DeviceId = PresetStore.KeyFor(LegacyId, 2),
        Mappings = [new MappingEntry { Source = new ButtonBinding(GamepadButton.Y), Target = new KeyPressTarget(GamepadKey.F14) }],
    };

    private static GamepadProfile LegacySlotThree() => new()
    {
        SchemaVersion = 2,
        Name = "我的 evdev 配置 · 预设 3",
        DeviceId = PresetStore.KeyFor(LegacyId, 3),
        Mappings = [new MappingEntry { Source = new ButtonBinding(GamepadButton.X), Target = new KeyPressTarget(GamepadKey.F15) }],
    };

    private static GamepadProfile LegacySlotFour() => new()
    {
        SchemaVersion = 2,
        Name = "我的 evdev 配置 · 预设 4",
        DeviceId = PresetStore.KeyFor(LegacyId, 4),
        Mappings = [new MappingEntry { Source = new ButtonBinding(GamepadButton.DPadUp), Target = new KeyPressTarget(GamepadKey.F16) }],
    };

    private static readonly int[] ExpectedSlots = [1, 2];

    private static Dictionary<string, string> Snapshot(IEnumerable<string> files)
        => files.ToDictionary(file => Path.GetFileName(file)!, File.ReadAllText);
}
