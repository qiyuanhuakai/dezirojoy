using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The 全局特殊键 area at the top of the 按键配置 page: the 精准键 and Leader 键 lead the
/// page because they are device-level configuration - every preset of the device shares
/// them - so they are listed by kind instead of hunted for inside one preset's rows. The
/// area reports and navigates; editing stays in the mapping editor, reached through the
/// row's 编辑 affordance. Every refresh path that re-lists the mappings re-lists these
/// too: a commit, a preset switch, a shared-key fan-out.
/// </summary>
public class MainWindowViewModelSpecialKeysTests
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

    private static void Connect(MainWindowViewModel vm, EngineHost host, FakeGamepadProvider provider)
    {
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);
    }

    private static GlobalSpecialKeyItem Precision(MainWindowViewModel vm) => vm.SpecialKeys[0];

    private static GlobalSpecialKeyItem Leader(MainWindowViewModel vm) => vm.SpecialKeys[1];

    /// <summary>Arms a fresh special-key mapping on one button and commits it.</summary>
    private static async Task CommitSpecialKey(
        MainWindowViewModel vm,
        GamepadButton button,
        SpecialKeyKind kind,
        LeaderSwitchMode switchMode = LeaderSwitchMode.Press,
        int slotA = 1,
        int slotB = 2,
        PrecisionMode precisionMode = PrecisionMode.Toggle)
    {
        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = button;
        vm.Editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        vm.Editor.ActiveSpecialKind = kind;
        if (kind == SpecialKeyKind.Leader)
        {
            vm.Editor.ActiveSpecialSwitchMode = switchMode;
            vm.Editor.ActiveSpecialSlotA = slotA;
            vm.Editor.ActiveSpecialSlotB = slotB;
        }
        else
        {
            vm.Editor.ActiveSpecialPrecisionMode = precisionMode;
        }

        await vm.CommitEditorCommand.ExecuteAsync(null);
    }

    [Fact]
    public void The_Area_Lists_Precision_First_Then_Leader()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(vm, host, provider);

        Assert.Equal(2, vm.SpecialKeys.Count);
        Assert.Equal("精准键", vm.SpecialKeys[0].Title);
        Assert.Equal(SpecialKeyKind.Precision, vm.SpecialKeys[0].Kind);
        Assert.Equal("Leader 键", vm.SpecialKeys[1].Title);
        Assert.Equal(SpecialKeyKind.Leader, vm.SpecialKeys[1].Kind);
    }

    [Fact]
    public void An_Unconfigured_Kind_Says_So_And_How_To_Configure_One()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(vm, host, provider);

        foreach (var item in vm.SpecialKeys)
        {
            Assert.False(item.IsConfigured);
            Assert.Null(item.MappingId);
            Assert.Equal("未配置", item.Summary);
            Assert.Contains("特殊键", item.Guide);
            Assert.Contains(item.Title, item.Guide);
            Assert.False(item.HasConflicts);
            Assert.Null(item.ConflictHint);
        }
    }

    [Fact]
    public async Task A_Committed_Leader_Key_Names_Its_Button_Mode_And_Slots()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(vm, host, provider);

        await CommitSpecialKey(vm, GamepadButton.Back, SpecialKeyKind.Leader, LeaderSwitchMode.Hold, 1, 3);

        var leader = Leader(vm);
        Assert.True(leader.IsConfigured);
        Assert.NotNull(leader.MappingId);
        Assert.Contains("Back 键", leader.Summary);
        Assert.Contains("按住切换", leader.Summary);
        Assert.Contains("预设 1 ↔ 3", leader.Summary);
        Assert.False(Precision(vm).IsConfigured);
        Assert.False(leader.HasConflicts);
    }

    [Fact]
    public async Task A_Committed_Precision_Key_Names_Its_Button_And_Trigger_Mode()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(vm, host, provider);

        await CommitSpecialKey(vm, GamepadButton.LeftStick, SpecialKeyKind.Precision, precisionMode: PrecisionMode.Hold);

        var precision = Precision(vm);
        Assert.True(precision.IsConfigured);
        Assert.Contains("左摇杆按下", precision.Summary);
        Assert.Contains("一直按住才触发", precision.Summary);
        Assert.False(precision.HasConflicts);
    }

    [Fact]
    public async Task A_Gesture_Position_Key_Names_Its_Position_And_Warns_It_Will_Not_Engage()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(vm, host, provider);

        vm.AddMappingCommand.Execute(null);
        vm.Editor.SelectedButton = GamepadButton.A;
        vm.Editor.ActiveTargetKind = MappingTargetKind.KeyPress;
        vm.Editor.SelectedGestureKind = GestureKind.LongPress;
        vm.Editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        vm.Editor.ActiveSpecialKind = SpecialKeyKind.Precision;
        await vm.CommitEditorCommand.ExecuteAsync(null);

        var precision = Precision(vm);
        Assert.True(precision.IsConfigured);
        Assert.Contains("A 键", precision.Summary);
        Assert.Contains("长按", precision.Summary);

        // The engine registers a precision key from the click alone, so this one engages
        // nothing - stated beside the key, with the click target untouched beside it.
        Assert.True(precision.HasConflicts);
        Assert.Contains("不会生效", precision.ConflictHint!);
    }

    [Fact]
    public async Task A_Disabled_Key_Reads_已禁用()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(vm, host, provider);

        await CommitSpecialKey(vm, GamepadButton.Back, SpecialKeyKind.Leader);
        var row = vm.Mappings.Single(r => r.Entry.Target is SpecialKeyTarget);
        row.IsEnabled = false;

        var leader = Leader(vm);
        Assert.True(leader.IsConfigured);
        Assert.Contains("已禁用", leader.Summary);
    }

    [Fact]
    public async Task The_Conflicts_Ride_On_Their_Kind_Row_And_On_The_Mapping_Rows()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(vm, host, provider);

        // B carries the default B -> Enter mapping; arming a leader on B through 新增 puts a
        // second entry on the same button, and one press edge runs both mappings.
        await CommitSpecialKey(vm, GamepadButton.B, SpecialKeyKind.Leader);

        var leader = Leader(vm);
        Assert.True(leader.HasConflicts);
        Assert.Contains("另一条映射", leader.ConflictHint!);
        Assert.Contains("Enter", leader.ConflictHint!);

        var leaderRow = vm.Mappings.Single(row => row.Entry.Target is SpecialKeyTarget);
        var enterRow = vm.Mappings.Single(row => row.Entry.Target is KeyPressTarget && row.Entry.Source == new ButtonBinding(GamepadButton.B));

        Assert.True(leaderRow.HasConflicts);
        Assert.Contains("另一条映射", leaderRow.ConflictHint!);
        Assert.True(enterRow.HasConflicts);
        Assert.Contains("另一条映射", enterRow.ConflictHint!);
    }

    [Fact]
    public async Task The_Area_Refreshes_After_A_Preset_Switch()
    {
        var (vm, host, provider, store) = CreateHost();
        Connect(vm, host, provider);
        var presets = new PresetStore(store);

        // Slot 2 carries a precision key of its own; slot 1 (the default profile) has none.
        presets.SaveSlot("xinput:0", 2, new GamepadProfile
        {
            Name = "第二套",
            DeviceId = "xinput:0",
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B),
                    Target = new SpecialKeyTarget(SpecialKeyKind.Precision, PrecisionMode.Hold),
                },
            ],
        });

        Assert.False(Precision(vm).IsConfigured);

        await vm.SwitchPresetSlotCommand.ExecuteAsync(2);

        var precision = Precision(vm);
        Assert.True(precision.IsConfigured);
        Assert.Contains("B 键", precision.Summary);
        Assert.False(Leader(vm).IsConfigured);
    }

    [Fact]
    public async Task The_Area_Refreshes_After_The_Shared_Key_Fans_Out_To_Another_Preset()
    {
        var (vm, host, provider, store) = CreateHost();
        Connect(vm, host, provider);
        var presets = new PresetStore(store);

        await CommitSpecialKey(vm, GamepadButton.Back, SpecialKeyKind.Leader);
        Assert.True(Leader(vm).IsConfigured);

        // The leader is device-global: committing it on slot 1 fans it out to slot 2, so the
        // area reports it there too without the user configuring it a second time.
        await vm.SwitchPresetSlotCommand.ExecuteAsync(2);

        Assert.True(Leader(vm).IsConfigured);
        Assert.Contains("Back 键", Leader(vm).Summary);

        await vm.SwitchPresetSlotCommand.ExecuteAsync(1);
        Assert.True(Leader(vm).IsConfigured);
    }

    [Fact]
    public async Task Editing_A_Global_Key_Opens_Its_Mapping_In_The_Editor()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(vm, host, provider);

        await CommitSpecialKey(vm, GamepadButton.Back, SpecialKeyKind.Leader, LeaderSwitchMode.Hold, 1, 3);

        Assert.False(vm.IsEditing);

        vm.EditSpecialKeyCommand.Execute(Leader(vm));

        Assert.True(vm.IsEditing);
        Assert.Equal(SpecialKeyKind.Leader, vm.Editor.ActiveSpecialKind);
        Assert.Equal(LeaderSwitchMode.Hold, vm.Editor.ActiveSpecialSwitchMode);
        Assert.Equal(3, vm.Editor.ActiveSpecialSlotB);
        Assert.Equal(GamepadButton.Back, vm.Editor.SelectedButton);
    }

    [Fact]
    public void Editing_An_Unconfigured_Kind_Opens_Nothing()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(vm, host, provider);

        vm.EditSpecialKeyCommand.Execute(Leader(vm));

        Assert.False(vm.IsEditing);
    }

    [Fact]
    public async Task The_Editor_Is_Handed_The_Profiles_Other_Mappings_For_Its_Hints()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(vm, host, provider);

        var bRow = vm.Mappings.Single(row => row.Entry.Source == new ButtonBinding(GamepadButton.B));
        vm.EditMappingCommand.Execute(bRow);

        // The edited entry is excluded, so the draft is never measured against itself; the
        // other nine mappings of the default profile stay in scope.
        Assert.DoesNotContain(vm.Editor.OtherMappings, entry => entry.Id == bRow.Entry.Id);
        Assert.Equal(9, vm.Editor.OtherMappings.Count);
    }

    [Fact]
    public async Task The_Editor_Hint_Is_Live_While_Configuring_A_Leader_Key()
    {
        var (vm, host, provider, _) = CreateHost();
        Connect(vm, host, provider);

        var bRow = vm.Mappings.Single(row => row.Entry.Source == new ButtonBinding(GamepadButton.B));
        vm.EditMappingCommand.Execute(bRow);

        Assert.False(vm.Editor.HasSpecialKeyConflict);

        vm.Editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        vm.Editor.ActiveSpecialKind = SpecialKeyKind.Leader;

        // B -> Enter becomes B -> Leader, so the click target the draft replaces is gone and
        // nothing else sits on B: no collision while the draft stands alone.
        Assert.False(vm.Editor.HasSpecialKeyConflict);

        // Arming the double-click on the same draft delays the switch - stated live.
        vm.Editor.DoubleClickEnabled = true;

        Assert.True(vm.Editor.HasSpecialKeyConflict);
        Assert.Contains("双击窗口", vm.Editor.SpecialKeyConflictHint!);

        // Cancelling closes the card. The draft itself is left as it was - reopening any
        // mapping reloads it wholesale, exactly as it did before the hints existed.
        vm.CancelEditorCommand.Execute(null);

        Assert.False(vm.IsEditing);
    }
}
