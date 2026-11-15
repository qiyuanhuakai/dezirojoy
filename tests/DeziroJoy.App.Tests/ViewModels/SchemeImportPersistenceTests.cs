using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>Durable scheme recovery and the opt-in global foreground-rule half of an import.</summary>
public sealed class SchemeImportPersistenceTests : IDisposable
{
    private const string DeviceId = "xinput:0";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "dezirojoy-scheme-recovery", Guid.NewGuid().ToString("N"));

    public SchemeImportPersistenceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string PathFor(string name) => Path.Combine(_directory, name);

    private static (MainWindowViewModel Vm, EngineHost Host, FakeGamepadProvider Provider, InMemoryProfileStore Profiles) Connected(
        IForegroundRuleStore rules,
        ISchemeImportRecoveryStore recovery,
        EngineHost? host = null,
        FakeGamepadProvider? provider = null,
        InMemoryProfileStore? profiles = null)
    {
        provider ??= new FakeGamepadProvider();
        profiles ??= new InMemoryProfileStore();
        host ??= new EngineHost(provider, new RecordingOutputSink(), profiles);
        var vm = new MainWindowViewModel(
            host, profiles, foregroundRuleStore: rules, schemeImportRecoveryStore: recovery);

        if (provider.GetConnected().Count == 0)
        {
            provider.Add(DeviceId, "Xbox Wireless Controller");
            provider.SetState(DeviceId, GamepadState.Neutral);
        }

        SampleHelper.Sample(vm, host);
        return (vm, host, provider, profiles);
    }

    private static SchemeBundle Bundle(ForegroundSettings? rules = null)
    {
        var profile = new GamepadProfile
        {
            Name = "分享的配置",
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B),
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.LeftStick),
                    Target = new SpecialKeyTarget(SpecialKeyKind.Precision),
                },
            ],
        };

        return new SchemeBundle
        {
            Name = "浏览器与编辑器工作流",
            Presets = [new SchemePreset { Slot = 1, Profile = profile }],
            ForegroundRules = rules,
        };
    }

    private static ForegroundSettings Rules(string process, int slot, bool enabled = true) => new()
    {
        Enabled = enabled,
        DefaultSlot = slot,
        Rules = [new ForegroundRule { ProcessName = process, Slot = slot }],
    };

    private void WriteBundle(SchemeBundle bundle)
        => File.WriteAllText(PathFor("scheme.json"), SchemeBundleCodec.Serialize(bundle));

    [Fact]
    public async Task Export_Carries_Rules_But_Import_Leaves_Them_Off_By_Default()
    {
        var rules = new InMemoryForegroundRuleStore();
        rules.Save(Rules("old-editor", 3));
        var recovery = new InMemorySchemeImportRecoveryStore();
        var (vm, _, _, _) = Connected(rules, recovery);
        var exported = PathFor("exported.json");

        vm.ExportSchemeTo(exported);
        var bundle = SchemeBundleCodec.Load(exported).Bundle!;
        Assert.Equal(
            ForegroundSettingsSerializer.Serialize(rules.Load()),
            ForegroundSettingsSerializer.Serialize(bundle.ForegroundRules!));

        WriteBundle(Bundle(Rules("incoming-browser", 2)));
        vm.ImportSchemeFrom(PathFor("scheme.json"));
        Assert.True(vm.SchemePreview!.HasForegroundRules);
        Assert.Contains("全应用共享", vm.SchemePreview.ForegroundRulesSummary);
        Assert.False(vm.ApplySchemeForegroundRules);

        await vm.ConfirmSchemeImportCommand.ExecuteAsync(null);

        Assert.Equal("old-editor", rules.Load().Rules.Single().ProcessName);
        Assert.Null(recovery.Load()!.ForegroundRules);
    }

    [Fact]
    public async Task Reopened_Gui_Restores_Presets_Active_Slot_And_Opted_In_Rules()
    {
        var rules = new ForegroundRuleStore(PathFor("rules.json"));
        var recovery = new FileSchemeImportRecoveryStore(PathFor("recovery.json"));
        var first = Connected(rules, recovery);
        var beforeRules = Rules("before", 2, enabled: false);
        rules.Save(beforeRules);
        await first.Vm.SwitchPresetSlotCommand.ExecuteAsync(2);
        var before = new PresetStore(first.Profiles).CaptureSlots(DeviceId);
        WriteBundle(Bundle(Rules("after", 1)));
        first.Vm.ImportSchemeFrom(PathFor("scheme.json"));
        first.Vm.ApplySchemeForegroundRules = true;
        await first.Vm.ConfirmSchemeImportCommand.ExecuteAsync(null);

        Assert.Equal("after", rules.Load().Rules.Single().ProcessName);
        Assert.True(File.Exists(PathFor("recovery.json")));
        Assert.Equal(1, first.Vm.ActivePresetSlot);

        // A new render process gets a new view model over the same daemon/store state.
        var reopened = Connected(rules, recovery, first.Host, first.Provider, first.Profiles);
        Assert.True(reopened.Vm.CanUndoSchemeImport);
        var persisted = recovery.Load()!;
        foreach (var (slot, profile) in before)
        {
            Assert.Equal(
                ProfileSerializer.Serialize(profile!),
                ProfileSerializer.Serialize(persisted.Slots[slot]!));
        }

        reopened.Vm.ShowSchemeUndoCommand.Execute(null);
        Assert.True(reopened.Vm.IsSchemePreviewOpen);
        Assert.Contains("浏览器与编辑器", reopened.Vm.SchemePreview!.SchemeName);
        await reopened.Vm.UndoSchemeImportCommand.ExecuteAsync(null);

        var presets = new PresetStore(first.Profiles);
        Assert.Equal(before.Keys.Order(), presets.CaptureSlots(DeviceId).Keys.Order());
        foreach (var (slot, profile) in before)
        {
            Assert.Equal(
                ProfileSerializer.Serialize(profile!),
                ProfileSerializer.Serialize(presets.LoadSlot(DeviceId, slot)!));
        }

        Assert.Equal(2, reopened.Vm.ActivePresetSlot);
        Assert.Equal(before[2]!.Name, reopened.Vm.ActiveProfile!.Name);
        Assert.Equal(
            ForegroundSettingsSerializer.Serialize(beforeRules),
            ForegroundSettingsSerializer.Serialize(rules.Load()));
        Assert.False(reopened.Vm.CanUndoSchemeImport);
        Assert.False(File.Exists(PathFor("recovery.json")));
    }

    [Fact]
    public async Task Failed_Recovery_Clear_Keeps_Undo_Available_For_A_Retry()
    {
        var rules = new InMemoryForegroundRuleStore();
        var recovery = new FlakyClearRecoveryStore();
        var (vm, _, _, profiles) = Connected(rules, recovery);
        var before = new PresetStore(profiles).CaptureSlots(DeviceId);
        WriteBundle(Bundle());
        vm.ImportSchemeFrom(PathFor("scheme.json"));
        await vm.ConfirmSchemeImportCommand.ExecuteAsync(null);
        recovery.FailClear = true;

        await vm.UndoSchemeImportCommand.ExecuteAsync(null);

        Assert.True(vm.IsTransferError);
        Assert.Contains("恢复点已保留", vm.TransferMessage);
        Assert.True(vm.CanUndoSchemeImport);
        Assert.NotNull(recovery.Load());

        recovery.FailClear = false;
        await vm.UndoSchemeImportCommand.ExecuteAsync(null);

        Assert.False(vm.CanUndoSchemeImport);
        Assert.Equal(before[1], new PresetStore(profiles).LoadSlot(DeviceId, 1));
        Assert.Null(recovery.Load());
    }

    [Fact]
    public async Task Recovery_Save_Failure_Aborts_Before_Any_Configuration_Write()
    {
        var rules = new InMemoryForegroundRuleStore();
        rules.Save(Rules("before", 2));
        var recovery = new FailingSaveRecoveryStore();
        var (vm, _, _, profiles) = Connected(rules, recovery);
        var before = new PresetStore(profiles).CaptureSlots(DeviceId);
        WriteBundle(Bundle(Rules("after", 1)));
        vm.ImportSchemeFrom(PathFor("scheme.json"));
        vm.ApplySchemeForegroundRules = true;

        await vm.ConfirmSchemeImportCommand.ExecuteAsync(null);

        Assert.True(vm.IsTransferError);
        Assert.Contains("尚未修改配置", vm.TransferMessage);
        Assert.Equal(before, new PresetStore(profiles).CaptureSlots(DeviceId));
        Assert.Equal("before", rules.Load().Rules.Single().ProcessName);
        Assert.False(vm.CanUndoSchemeImport);
    }

    [Fact]
    public void Damaged_Recovery_Is_Reported_Without_Blocking_The_Gui()
    {
        File.WriteAllText(PathFor("broken-recovery.json"), "{ not json");
        var recovery = new FileSchemeImportRecoveryStore(PathFor("broken-recovery.json"));

        var (vm, _, _, _) = Connected(new InMemoryForegroundRuleStore(), recovery);

        Assert.False(vm.CanUndoSchemeImport);
        Assert.True(vm.IsTransferError);
        Assert.Contains("恢复点无法读取", vm.TransferMessage);
    }

    private sealed class FlakyClearRecoveryStore : ISchemeImportRecoveryStore
    {
        private readonly InMemorySchemeImportRecoveryStore _inner = new();
        public bool FailClear { get; set; }
        public string? LastError => null;
        public SchemeImportRecovery? Load() => _inner.Load();
        public void Save(SchemeImportRecovery recovery) => _inner.Save(recovery);
        public void Clear()
        {
            if (FailClear)
            {
                throw new IOException("模拟恢复点删除失败");
            }

            _inner.Clear();
        }
    }

    private sealed class FailingSaveRecoveryStore : ISchemeImportRecoveryStore
    {
        public string? LastError => null;
        public SchemeImportRecovery? Load() => null;
        public void Save(SchemeImportRecovery recovery) => throw new IOException("模拟磁盘已满");
        public void Clear()
        {
        }
    }
}
