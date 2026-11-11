using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

public class MainWindowViewModelTests
{
    private static (MainWindowViewModel Vm, EngineHost Host, FakeGamepadProvider Provider, InMemoryProfileStore Store) CreateHost()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var vm = new MainWindowViewModel(host, store);

        return (vm, host, provider, store);
    }

    [Fact]
    public void First_Sample_Shows_The_Controller_And_Its_Default_Profile()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        SampleHelper.Sample(vm, host);

        Assert.True(vm.HasController);
        Assert.NotNull(vm.ActiveController);
        Assert.Equal(GamepadLayout.Xbox, vm.ActiveController!.Descriptor.Layout);
        Assert.Equal(10, vm.Mappings.Count);
        Assert.Contains("Xbox", vm.StatusMessage);
    }

    [Fact]
    public void Add_Mapping_Appends_To_The_Profile_And_Applies_It()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.LeftBumper;
        vm.Editor.TargetKind = MappingTargetKind.KeyPress;
        vm.Editor.SelectedKey = GamepadKey.Space;
        vm.CommitEditorCommand.Execute(null);

        Assert.Equal(11, vm.Mappings.Count);
        Assert.Equal(11, vm.ActiveProfile!.Mappings.Count);
        Assert.Contains(vm.Mappings, row => row.TargetText.Contains("Space"));

        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.LeftBumper, 0, 0, 0, 0));
        SampleHelper.Sample(vm, host);

        Assert.True(vm.HasController);
    }

    [Fact]
    public void Edit_Mapping_Replaces_The_Selected_Row()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        var bRow = vm.Mappings.Single(row => row.SourceText.Contains('B'));
        vm.EditMappingCommand.Execute(bRow);
        vm.Editor.TargetKind = MappingTargetKind.KeyPress;
        vm.Editor.SelectedKey = GamepadKey.Space;
        vm.CommitEditorCommand.Execute(null);

        Assert.Equal(10, vm.Mappings.Count);
        Assert.DoesNotContain(vm.Mappings, row => row.TargetText.Contains("Enter"));
        Assert.Contains(vm.Mappings, row => row.TargetText.Contains("Space"));
    }

    [Fact]
    public void Delete_Mapping_Removes_The_Row()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        var row = vm.Mappings.Single(r => r.SourceText.Contains('A'));
        vm.DeleteMappingCommand.Execute(row);

        Assert.Equal(9, vm.Mappings.Count);
        Assert.DoesNotContain(vm.Mappings, r => r.SourceText.Contains('A'));
    }

    [Fact]
    public void Toggle_Mapping_Delegates_To_The_Host()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        vm.ToggleMappingCommand.Execute(null);

        Assert.False(host.IsMappingEnabled);
        Assert.False(vm.IsMappingEnabled);
    }

    [Fact]
    public void Save_Profile_Persists_To_The_Store()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        vm.SaveProfileCommand.Execute(null);

        Assert.NotNull(store.Load("xinput:0"));
    }

    [Fact]
    public async Task Save_Profile_Writes_Only_The_Active_Preset_Slot()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
        await vm.SwitchPresetSlotCommand.ExecuteAsync(2);
        var presets = new PresetStore(store);
        var untouched = new Dictionary<int, string>
        {
            [1] = ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", 1)!),
            [3] = ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", 3)!),
            [4] = ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", 4)!),
        };
        vm.ActiveProfile = vm.ActiveProfile! with { Name = "Custom slot two" };

        vm.SaveProfileCommand.Execute(null);

        Assert.Equal("Custom slot two", presets.LoadSlot("xinput:0", 2)!.Name);
        foreach (var (slot, expected) in untouched)
        {
            Assert.Equal(expected, ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", slot)!));
        }
    }

    [Fact]
    public async Task Committing_An_Edit_Persists_To_The_Slot_And_Survives_A_Switch_Round_Trip()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        // Commit an edit on slot 1 through the real editor path, with no explicit save.
        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.LeftBumper;
        vm.Editor.TargetKind = MappingTargetKind.KeyPress;
        vm.Editor.SelectedKey = GamepadKey.Space;
        vm.CommitEditorCommand.Execute(null);
        Assert.Equal(11, vm.Mappings.Count);

        // Leave the slot and come back: the committed edit must still be there.
        await vm.SwitchPresetSlotCommand.ExecuteAsync(2);
        Assert.Equal(10, vm.Mappings.Count);
        await vm.SwitchPresetSlotCommand.ExecuteAsync(1);

        Assert.Equal(11, vm.Mappings.Count);
        Assert.Contains(vm.Mappings, row => row.TargetText.Contains("Space"));

        var presets = new PresetStore(store);
        Assert.Equal(11, presets.LoadSlot("xinput:0", 1)!.Mappings.Count);
        Assert.Equal(10, presets.LoadSlot("xinput:0", 2)!.Mappings.Count);
    }

    [Fact]
    public async Task Deleting_A_Mapping_Persists_Only_To_The_Active_Slot()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
        var presets = new PresetStore(store);
        var slotTwoBefore = ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", 2)!);

        var row = vm.Mappings.Single(r => r.SourceText.Contains('A'));
        await vm.DeleteMappingCommand.ExecuteAsync(row);
        Assert.Equal(9, vm.Mappings.Count);

        await vm.SwitchPresetSlotCommand.ExecuteAsync(2);
        Assert.Equal(10, vm.Mappings.Count);
        Assert.Equal(slotTwoBefore, ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", 2)!));

        await vm.SwitchPresetSlotCommand.ExecuteAsync(1);
        Assert.Equal(9, vm.Mappings.Count);
        Assert.Equal(9, presets.LoadSlot("xinput:0", 1)!.Mappings.Count);
    }

    [Fact]
    public async Task Applying_Sensitivity_Writes_Only_The_Active_Slot()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
        await vm.SwitchPresetSlotCommand.ExecuteAsync(2);
        var presets = new PresetStore(store);
        var others = new Dictionary<int, string>
        {
            [1] = ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", 1)!),
            [3] = ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", 3)!),
            [4] = ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", 4)!),
        };

        vm.MouseSensitivity.MaxSpeed = 4321f;
        await vm.ApplyMouseSensitivityCommand.ExecuteAsync(null);

        var saved = Assert.IsType<MouseMoveTarget>(
            presets.LoadSlot("xinput:0", 2)!.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Right }).Target);
        Assert.Equal(4321f, saved.Settings.MaxSpeed);
        foreach (var (slot, expected) in others)
        {
            Assert.Equal(expected, ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", slot)!));
        }
    }

    [Fact]
    public async Task Reset_To_Default_Writes_Only_The_Active_Slot()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
        var row = vm.Mappings.Single(r => r.SourceText.Contains('A'));
        await vm.DeleteMappingCommand.ExecuteAsync(row);
        await vm.SwitchPresetSlotCommand.ExecuteAsync(2);
        var presets = new PresetStore(store);
        var slotOneAfterDelete = ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", 1)!);

        await vm.ResetToDefaultCommand.ExecuteAsync(null);

        Assert.Equal(10, vm.ActiveProfile!.Mappings.Count);
        Assert.Equal(10, presets.LoadSlot("xinput:0", 2)!.Mappings.Count);
        // Slot 1 keeps its customized 9-mapping state; the reset only touched slot 2.
        Assert.Equal(slotOneAfterDelete, ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", 1)!));
    }

    [Fact]
    public async Task Committing_A_Special_Key_Edit_Propagates_It_To_Every_Preset()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
        var presets = new PresetStore(store);
        var siblingMappingsBefore = new Dictionary<int, IReadOnlyList<Guid>>();
        foreach (var slot in new[] { 2, 3, 4 })
        {
            siblingMappingsBefore[slot] = presets.LoadSlot("xinput:0", slot)!.Mappings.Select(m => m.Id).ToList();
        }

        // Add a Leader key on the Back button through the real editor path, on slot 1.
        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.Back;
        vm.Editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        vm.Editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        await vm.CommitEditorCommand.ExecuteAsync(null);

        Assert.Equal(11, vm.Mappings.Count);

        // The special key is device-global: every slot carries it now.
        foreach (var slot in new[] { 1, 2, 3, 4 })
        {
            var leader = presets.LoadSlot("xinput:0", slot)!.Mappings
                .Single(m => m.Source is ButtonBinding { Button: GamepadButton.Back }).Target;
            var special = Assert.IsType<SpecialKeyTarget>(leader);
            Assert.Equal(SpecialKeyKind.Leader, special.Kind);
            Assert.Equal(1, special.SlotA);
            Assert.Equal(2, special.SlotB);
        }

        // Every other mapping of every sibling slot survived, id for id - the fan-out added
        // the shared key and touched nothing else.
        foreach (var (slot, ids) in siblingMappingsBefore)
        {
            var after = presets.LoadSlot("xinput:0", slot)!.Mappings.Select(m => m.Id).ToList();
            Assert.Equal(ids.Count + 1, after.Count);
            Assert.Equal(ids, after.Take(ids.Count));
        }
    }

    [Fact]
    public async Task Deleting_A_Special_Key_Mapping_Removes_It_From_Every_Preset()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.Back;
        vm.Editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        vm.Editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        await vm.CommitEditorCommand.ExecuteAsync(null);

        var presets = new PresetStore(store);
        var siblingMappings = presets.LoadSlot("xinput:0", 2)!.Mappings
            .Where(m => m.Target is not SpecialKeyTarget)
            .Select(m => m.Id)
            .ToArray();

        var leaderRow = vm.Mappings.Single(row => row.Entry.Target is SpecialKeyTarget);
        await vm.DeleteMappingCommand.ExecuteAsync(leaderRow);

        Assert.Equal(10, vm.Mappings.Count);
        foreach (var slot in new[] { 1, 2, 3, 4 })
        {
            Assert.DoesNotContain(
                presets.LoadSlot("xinput:0", slot)!.Mappings,
                m => m.Target is SpecialKeyTarget);
        }

        // The siblings' own mappings survived the removal, id for id.
        Assert.Equal(siblingMappings, presets.LoadSlot("xinput:0", 2)!.Mappings.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task A_Normal_Edit_Leaves_Sibling_Presets_Byte_Identical_While_A_Special_Key_Is_Shared()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.Back;
        vm.Editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        vm.Editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        await vm.CommitEditorCommand.ExecuteAsync(null);

        var presets = new PresetStore(store);
        var siblings = new Dictionary<int, string>();
        foreach (var slot in new[] { 2, 3, 4 })
        {
            siblings[slot] = ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", slot)!);
        }

        // A perfectly ordinary edit on the active slot must not rewrite the siblings: the
        // shared special-key subset is unchanged, so the fan-out is a no-op for them.
        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.LeftBumper;
        vm.Editor.TargetKind = MappingTargetKind.KeyPress;
        vm.Editor.SelectedKey = GamepadKey.Space;
        await vm.CommitEditorCommand.ExecuteAsync(null);

        Assert.Equal(12, vm.Mappings.Count);
        Assert.Equal(12, presets.LoadSlot("xinput:0", 1)!.Mappings.Count);
        foreach (var (slot, expected) in siblings)
        {
            Assert.Equal(expected, ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", slot)!));
        }
    }

    [Fact]
    public async Task Adding_A_Preset_Carries_The_Shared_Special_Keys_Into_The_New_Slot()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.Back;
        vm.Editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        vm.Editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        await vm.CommitEditorCommand.ExecuteAsync(null);

        await vm.AddPresetCommand.ExecuteAsync(null);

        var presets = new PresetStore(store);
        var added = presets.SlotsInUse("xinput:0").Max();
        var profile = presets.LoadSlot("xinput:0", added)!;
        Assert.Equal(5, added);
        Assert.Contains(profile.Mappings, m => m.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader });
        Assert.Contains(vm.PresetSlots, item => item.Slot == added);
    }

    [Fact]
    public async Task PresetSlots_Tracks_The_Connected_Device_And_The_Active_Slot()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        Assert.Equal([1, 2, 3, 4], vm.PresetSlots.Select(item => item.Slot));
        Assert.Equal(1, vm.PresetSlots.Single(item => item.IsActive).Slot);

        await vm.SwitchPresetSlotCommand.ExecuteAsync(3);

        Assert.Equal(3, vm.PresetSlots.Single(item => item.IsActive).Slot);
    }

    [Fact]
    public async Task AddPreset_Creates_An_Independent_Slot_That_Switches_And_Persists()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        // Give slot 1 a distinguishing edit so the copied slot is recognizable.
        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.LeftBumper;
        vm.Editor.TargetKind = MappingTargetKind.KeyPress;
        vm.Editor.SelectedKey = GamepadKey.Space;
        vm.CommitEditorCommand.Execute(null);
        Assert.Equal(4, vm.PresetSlots.Count);

        await vm.AddPresetCommand.ExecuteAsync(null);

        var presets = new PresetStore(store);
        Assert.Equal(5, vm.ActivePresetSlot);
        Assert.Equal(5, vm.PresetSlots.Count);
        Assert.Equal(5, vm.PresetSlots.Single(item => item.IsActive).Slot);

        // The new slot is an independent copy of the profile in view, stored on its own key.
        Assert.Equal(11, vm.ActiveProfile!.Mappings.Count);
        Assert.Equal(11, presets.LoadSlot("xinput:0", 5)!.Mappings.Count);
        Assert.Equal(11, presets.LoadSlot("xinput:0", 1)!.Mappings.Count);
        Assert.Equal(10, presets.LoadSlot("xinput:0", 2)!.Mappings.Count);

        // The leader slot options now offer the added slot.
        Assert.Contains(5, vm.Editor.SpecialSlotOptions);

        // It persists across a switch away and back, exactly like the initial four.
        await vm.SwitchPresetSlotCommand.ExecuteAsync(2);
        await vm.SwitchPresetSlotCommand.ExecuteAsync(5);
        Assert.Equal(11, vm.ActiveProfile!.Mappings.Count);
    }

    [Fact]
    public async Task DeletePreset_Removes_The_Slot_From_The_Store_And_The_Collection()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        // Add a fifth preset so the delete covers a slot past the initial four, then remove it.
        await vm.AddPresetCommand.ExecuteAsync(null);
        Assert.Equal(5, vm.PresetSlots.Count);
        var presets = new PresetStore(store);
        Assert.NotNull(presets.LoadSlot("xinput:0", 5));

        // Two presses: the first arms, the second deletes.
        await vm.DeletePresetCommand.ExecuteAsync(5);
        Assert.Equal(5, vm.PresetSlots.Count);
        await vm.DeletePresetCommand.ExecuteAsync(5);

        Assert.Equal([1, 2, 3, 4], vm.PresetSlots.Select(item => item.Slot));
        Assert.Null(presets.LoadSlot("xinput:0", 5));
        Assert.False(vm.IsTransferError);
    }

    [Fact]
    public async Task DeletePreset_Requires_A_Confirming_Second_Press()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        await vm.DeletePresetCommand.ExecuteAsync(2);

        // The first press only arms: the slot survives and exactly its card is flagged.
        Assert.Equal([1, 2, 3, 4], vm.PresetSlots.Select(item => item.Slot));
        Assert.True(vm.PresetSlots.Single(item => item.Slot == 2).IsDeleteArmed);
        Assert.Equal(1, vm.PresetSlots.Count(item => item.IsDeleteArmed));

        await vm.DeletePresetCommand.ExecuteAsync(2);

        Assert.Equal([1, 3, 4], vm.PresetSlots.Select(item => item.Slot));
        Assert.Equal(0, vm.PresetSlots.Count(item => item.IsDeleteArmed));
    }

    [Fact]
    public async Task Deleting_The_Active_Preset_Switches_To_A_Surviving_Slot()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
        await vm.SwitchPresetSlotCommand.ExecuteAsync(2);
        Assert.Equal(2, vm.ActivePresetSlot);

        await vm.DeletePresetCommand.ExecuteAsync(2);
        await vm.DeletePresetCommand.ExecuteAsync(2);

        // The active slot 2 is gone and the app fell back to a surviving slot (the primary).
        Assert.Equal(1, vm.ActivePresetSlot);
        Assert.Equal([1, 3, 4], vm.PresetSlots.Select(item => item.Slot));
        Assert.Equal(1, vm.PresetSlots.Single(item => item.IsActive).Slot);
        var presets = new PresetStore(store);
        Assert.Null(presets.LoadSlot("xinput:0", 2));
    }

    [Fact]
    public async Task DeletePreset_Refuses_The_Primary_Slot()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        await vm.DeletePresetCommand.ExecuteAsync(1);
        await vm.DeletePresetCommand.ExecuteAsync(1);

        Assert.True(vm.IsTransferError);
        Assert.Contains("主预设", vm.TransferMessage);
        Assert.Equal([1, 2, 3, 4], vm.PresetSlots.Select(item => item.Slot));
        Assert.NotNull(new PresetStore(store).LoadSlot("xinput:0", 1));
    }

    [Fact]
    public async Task DeletePreset_Is_Disarmed_By_A_Switch()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        await vm.DeletePresetCommand.ExecuteAsync(2);
        Assert.True(vm.PresetSlots.Single(item => item.Slot == 2).IsDeleteArmed);

        await vm.SwitchPresetSlotCommand.ExecuteAsync(3);

        Assert.Equal(0, vm.PresetSlots.Count(item => item.IsDeleteArmed));
    }

    [Fact]
    public async Task Deleting_A_NonActive_Preset_Reloads_The_Repaired_Active_Profile_Locally()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
        var presets = new PresetStore(store);

        // The active slot 1 carries a leader that flips to slot 3.
        presets.SaveSlot("xinput:0", 1, new GamepadProfile
        {
            Name = "主配置",
            DeviceId = "xinput:0",
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.Back),
                    Target = new SpecialKeyTarget(SpecialKeyKind.Leader, PrecisionMode.Toggle, LeaderSwitchMode.Press, SlotA: 1, SlotB: 3),
                    Enabled = true,
                },
            ],
        });

        await vm.DeletePresetCommand.ExecuteAsync(3);
        await vm.DeletePresetCommand.ExecuteAsync(3);

        // Slot 3 is gone, and the active profile's leader was repaired onto a survivor and
        // reloaded into the running engine and the in-view profile - not left stale.
        Assert.Equal([1, 2, 4], presets.SlotsInUse("xinput:0"));
        Assert.Equal(2, LeaderTarget(host.Controllers.Single().Profile)!.SlotB);
        Assert.Equal(2, LeaderTarget(vm.ActiveProfile!)!.SlotB);
    }

    [Fact]
    public async Task The_Gamepad_Can_Move_Along_The_Preset_Strip_And_Activate_Its_Delete()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
        var presets = new PresetStore(store);

        // The strip's gamepad focus order: each card, then its delete badge, then the add button.
        Assert.Equal(8, vm.PresetStripFocusCount);
        Assert.Equal((PresetStripAction.Switch, 1), vm.PresetStripActionAt(0));
        Assert.Equal((PresetStripAction.Delete, 3), vm.PresetStripActionAt(4));
        Assert.Equal((PresetStripAction.Add, 0), vm.PresetStripActionAt(7));

        // Park the gamepad focus on slot 3's delete badge, then drive it with the pad: the first
        // A arms the confirm (the press must not also switch or delete), the second A confirms.
        vm.PresetStripFocusIndex = 4;
        PressPad(vm, host, provider, GamepadButtonMask.A);
        Assert.Equal(4, vm.PresetSlots.Count);
        Assert.True(vm.PresetSlots.Single(item => item.Slot == 3).IsDeleteArmed);

        PressPad(vm, host, provider, GamepadButtonMask.A);
        Assert.Equal([1, 2, 4], vm.PresetSlots.Select(item => item.Slot));
        Assert.Null(presets.LoadSlot("xinput:0", 3));
    }

    [Fact]
    public void The_Gamepad_Dpad_Moves_The_Preset_Strip_Focus()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        vm.PresetStripFocusIndex = 0;
        PressPad(vm, host, provider, GamepadButtonMask.DPadRight);
        Assert.Equal(1, vm.PresetStripFocusIndex);
        PressPad(vm, host, provider, GamepadButtonMask.DPadRight);
        Assert.Equal(2, vm.PresetStripFocusIndex);
        PressPad(vm, host, provider, GamepadButtonMask.DPadLeft);
        Assert.Equal(1, vm.PresetStripFocusIndex);
    }

    [Fact]
    public async Task Deleting_A_Slot_The_Active_Controller_Is_Running_Moves_It_Even_When_Not_The_Active_Slot()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("pad-A", "Controller A");
        provider.Add("pad-B", "Controller B");
        provider.SetState("pad-A", GamepadState.Neutral);
        provider.SetState("pad-B", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
        var presets = new PresetStore(store);

        // A (the active controller) deletes its non-active slot 2.
        await vm.DeletePresetCommand.ExecuteAsync(2);
        await vm.DeletePresetCommand.ExecuteAsync(2);
        Assert.Equal([1, 3, 4], presets.SlotsInUse("pad-A"));

        // Switch to slot 3 (both run it), then to slot 2 - A lacks slot 2, so A stays on slot 3
        // while the app-wide slot moves to 2.
        await vm.SwitchPresetSlotCommand.ExecuteAsync(3);
        await vm.SwitchPresetSlotCommand.ExecuteAsync(2);
        Assert.Equal(2, vm.ActivePresetSlot);
        Assert.Equal(3, host.Controllers.Single(c => c.Descriptor.DeviceId == "pad-A").Profile.PresetSlot);

        // Delete slot 3 - the slot A is actually running, though it is not the app-wide slot.
        await vm.DeletePresetCommand.ExecuteAsync(3);
        await vm.DeletePresetCommand.ExecuteAsync(3);

        // A is moved to a survivor rather than left executing the just-deleted profile.
        Assert.Equal([1, 4], presets.SlotsInUse("pad-A"));
        Assert.Equal(1, host.Controllers.Single(c => c.Descriptor.DeviceId == "pad-A").Profile.PresetSlot);
        Assert.Equal(2, vm.ActivePresetSlot);
    }

    [Fact]
    public void The_Gamepad_Can_Enter_The_Preset_Strip_And_Reach_Delete_Without_External_Focus()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
        var presets = new PresetStore(store);

        // No strip control is focused. The pad enters the strip with Guide, parking on the active
        // card - no keyboard or pointer focus anywhere.
        Assert.Equal(-1, vm.PresetStripFocusIndex);
        PressPad(vm, host, provider, GamepadButtonMask.Guide);
        Assert.Equal((PresetStripAction.Switch, 1), vm.PresetStripActionAt(vm.PresetStripFocusIndex));

        // D-pad right twice lands on slot 2's delete badge (Switch1, Switch2, Delete2).
        PressPad(vm, host, provider, GamepadButtonMask.DPadRight);
        PressPad(vm, host, provider, GamepadButtonMask.DPadRight);
        Assert.Equal((PresetStripAction.Delete, 2), vm.PresetStripActionAt(vm.PresetStripFocusIndex));

        // A arms, A confirms - the delete happens purely by gamepad.
        PressPad(vm, host, provider, GamepadButtonMask.A);
        Assert.True(vm.PresetSlots.Single(item => item.Slot == 2).IsDeleteArmed);
        PressPad(vm, host, provider, GamepadButtonMask.A);
        Assert.Equal([1, 3, 4], vm.PresetSlots.Select(item => item.Slot));
        Assert.Null(presets.LoadSlot("xinput:0", 2));

        // Guide exits the strip, returning the pad to the gamepad model.
        PressPad(vm, host, provider, GamepadButtonMask.Guide);
        Assert.Equal(-1, vm.PresetStripFocusIndex);
    }

    private static void PressPad(MainWindowViewModel vm, EngineHost host, FakeGamepadProvider provider, GamepadButtonMask mask)
    {
        // Release first so the press is a fresh edge the pad navigation can see.
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
        provider.SetState("xinput:0", GamepadState.Create(mask, 0, 0, 0, 0));
        SampleHelper.Sample(vm, host);
    }

    private static SpecialKeyTarget? LeaderTarget(GamepadProfile profile)
        => profile.Mappings.Select(mapping => mapping.Target)
            .OfType<SpecialKeyTarget>()
            .FirstOrDefault(target => target.Kind == SpecialKeyKind.Leader);

    [Fact]
    public void Reset_To_Default_Restores_The_Built_In_Profile()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        var row = vm.Mappings.Single(r => r.SourceText.Contains('B'));
        vm.DeleteMappingCommand.Execute(row);
        Assert.Equal(9, vm.Mappings.Count);

        vm.ResetToDefaultCommand.Execute(null);

        Assert.Equal(10, vm.Mappings.Count);
    }

    [Fact]
    public void Sample_Updates_The_Visualizer()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.A | GamepadButtonMask.DPadUp, 0.5f, -0.5f, 0, 0));

        SampleHelper.Sample(vm, host);

        Assert.True(vm.Visualizer.IsAPressed);
        Assert.True(vm.Visualizer.IsDPadUpPressed);
        // The preview caps share the mapping path's radial deadzone, so this 0.7071 push
        // reports the ramped 0.4635 rather than the raw 0.5.
        Assert.Equal(0.4634517, vm.Visualizer.LeftStickX, 6);
        Assert.Equal(-0.4634517, vm.Visualizer.LeftStickY, 6);
    }

    [Fact]
    public void Sensitivity_Panel_Edits_The_Active_Mouse_Mapping()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        vm.MouseSensitivity.MaxSpeed = 3000f;
        vm.ApplyMouseSensitivityCommand.Execute(null);

        var target = Assert.IsType<MouseMoveTarget>(
            vm.ActiveProfile!.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Right }).Target);

        Assert.Equal(3000f, target.Settings.MaxSpeed);
    }

    [Fact]
    public void Applying_Sensitivity_Keeps_A_Custom_Precision_Button()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        // The panel loads the mapping's settings, the user changes only the speed, and applies.
        // The precision key is not touched, so it must survive the round-trip untouched.
        vm.MouseSensitivity.LoadFrom(new MouseStickSettings { PrecisionButton = GamepadButton.RightBumper });
        vm.MouseSensitivity.MaxSpeed = 3000f;
        vm.ApplyMouseSensitivityCommand.Execute(null);

        var target = Assert.IsType<MouseMoveTarget>(
            vm.ActiveProfile!.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Right }).Target);

        Assert.Equal(3000f, target.Settings.MaxSpeed);
        Assert.Equal(GamepadButton.RightBumper, target.Settings.PrecisionButton);
    }

    [Fact]
    public void The_Active_Preset_Slot_Starts_At_One()
    {
        var (vm, _, _, _) = CreateHost();

        Assert.Equal(1, vm.ActivePresetSlot);
    }

    [Fact]
    public async Task SwitchPresetSlot_Applies_The_Stored_Slot_Profile_Locally()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        var presets = new PresetStore(store);
        presets.SaveSlot("xinput:0", 2, new GamepadProfile
        {
            Name = "第二套",
            DeviceId = "xinput:0",
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.A),
                    Target = new KeyPressTarget(GamepadKey.Space),
                },
            ],
        });

        await vm.SwitchPresetSlotCommand.ExecuteAsync(2);

        Assert.Equal(2, vm.ActivePresetSlot);
        Assert.Equal("第二套", vm.ActiveProfile!.Name);
    }

    [Fact]
    public async Task Switching_A_Preset_Locally_Applies_To_Every_Connected_Controller()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.Add("xinput:1", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        provider.SetState("xinput:1", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        var presets = new PresetStore(store);
        presets.SaveSlot("xinput:0", 2, new GamepadProfile
        {
            Name = "第二套·甲",
            DeviceId = "xinput:0",
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.A),
                    Target = new KeyPressTarget(GamepadKey.Space),
                },
            ],
        });
        presets.SaveSlot("xinput:1", 2, new GamepadProfile
        {
            Name = "第二套·乙",
            DeviceId = "xinput:1",
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B),
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
            ],
        });

        await vm.SwitchPresetSlotCommand.ExecuteAsync(2);

        Assert.Equal(2, vm.ActivePresetSlot);
        Assert.Equal("第二套·甲", vm.ActiveProfile!.Name);

        // The slot is app-wide: the second pad is not the active one, yet it must have
        // been reloaded with its own stored profile for the slot too.
        var running = host.Controllers.ToDictionary(
            controller => controller.Descriptor.DeviceId,
            controller => controller.Profile.Name);
        Assert.Equal("第二套·甲", running["xinput:0"]);
        Assert.Equal("第二套·乙", running["xinput:1"]);
    }

    [Fact]
    public void A_Leader_Press_Through_The_Host_Switches_The_Active_Preset_Slot()
    {
        var (vm, host, provider, store) = CreateHost();
        var presets = new PresetStore(store);
        presets.SaveSlot("xinput:0", 1, new GamepadProfile
        {
            Name = "主配置",
            DeviceId = "xinput:0",
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
                        SlotA: 1,
                        SlotB: 2),
                },
            ],
        });
        presets.SaveSlot("xinput:0", 2, new GamepadProfile
        {
            Name = "第二套",
            DeviceId = "xinput:0",
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.A),
                    Target = new KeyPressTarget(GamepadKey.Space),
                },
            ],
        });
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        vm.SetGamepadNavigationEnabled(false);
        SampleHelper.Sample(vm, host);

        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.Back, 0, 0, 0, 0));
        SampleHelper.Sample(vm, host);

        Assert.Equal(2, vm.ActivePresetSlot);
        Assert.Equal("第二套", vm.ActiveProfile!.Name);
    }

    [Fact]
    public void Toggle_Engine_Starts_And_Stops_The_Host_Loop()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        vm.ToggleEngineCommand.Execute(null);
        Assert.True(host.IsRunning);
        Assert.True(vm.IsEngineRunning);

        vm.ToggleEngineCommand.Execute(null);
        Assert.False(host.IsRunning);
        Assert.False(vm.IsEngineRunning);
    }

    [Fact]
    public void Losing_The_Controller_Clears_The_Active_Profile()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        provider.Remove("xinput:0");
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(TimeSpan.FromSeconds(1.0 / 64.0));
        vm.SyncControllers();

        Assert.False(vm.HasController);
        Assert.Null(vm.ActiveProfile);
    }

    [Fact]
    public void Sync_Reports_A_Connected_Gamepad_While_The_Engine_Is_Stopped()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        vm.SyncControllers();

        Assert.True(vm.HasController);
        Assert.NotNull(vm.ActiveController);
        Assert.True(vm.Visualizer.IsConnected);
    }

    [Fact]
    public void Sync_Reports_A_Disconnect_While_The_Engine_Is_Stopped()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        // Establish the connected state through the engine tick path first.
        SampleHelper.Sample(vm, host);
        Assert.True(vm.HasController);

        provider.Remove("xinput:0");
        vm.SyncControllers();

        Assert.False(vm.HasController);
        Assert.Null(vm.ActiveController);
        Assert.False(vm.Visualizer.IsConnected);
    }

    [Fact]
    public void Stopped_With_No_Gamepad_Reports_Disconnected()
    {
        var (vm, host, provider, _) = CreateHost();

        vm.SyncControllers();

        Assert.False(vm.IsEngineRunning);
        Assert.False(vm.HasController);
        Assert.False(vm.Visualizer.IsConnected);
        Assert.Equal("未检测到手柄，请连接手柄", vm.StatusMessage);
    }

    [Fact]
    public void A_Gamepad_That_Disappears_Reports_Asleep_Not_Never_Connected()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        vm.SyncControllers();
        Assert.True(vm.HasController);

        provider.Remove("xinput:0");
        vm.SyncControllers();

        Assert.False(vm.HasController);
        Assert.Contains("已断开连接", vm.StatusMessage);
        Assert.Contains("休眠", vm.StatusMessage);
    }

    [Fact]
    public void Stopped_With_Gamepad_Reports_Connected_With_Mapping_Off()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        vm.SyncControllers();

        Assert.False(vm.IsEngineRunning);
        Assert.True(vm.HasController);
        Assert.True(vm.Visualizer.IsConnected);
        Assert.Contains("Xbox", vm.StatusMessage);
        Assert.Contains("映射未启动", vm.StatusMessage);
    }

    [Fact]
    public void Running_With_Gamepad_Reports_Live_Connected_State()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));

        SampleHelper.StartEngineManually(vm, host);
        SampleHelper.Sample(vm, host);

        Assert.True(vm.IsEngineRunning);
        Assert.True(vm.HasController);
        Assert.True(vm.Visualizer.IsConnected);
        Assert.True(vm.Visualizer.IsAPressed);
        Assert.Contains("Xbox", vm.StatusMessage);
        Assert.DoesNotContain("映射未启动", vm.StatusMessage);

        vm.ToggleEngineCommand.Execute(null);
    }

    [Fact]
    public void Running_With_Gamepad_Lost_Reports_Disconnected()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        SampleHelper.StartEngineManually(vm, host);
        SampleHelper.Sample(vm, host);
        Assert.True(vm.HasController);

        provider.Remove("xinput:0");
        for (var i = 0; i < EngineHost.DeviceAbsenceGraceTicks + 1; i++)
        {
            host.TickOnce(TimeSpan.FromSeconds(1.0 / 64.0));
        }

        vm.SyncControllers();

        Assert.True(vm.IsEngineRunning);
        Assert.False(vm.HasController);
        Assert.False(vm.Visualizer.IsConnected);
        Assert.Contains("已断开连接", vm.StatusMessage);

        vm.ToggleEngineCommand.Execute(null);
    }

    [Fact]
    public void Starting_The_Engine_Keeps_The_Already_Detected_Gamepad()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        vm.SyncControllers();
        var detected = vm.ActiveController;
        Assert.True(vm.HasController);

        vm.ToggleEngineCommand.Execute(null);
        vm.SyncControllers();

        Assert.Same(detected, vm.ActiveController);
        Assert.Single(vm.Controllers);
        Assert.True(vm.Visualizer.IsConnected);
        Assert.DoesNotContain("映射未启动", vm.StatusMessage);

        vm.ToggleEngineCommand.Execute(null);
    }

    [Fact]
    public void Reset_To_Default_Reloads_The_Sensitivity_Panels_From_The_New_Profile()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        vm.MouseSensitivity.MaxSpeed = 5000f;
        vm.ApplyMouseSensitivityCommand.Execute(null);
        Assert.Equal(5000f, vm.MouseSensitivity.MaxSpeed);

        vm.ResetToDefaultCommand.Execute(null);

        var mouseTarget = Assert.IsType<MouseMoveTarget>(
            vm.ActiveProfile!.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Right }).Target);
        Assert.Equal(mouseTarget.Settings.MaxSpeed, vm.MouseSensitivity.MaxSpeed);
        Assert.NotEqual(5000f, vm.MouseSensitivity.MaxSpeed);
    }

    [Fact]
    public async Task Import_From_Writes_Only_The_Active_Preset_Slot()
    {
        var (vm, host, provider, store) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
        await vm.SwitchPresetSlotCommand.ExecuteAsync(3);
        var presets = new PresetStore(store);
        var untouched = new Dictionary<int, string>
        {
            [1] = ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", 1)!),
            [2] = ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", 2)!),
            [4] = ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", 4)!),
        };
        var path = Path.Combine(Path.GetTempPath(), $"dezirojoy-import-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, ProfileSerializer.Serialize(new GamepadProfile
        {
            Name = "Imported slot three",
            DeviceId = "another-device",
            Mappings = [],
        }));

        try
        {
            vm.ImportFrom(path);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal("Imported slot three", presets.LoadSlot("xinput:0", 3)!.Name);
        foreach (var (slot, expected) in untouched)
        {
            Assert.Equal(expected, ProfileSerializer.Serialize(presets.LoadSlot("xinput:0", slot)!));
        }
    }

    [Fact]
    public void Import_From_Reloads_The_Sensitivity_Panels_From_The_Imported_Profile()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        var path = Path.Combine(Path.GetTempPath(), $"dezirojoy-import-{Guid.NewGuid():N}.json");
        var base_ = vm.ActiveProfile!;
        var imported = base_ with
        {
            Mappings = base_.Mappings
                .Select(m => m is { Source: StickBinding { Stick: GamepadStick.Right }, Target: MouseMoveTarget mouse }
                    ? m with { Target = new MouseMoveTarget(mouse.Settings with { MaxSpeed = 1234f }) }
                    : m)
                .ToList(),
        };
        File.WriteAllText(path, ProfileSerializer.Serialize(imported));

        try
        {
            vm.ImportFrom(path);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal(1234f, vm.MouseSensitivity.MaxSpeed);
    }

    // ---- Row gesture summaries -------------------------------------------
    //
    // A row is the complete summary of the three gestures a mapping carries (DESIGN 4.3.8):
    // the click is the mapping's own target, the double-click and the long-press are the
    // two gestures a button can carry on top of it. 已关闭, 无功能 and the legacy
    // repeat-click are three different states and must never read the same.

    private static MappingRowViewModel RowOf(MappingEntry entry)
        => new(entry, _ => { }, _ => { }, (_, _) => { });

    [Fact]
    public void A_Row_Summarizes_The_Click_As_Its_Target_With_Both_Gestures_Off()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        // B carries the default B -> Enter mapping and no gestures at all.
        var row = vm.Mappings.Single(r => r.Entry.Source == new ButtonBinding(GamepadButton.B));

        Assert.Equal("Enter", row.ClickText);
        Assert.Equal("已关闭", row.DoubleClickText);
        Assert.Equal("已关闭", row.LongPressText);
    }

    [Fact]
    public void A_Stick_Row_Summarizes_Its_Target_With_No_Gesture_Lines()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        // Gestures live on the button binding, so a stick row has none to summarize.
        var row = vm.Mappings.Single(r => r.Entry.Source is StickBinding { Stick: GamepadStick.Left });

        Assert.Equal("滚轮滚动", row.ClickText);
        Assert.Equal("已关闭", row.DoubleClickText);
        Assert.Equal("已关闭", row.LongPressText);
    }

    [Fact]
    public void A_Legacy_Gesture_Row_Shows_Repeat_Click_On_Both_Gesture_Lines()
    {
        // The shape every profile written before gesture actions existed has: the windows
        // are set, both actions are null, so each gesture re-fires the click.
        var row = RowOf(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.B)
            {
                Gesture = new ButtonGesture { DoubleClickWindow = 0.25f, LongPressThreshold = 0.50f },
            },
            Target = new KeyPressTarget(GamepadKey.Enter),
        });

        Assert.Equal("Enter", row.ClickText);
        Assert.Equal("重复单击的功能", row.DoubleClickText);
        Assert.Equal("重复单击的功能", row.LongPressText);
    }

    [Fact]
    public void A_Disabled_Mapping_Row_Shows_Closed_On_Its_Click_Line()
    {
        var row = RowOf(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.B),
            Target = new KeyPressTarget(GamepadKey.Enter),
            Enabled = false,
        });

        Assert.Equal("已关闭", row.ClickText);
    }

    [Fact]
    public void A_None_Target_Click_Line_Shows_No_Function()
    {
        var row = RowOf(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.B),
            Target = new NoneTarget(),
        });

        Assert.Equal("无功能", row.ClickText);
    }

    [Fact]
    public void Gesture_Lines_Distinguish_An_Own_Target_From_No_Function_And_Off()
    {
        var row = RowOf(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.A)
            {
                Gesture = new ButtonGesture
                {
                    DoubleClickWindow = 0.25f,
                    DoubleClickAction = new KeyPressTarget(GamepadKey.Space),
                    LongPressThreshold = 0.50f,
                    LongPressAction = new NoneTarget(),
                },
            },
            Target = new KeyPressTarget(GamepadKey.Escape),
        });

        Assert.Equal("Esc", row.ClickText);
        Assert.Equal("Space", row.DoubleClickText);
        Assert.Equal("无功能", row.LongPressText);
    }

    [Fact]
    public void Click_Repeat_Closes_The_Long_Press_Line()
    {
        // Repeat and the long-press are mutually exclusive and the repeat wins, so the row
        // must not advertise a long-press that never fires.
        var row = RowOf(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.X)
            {
                Gesture = new ButtonGesture
                {
                    ClickRepeatInterval = 0.10f,
                    LongPressThreshold = 0.50f,
                },
            },
            Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C),
        });

        Assert.Equal("Ctrl + C", row.ClickText);
        Assert.Equal("已关闭", row.LongPressText);
    }

    [Fact]
    public void A_Complex_Shortcut_Row_Names_The_Program_On_Its_Long_Press_Line()
    {
        // The program is the long press's own action, so the click line names the click's
        // own target and the long-press line names the program - not the null action that
        // would read as the legacy 重复单击的功能.
        var row = RowOf(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.LeftBumper)
            {
                Gesture = new ButtonGesture
                {
                    LongPressThreshold = 0.50f,
                    LongPressAction = new ComplexShortcutTarget { Script = "hold Alt\nevery 1s tap Tab" },
                },
            },
            Target = new NoneTarget(),
        });

        Assert.Equal("无功能", row.ClickText);
        Assert.Equal("复杂快捷键：持续按住 Alt；每 1 秒点按 Tab", row.LongPressText);
        Assert.Equal("已关闭", row.DoubleClickText);
    }

    [Fact]
    public void A_v1_Complex_Shortcut_Row_Names_The_Program_On_Both_Lines()
    {
        // A profile written before the program became the long press's own action carries
        // it as the mapping's own target, so the click line names the program too - that is
        // what the mapping's target is - and the long-press line still names it.
        var row = RowOf(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.LeftBumper)
            {
                Gesture = new ButtonGesture { LongPressThreshold = 0.50f },
            },
            Target = new ComplexShortcutTarget { Script = "hold Alt\nevery 1s tap Tab" },
        });

        Assert.Equal("复杂快捷键：持续按住 Alt；每 1 秒点按 Tab", row.ClickText);
        Assert.Equal("复杂快捷键：持续按住 Alt；每 1 秒点按 Tab", row.LongPressText);
        Assert.Equal("已关闭", row.DoubleClickText);
    }

    [Fact]
    public void A_Complex_Shortcut_Without_A_Long_Press_Keeps_Its_Long_Press_Line_Closed()
    {
        // A hand-edited profile that pairs the program with click auto-repeat (or with no
        // long press at all) is one the engine refuses; the row must not advertise a
        // long-press that cannot start the program.
        var row = RowOf(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.LeftBumper)
            {
                Gesture = new ButtonGesture
                {
                    ClickRepeatInterval = 0.10f,
                    LongPressThreshold = 0.50f,
                },
            },
            Target = new ComplexShortcutTarget { Script = "hold Alt" },
        });

        Assert.Equal("复杂快捷键：持续按住 Alt", row.ClickText);
        Assert.Equal("已关闭", row.LongPressText);
    }

    // ---- The legacy leader key is migrated into the preset slots -------------

    [Fact]
    public async Task A_Legacy_Leader_Button_Is_Migrated_Into_Preset_Slot_Two_And_The_Settings_Cleared()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dezirojoy-leader-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            // An old settings.json still carries the retired leader pair; it deserializes
            // even though new saves never write it. The pair is stored numerically, the way
            // the settings serializer has always written enums.
            File.WriteAllText(
                Path.Combine(directory, "settings.json"),
                """{"Theme":"Dark","LeaderButton":10,"LeaderMode":1}""");

            var provider = new FakeGamepadProvider();
            var sink = new RecordingOutputSink();
            var store = new InMemoryProfileStore();
            var host = new EngineHost(provider, sink, store);
            var settings = new ThemeService(directory);
            settings.Load();

            store.Save(new GamepadProfile
            {
                Name = "第二套",
                DeviceId = "leader-alternate:xinput:0",
                Layout = GamepadLayout.Xbox,
                Mappings =
                [
                    new MappingEntry
                    {
                        Source = new ButtonBinding(GamepadButton.A),
                        Target = new KeyPressTarget(GamepadKey.Space),
                    },
                ],
            });

            var vm = new MainWindowViewModel(host, store, settings: settings);
            await using var _ = vm;

            provider.Add("xinput:0", "Xbox Wireless Controller");
            provider.SetState("xinput:0", GamepadState.Neutral);
            SampleHelper.Sample(vm, host);

            var presets = new PresetStore(store);
            var slotTwo = presets.LoadSlot("xinput:0", 2);

            Assert.NotNull(slotTwo);
            Assert.Equal("第二套", slotTwo!.Name);
            Assert.Contains(slotTwo.Mappings, mapping => mapping.Target is KeyPressTarget { Key: GamepadKey.Space });

            Assert.Single(
                slotTwo.Mappings,
                mapping => mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader });
            var leader = slotTwo.Mappings.First(
                mapping => mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader });
            var target = Assert.IsType<SpecialKeyTarget>(leader.Target);

            Assert.Equal(new ButtonBinding(GamepadButton.Guide), leader.Source);
            Assert.Equal(LeaderSwitchMode.Hold, target.SwitchMode);
            Assert.Equal(1, target.SlotA);
            Assert.Equal(2, target.SlotB);

            // The legacy entry is consumed, so a fresh read of the same file agrees.
            var reloaded = new ThemeService(directory);
            reloaded.Load();

            Assert.Null(reloaded.LegacyLeaderButton);

            // A second construction migrates nothing: the slot keeps exactly one leader
            // entry rather than gaining a duplicate.
            var secondHost = new EngineHost(provider, sink, store);
            var second = new MainWindowViewModel(secondHost, store, settings: reloaded);
            await using var __ = second;

            provider.SetState("xinput:0", GamepadState.Neutral);
            SampleHelper.Sample(second, secondHost);

            var slotTwoAgain = presets.LoadSlot("xinput:0", 2);

            Assert.Single(
                slotTwoAgain!.Mappings,
                mapping => mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Tray_Preferences_Default_True_And_Expose_Both_Binding_Names()
    {
        var (vm, _, _, _) = CreateHost();

        Assert.True(vm.CloseToTrayEnabled);
        Assert.True(vm.MinimizeToTrayEnabled);
        vm.CloseToTray = false;
        vm.MinimizeToTray = false;

        Assert.False(vm.CloseToTrayEnabled);
        Assert.False(vm.MinimizeToTrayEnabled);
    }

    [Fact]
    public async Task The_Migrated_Leader_Reaches_The_Running_Engine_And_Survives_The_Next_Edit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dezirojoy-leader-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(
                Path.Combine(directory, "settings.json"),
                """{"Theme":"Dark","LeaderButton":10,"LeaderMode":1}""");

            var provider = new FakeGamepadProvider();
            var sink = new RecordingOutputSink();
            var store = new InMemoryProfileStore();
            var host = new EngineHost(provider, sink, store);
            var settings = new ThemeService(directory);
            settings.Load();

            var vm = new MainWindowViewModel(host, store, settings: settings);
            await using var _ = vm;

            provider.Add("xinput:0", "Xbox Wireless Controller");
            provider.SetState("xinput:0", GamepadState.Neutral);
            SampleHelper.Sample(vm, host);

            // The migration converged every slot, and the runtime was reloaded with the
            // converged profile - so the leader is live on slot 1 rather than only stored.
            var presets = new PresetStore(store);
            Assert.Contains(
                vm.ActiveProfile!.Mappings,
                mapping => mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader });
            Assert.Contains(
                host.Controllers.Single().Profile.Mappings,
                mapping => mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader });
            foreach (var slot in new[] { 1, 2, 3, 4 })
            {
                Assert.Contains(
                    presets.LoadSlot("xinput:0", slot)!.Mappings,
                    mapping => mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader });
            }

            // The next ordinary edit commits the migrated profile, so the shared keys are not
            // fanned back out of the other slots by a pre-migration draft.
            vm.AddMappingCommand.Execute(null);
            vm.Editor.SelectedButton = GamepadButton.LeftBumper;
            vm.Editor.TargetKind = MappingTargetKind.KeyPress;
            vm.Editor.SelectedKey = GamepadKey.Space;
            await vm.CommitEditorCommand.ExecuteAsync(null);

            foreach (var slot in new[] { 1, 2, 3, 4 })
            {
                Assert.Contains(
                    presets.LoadSlot("xinput:0", slot)!.Mappings,
                    mapping => mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader });
            }

            // The ordinary edit itself stays in the active slot, exactly as it always has.
            Assert.Contains(
                presets.LoadSlot("xinput:0", 1)!.Mappings,
                mapping => mapping.Target is KeyPressTarget { Key: GamepadKey.Space });
            Assert.DoesNotContain(
                presets.LoadSlot("xinput:0", 2)!.Mappings,
                mapping => mapping.Target is KeyPressTarget { Key: GamepadKey.Space });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Without_A_Legacy_Leader_Button_Default_Slots_Contain_No_Leader_Mapping()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dezirojoy-leader-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var provider = new FakeGamepadProvider();
            var sink = new RecordingOutputSink();
            var store = new InMemoryProfileStore();
            var host = new EngineHost(provider, sink, store);
            var settings = new ThemeService(directory);
            settings.Load();

            var vm = new MainWindowViewModel(host, store, settings: settings);
            await using var _ = vm;

            provider.Add("xinput:0", "Xbox Wireless Controller");
            provider.SetState("xinput:0", GamepadState.Neutral);
            SampleHelper.Sample(vm, host);

            var slotTwo = new PresetStore(store).LoadSlot("xinput:0", 2);
            Assert.NotNull(slotTwo);
            Assert.DoesNotContain(slotTwo.Mappings, mapping => mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- 复杂快捷键: a DSL program committed from the editor -------------------

    [Fact]
    public async Task A_Committed_Complex_Shortcut_Persists_Reopens_And_Runs_Through_The_Host()
    {
        // The user's own example, end to end: authored on the long-press tab, committed,
        // persisted across a preset switch, reopened, and finally executed by the engine -
        // holding the button holds Alt and taps Tab once a second, releasing lifts Alt.
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var vm = new MainWindowViewModel(host, store);

        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        vm.SetGamepadNavigationEnabled(false);
        SampleHelper.Sample(vm, host);

        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.LeftBumper;
        vm.Editor.SelectedGestureKind = GestureKind.LongPress;
        vm.Editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;
        vm.Editor.ActiveComplexShortcutScript = "hold Alt\nevery 1s tap Tab";
        vm.CommitEditorCommand.Execute(null);

        Assert.False(vm.IsEditing);
        var committed = vm.ActiveProfile!.Mappings.Single(
            mapping => mapping.Source is ButtonBinding { Button: GamepadButton.LeftBumper });
        var committedGesture = Assert.IsType<ButtonGesture>(
            Assert.IsType<ButtonBinding>(committed.Source).Gesture);
        Assert.Equal(
            new ComplexShortcutTarget { Script = "hold Alt\nevery 1s tap Tab" },
            committedGesture.LongPressAction);

        // The mapping row reads the program in plain language, never the record's ToString:
        // the click line names the click's own target and the long-press line names the
        // program it starts.
        var row = vm.Mappings.Single(r => r.Entry == committed);
        Assert.Equal("Enter", row.ClickText);
        Assert.Equal("复杂快捷键：持续按住 Alt；每 1 秒点按 Tab", row.LongPressText);
        Assert.DoesNotContain("ComplexShortcutTarget", row.TargetText);

        // It survives the store: leave the slot, come back, reopen the mapping.
        await vm.SwitchPresetSlotCommand.ExecuteAsync(2);
        Assert.DoesNotContain(
            vm.Mappings,
            r => r.Entry.Source is ButtonBinding { Gesture.LongPressAction: ComplexShortcutTarget });
        await vm.SwitchPresetSlotCommand.ExecuteAsync(1);

        var reopened = vm.Mappings.Single(
            r => r.Entry.Source is ButtonBinding { Gesture.LongPressAction: ComplexShortcutTarget });
        vm.EditMappingCommand.Execute(reopened);
        vm.Editor.SelectedGestureKind = GestureKind.LongPress;
        Assert.Equal("hold Alt\nevery 1s tap Tab", vm.Editor.ActiveComplexShortcutScript);
        vm.CancelEditorCommand.Execute(null);

        // And the engine runs it: holding past the long-press threshold starts the program,
        // another second of holding taps Tab, and the release lifts Alt.
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.LeftBumper, 0, 0, 0, 0));
        for (var tick = 0; tick < 40; tick++)
        {
            SampleHelper.Sample(vm, host);
        }

        Assert.Contains("down:LeftAlt", sink.Calls);
        Assert.DoesNotContain(sink.Calls, call => call.StartsWith("up:LeftAlt", StringComparison.Ordinal));

        for (var tick = 0; tick < 64; tick++)
        {
            SampleHelper.Sample(vm, host);
        }

        Assert.Contains("down:Tab", sink.Calls);

        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        Assert.Contains("up:LeftAlt", sink.Calls);
    }

    [Fact]
    public void An_Unparseable_Script_Blocks_The_Commit_And_Leaves_The_Profile_Untouched()
    {
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.RightBumper;
        vm.Editor.SelectedGestureKind = GestureKind.LongPress;
        vm.Editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;
        vm.Editor.ActiveComplexShortcutScript = "hold Alt\nfrobnicate";
        var mappings = vm.ActiveProfile!.Mappings.Count;

        vm.CommitEditorCommand.Execute(null);

        // The editor stays open on the offending script and the active profile is untouched:
        // a program the engine cannot build never reaches the store or the running engine.
        Assert.True(vm.IsEditing);
        Assert.Equal(mappings, vm.ActiveProfile.Mappings.Count);
        Assert.DoesNotContain(
            vm.Mappings,
            row => row.Entry.Source is ButtonBinding { Gesture.LongPressAction: ComplexShortcutTarget });
        Assert.Contains("第 2 行", vm.Editor.ComplexShortcutScriptError);

        // Cancelling drops the draft entirely.
        vm.CancelEditorCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.Equal(mappings, vm.ActiveProfile.Mappings.Count);
    }

    [Fact]
    public void Reopening_An_Ordinary_Mapping_After_A_Complex_Shortcut_Keeps_Its_Double_Click()
    {
        // The editor is reused: EditMapping loads into the same instance without a reset, so
        // a draft left running a complex shortcut must not gate the incoming mapping's flags
        // - the guards that refuse a double-click belong to interactive edits, not to a load
        // that replaces the draft wholesale.
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        // A complex shortcut on the long press, committed through the real editor path.
        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.LeftBumper;
        vm.Editor.SelectedGestureKind = GestureKind.LongPress;
        vm.Editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;
        vm.CommitEditorCommand.Execute(null);
        var complexRow = vm.Mappings.Single(
            row => row.Entry.Source is ButtonBinding { Gesture.LongPressAction: ComplexShortcutTarget });

        // An ordinary double-click mapping, committed the same way.
        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.RightBumper;
        vm.Editor.SelectedGestureKind = GestureKind.DoubleClick;
        vm.Editor.ActiveGestureEnabled = true;
        vm.Editor.ActiveTargetKind = MappingTargetKind.KeyPress;
        vm.Editor.ActiveSelectedKey = GamepadKey.Space;
        vm.CommitEditorCommand.Execute(null);
        var doubleClickRow = vm.Mappings.Single(
            row => row.Entry.Source is ButtonBinding { Button: GamepadButton.RightBumper });

        // Reopen the complex mapping, then the ordinary one: the incoming gesture must load
        // exactly as it was committed, and committing it back must not rewrite it.
        vm.EditMappingCommand.Execute(complexRow);
        Assert.True(vm.Editor.LongPressRunsComplexShortcut);

        vm.EditMappingCommand.Execute(doubleClickRow);

        Assert.False(vm.Editor.LongPressRunsComplexShortcut);
        Assert.True(vm.Editor.DoubleClickEnabled);
        Assert.False(vm.Editor.LongPressEnabled);
        Assert.Equal(doubleClickRow.Entry.Source, vm.Editor.BuildEntry().Source);
        Assert.Equal(doubleClickRow.Entry.Target, vm.Editor.BuildEntry().Target);

        vm.CommitEditorCommand.Execute(null);

        var rewritten = vm.ActiveProfile!.Mappings.Single(
            mapping => mapping.Source is ButtonBinding { Button: GamepadButton.RightBumper });
        Assert.Equal(doubleClickRow.Entry.Source, rewritten.Source);
        Assert.Equal(doubleClickRow.Entry.Target, rewritten.Target);
    }

    [Fact]
    public void Reopening_A_No_Gesture_Mapping_After_A_Complex_Shortcut_Adds_No_Long_Press()
    {
        // The same reuse path with an ordinary mapping that carries no gesture at all: the
        // stale program must not leave a bare long press behind, because a long press that
        // cannot fire would silently change what the mapping does.
        var (vm, host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.LeftBumper;
        vm.Editor.SelectedGestureKind = GestureKind.LongPress;
        vm.Editor.ActiveTargetKind = MappingTargetKind.ComplexShortcut;
        vm.CommitEditorCommand.Execute(null);
        var complexRow = vm.Mappings.Single(
            row => row.Entry.Source is ButtonBinding { Gesture.LongPressAction: ComplexShortcutTarget });

        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.RightBumper;
        vm.Editor.TargetKind = MappingTargetKind.KeyPress;
        vm.Editor.SelectedKey = GamepadKey.Space;
        vm.CommitEditorCommand.Execute(null);
        var plainRow = vm.Mappings.Single(
            row => row.Entry.Source is ButtonBinding { Button: GamepadButton.RightBumper });
        Assert.Null(Assert.IsType<ButtonBinding>(plainRow.Entry.Source).Gesture);

        vm.EditMappingCommand.Execute(complexRow);
        Assert.True(vm.Editor.LongPressRunsComplexShortcut);

        vm.EditMappingCommand.Execute(plainRow);

        Assert.False(vm.Editor.LongPressRunsComplexShortcut);
        Assert.False(vm.Editor.LongPressEnabled);
        Assert.False(vm.Editor.DoubleClickEnabled);
        Assert.False(vm.Editor.ClickRepeatEnabled);
        Assert.Equal(plainRow.Entry.Source, vm.Editor.BuildEntry().Source);
        Assert.Equal(plainRow.Entry.Target, vm.Editor.BuildEntry().Target);

        vm.CommitEditorCommand.Execute(null);

        var rewritten = vm.ActiveProfile!.Mappings.Single(
            mapping => mapping.Source is ButtonBinding { Button: GamepadButton.RightBumper });
        Assert.Equal(plainRow.Entry.Source, rewritten.Source);
        Assert.Equal(plainRow.Entry.Target, rewritten.Target);
    }
}
