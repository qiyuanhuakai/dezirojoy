using System.ComponentModel;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.Tests.Ipc;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The scheme-bundle seam: exporting a device's whole scheme, previewing what an import will
/// replace, applying it as one atomic operation and undoing it. The scheme bundle is the single
/// import/export design, so a plain single-profile file is imported through it as a one-preset
/// scheme. The view is stood in for by answering the path requests directly.
/// </summary>
public class MainWindowViewModelSchemeBundleTests : IDisposable
{
    private readonly string _directory;

    public MainWindowViewModelSchemeBundleTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-vm-scheme-tests", Guid.NewGuid().ToString("N"));
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

    private string TempPath(string name) => Path.Combine(_directory, name);

    private static (MainWindowViewModel Vm, EngineHost Host, FakeGamepadProvider Provider, InMemoryProfileStore Store) Connected()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var vm = new MainWindowViewModel(host, store);

        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        return (vm, host, provider, store);
    }

    /// <summary>A two-preset bundle to import: the base keeps one mapping, the second adds a stick.</summary>
    private static SchemeBundle TwoPresetBundle(string name = "导入的方案")
    {
        var baseProfile = new GamepadProfile
        {
            Name = "导入的主配置",
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
                    Source = new ButtonBinding(GamepadButton.LeftStick),
                    Target = new SpecialKeyTarget(SpecialKeyKind.Precision, PrecisionMode.Toggle),
                },
            ],
        };

        var secondProfile = new GamepadProfile
        {
            Name = "导入的第二套",
            Mappings =
            [
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right),
                    Target = new MouseMoveTarget(new MouseStickSettings { MaxSpeed = 2000f }),
                },
            ],
        };

        return new SchemeBundle
        {
            Name = name,
            AppVersion = "9.9.9-test",
            Notes = "使用说明：先试主配置。",
            Presets =
            [
                new SchemePreset { Slot = 1, Profile = baseProfile, ActionNames = SchemeBundleFactory.ActionNamesOf(baseProfile) },
                new SchemePreset { Slot = 2, Profile = secondProfile, ActionNames = SchemeBundleFactory.ActionNamesOf(secondProfile) },
            ],
            SpecialKeys =
            [
                new PresetStore.DeviceSpecialKey(
                    GamepadButton.LeftStick,
                    0,
                    SpecialKeyKind.Precision,
                    PresetStore.SpecialKeyPosition.Target,
                    new SpecialKeyTarget(SpecialKeyKind.Precision, PrecisionMode.Toggle),
                    Enabled: true),
            ],
            Layers = [],
        };
    }

    private void WriteBundle(string fileName, SchemeBundle bundle)
        => File.WriteAllText(TempPath(fileName), SchemeBundleCodec.Serialize(bundle));

    [Fact]
    public void ExportSchemeTo_Writes_A_Bundle_Of_The_Devices_Whole_Scheme()
    {
        var (vm, _, _, _) = Connected();
        var path = TempPath("scheme.json");

        vm.ExportSchemeTo(path);

        Assert.False(vm.IsTransferError, vm.TransferMessage);
        Assert.Contains("方案已导出", vm.TransferMessage);

        var loaded = SchemeBundleCodec.Load(path);
        Assert.True(loaded.Succeeded, loaded.Message);
        var bundle = loaded.Bundle!;

        // The device's whole preset set, not one profile: the initial four slots.
        Assert.Equal(4, bundle.Presets.Count);
        Assert.Equal([1, 2, 3, 4], bundle.Presets.Select(preset => preset.Slot));
        Assert.False(string.IsNullOrWhiteSpace(bundle.AppVersion));
        Assert.False(string.IsNullOrWhiteSpace(bundle.Notes));
        Assert.Contains(bundle.Presets[0].ActionNames, action => action.Action == "鼠标移动");
    }

    [Fact]
    public void ExportSchemeTo_Without_A_Controller_Reports_An_Error()
    {
        var provider = new FakeGamepadProvider();
        var store = new InMemoryProfileStore();
        var vm = new MainWindowViewModel(new EngineHost(provider, new RecordingOutputSink(), store), store);

        vm.ExportSchemeTo(TempPath("nothing.json"));

        Assert.True(vm.IsTransferError);
        Assert.Contains("导出失败", vm.TransferMessage);
        Assert.False(File.Exists(TempPath("nothing.json")));
    }

    [Fact]
    public void Import_Previews_What_Will_Be_Replaced_Before_Anything_Is_Written()
    {
        var (vm, _, _, store) = Connected();
        var before = store.Load("xinput:0")!.Name;
        WriteBundle("incoming.json", TwoPresetBundle());

        vm.ImportSchemeFrom(TempPath("incoming.json"));

        Assert.False(vm.IsTransferError, vm.TransferMessage);
        Assert.True(vm.IsSchemePreviewOpen);
        var preview = vm.SchemePreview!;
        Assert.Equal("导入的方案", preview.SchemeName);
        Assert.Equal("9.9.9-test", preview.AppVersion);
        Assert.Equal("使用说明：先试主配置。", preview.Notes);

        // Two slots are replaced (the device holds all four), two are removed.
        Assert.Equal(4, preview.Rows.Count);
        Assert.Equal(2, preview.Rows.Count(row => row.Kind == SchemePreviewRowKind.Replaced));
        Assert.Equal(2, preview.Rows.Count(row => row.Kind == SchemePreviewRowKind.Removed));
        Assert.True(preview.ReplacesAnything);
        Assert.Contains(preview.Rows, row => row.Kind == SchemePreviewRowKind.Replaced && row.Slot == 1);
        var first = preview.Rows.First(row => row.Slot == 1);
        Assert.Equal(before, first.CurrentName);
        Assert.Equal("导入的主配置", first.IncomingName);
        Assert.Contains("导入的主配置", first.Detail);

        // The device-global special keys and the readable action names travel with the preview.
        Assert.Single(preview.SpecialKeyLines);
        Assert.Contains(preview.SpecialKeyLines, line => line.Contains("精准键"));
        Assert.Contains(preview.Presets[0].ActionNames, action => action.Source == "B 键" && action.Action == "Enter");

        // Nothing is written until the user confirms.
        Assert.Equal(before, store.Load("xinput:0")!.Name);
        Assert.False(vm.SchemeImportApplied);
    }

    [Fact]
    public async Task Confirm_Replaces_Every_Slot_As_One_Atomic_Operation()
    {
        var (vm, _, _, store) = Connected();
        WriteBundle("incoming.json", TwoPresetBundle());
        vm.ImportSchemeFrom(TempPath("incoming.json"));

        await vm.ConfirmSchemeImportCommand.ExecuteAsync(null);

        Assert.False(vm.IsTransferError, vm.TransferMessage);
        Assert.Contains("已导入方案", vm.TransferMessage);

        // The whole slot set is the scheme's: the bundle carries two presets, so the device
        // ends with exactly those two, not four.
        var slots = new PresetStore(store).SlotsInUse("xinput:0");
        Assert.Equal([1, 2], slots);
        Assert.Equal("导入的主配置", store.Load("xinput:0")!.Name);
        Assert.Equal("导入的第二套", store.Load("preset2:xinput:0")!.Name);

        // The device-global special key the bundle names is fanned out to every slot.
        foreach (var slot in new[] { 1, 2 })
        {
            var profile = new PresetStore(store).LoadSlot("xinput:0", slot)!;
            _ = new MappingEngine(profile);
            Assert.Contains(profile.Mappings, m =>
                m.Source is ButtonBinding { Button: GamepadButton.LeftStick } && m.Target is SpecialKeyTarget);
        }

        // The in-view state follows the base preset the device now runs.
        Assert.Equal("导入的主配置", vm.ActiveProfile!.Name);
        Assert.Equal(1, vm.ActivePresetSlot);
        Assert.True(vm.CanUndoSchemeImport);
        Assert.True(vm.SchemeImportApplied);
    }

    [Fact]
    public async Task Undo_Restores_The_Slot_Set_The_Import_Replaced()
    {
        var (vm, _, _, store) = Connected();
        var presets = new PresetStore(store);
        var before = presets.CaptureSlots("xinput:0");
        WriteBundle("incoming.json", TwoPresetBundle());
        vm.ImportSchemeFrom(TempPath("incoming.json"));
        await vm.ConfirmSchemeImportCommand.ExecuteAsync(null);

        await vm.UndoSchemeImportCommand.ExecuteAsync(null);

        Assert.Contains("已撤销", vm.TransferMessage);
        Assert.Equal([1, 2, 3, 4], presets.SlotsInUse("xinput:0"));
        foreach (var (slot, profile) in before)
        {
            Assert.Equal(profile, presets.LoadSlot("xinput:0", slot));
        }

        Assert.Equal(before[1]!.Name, vm.ActiveProfile!.Name);
        Assert.False(vm.CanUndoSchemeImport);
        Assert.False(vm.IsSchemePreviewOpen);
    }

    [Fact]
    public void Cancel_Leaves_The_Device_Untouched()
    {
        var (vm, _, _, store) = Connected();
        var before = store.Load("xinput:0")!.Name;
        WriteBundle("incoming.json", TwoPresetBundle());

        vm.ImportSchemeFrom(TempPath("incoming.json"));
        vm.CancelSchemeImportCommand.Execute(null);

        Assert.False(vm.IsSchemePreviewOpen);
        Assert.Null(vm.SchemePreview);
        Assert.Equal(before, store.Load("xinput:0")!.Name);
        Assert.False(vm.CanUndoSchemeImport);
    }

    [Fact]
    public async Task The_Windows_Template_Imports_Cleanly_And_Consistently()
    {
        var (vm, _, _, store) = Connected();

        vm.ImportWindowsTemplateCommand.Execute(null);
        Assert.True(vm.IsSchemePreviewOpen);

        await vm.ConfirmSchemeImportCommand.ExecuteAsync(null);

        Assert.False(vm.IsTransferError, vm.TransferMessage);
        var presets = new PresetStore(store);
        Assert.Equal([1, 2], presets.SlotsInUse("xinput:0"));

        var desktop = presets.LoadSlot("xinput:0", 1)!;
        var gaming = presets.LoadSlot("xinput:0", 2)!;
        Assert.Equal("Windows 桌面办公", desktop.Name);
        Assert.Equal("Windows 游戏竞技", gaming.Name);

        // Both presets build in the engine and both carry the same three device-global
        // special keys, so the pair behaves consistently whichever one is active.
        _ = new MappingEngine(desktop);
        _ = new MappingEngine(gaming);
        foreach (var profile in new[] { desktop, gaming })
        {
            var kinds = profile.Mappings
                .Where(m => m.Target is SpecialKeyTarget)
                .Select(m => (SpecialKeyTarget)m.Target)
                .ToList();
            Assert.Contains(kinds, key => key.Kind == SpecialKeyKind.Precision);
            Assert.Contains(kinds, key => key.Kind == SpecialKeyKind.Leader && key.Action == LeaderAction.Layer);
            Assert.Contains(kinds, key =>
                key.Kind == SpecialKeyKind.Leader && key.Action is LeaderAction.Preset && key.SlotA == 1 && key.SlotB == 2);
        }

        // The preset leader flips between the two slots the template actually shipped, and the
        // layer the layer leader enters is populated in both presets.
        Assert.Contains(desktop.Mappings, m => m.Layer == 1);
        Assert.Contains(gaming.Mappings, m => m.Layer == 1);
    }

    [Fact]
    public void Import_Of_A_Malformed_File_Reports_An_Error_And_Changes_Nothing()
    {
        var (vm, _, _, store) = Connected();
        var before = store.Load("xinput:0")!.Name;
        File.WriteAllText(TempPath("broken.json"), "{ not a bundle");

        vm.ImportSchemeFrom(TempPath("broken.json"));

        Assert.True(vm.IsTransferError);
        Assert.Contains("导入失败", vm.TransferMessage);
        Assert.False(vm.IsSchemePreviewOpen);
        Assert.Equal(before, store.Load("xinput:0")!.Name);
    }

    [Fact]
    public async Task Import_Of_A_Plain_Profile_File_Imports_It_As_A_One_Preset_Scheme()
    {
        var (vm, _, _, store) = Connected();
        File.WriteAllText(TempPath("single.json"), ProfileSerializer.Serialize(new GamepadProfile
        {
            Name = "旧版单个配置",
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.X),
                    Target = new KeyPressTarget(GamepadKey.Space),
                },
            ],
        }));

        vm.ImportSchemeFrom(TempPath("single.json"));

        Assert.False(vm.IsTransferError, vm.TransferMessage);
        Assert.True(vm.IsSchemePreviewOpen);

        // The old single-profile file arrives as a one-preset scheme: slot 1 is replaced and
        // the slots the scheme does not carry are marked for removal, like any bundle.
        var preview = vm.SchemePreview!;
        Assert.Equal("旧版单个配置", preview.SchemeName);
        Assert.Single(preview.Presets);
        Assert.Contains(preview.Rows, row =>
            row.Kind == SchemePreviewRowKind.Replaced && row.Slot == 1 && row.IncomingName == "旧版单个配置");

        await vm.ConfirmSchemeImportCommand.ExecuteAsync(null);

        Assert.False(vm.IsTransferError, vm.TransferMessage);
        var presets = new PresetStore(store);
        Assert.Equal([1], presets.SlotsInUse("xinput:0"));
        Assert.Equal("旧版单个配置", store.Load("xinput:0")!.Name);
        Assert.Equal("旧版单个配置", vm.ActiveProfile!.Name);
        Assert.Contains(vm.Mappings, row => row.TargetText.Contains("Space"));
        Assert.True(vm.CanUndoSchemeImport);
    }

    [Fact]
    public void A_Leader_Aimed_Outside_The_Scheme_Is_Refused_Before_The_Preview_Opens()
    {
        var (vm, _, _, store) = Connected();
        var bundle = TwoPresetBundle() with
        {
            Presets =
            [
                TwoPresetBundle().Presets[0] with
                {
                    Profile = TwoPresetBundle().Presets[0].Profile with
                    {
                        Mappings =
                        [
                            .. TwoPresetBundle().Presets[0].Profile.Mappings,
                            new MappingEntry
                            {
                                Source = new ButtonBinding(GamepadButton.Back),
                                Target = new SpecialKeyTarget(SpecialKeyKind.Leader, SlotA: 1, SlotB: 9),
                            },
                        ],
                    },
                },
                TwoPresetBundle().Presets[1],
            ],
        };
        WriteBundle("outside.json", bundle);
        var before = store.Load("xinput:0")!.Name;

        vm.ImportSchemeFrom(TempPath("outside.json"));

        Assert.True(vm.IsTransferError);
        Assert.Contains("Leader", vm.TransferMessage);
        Assert.False(vm.IsSchemePreviewOpen);
        Assert.Equal(before, store.Load("xinput:0")!.Name);
    }

    [Fact]
    public async Task A_Failed_Reload_Rolls_The_Whole_Import_Back()
    {
        var (vm, _, _, store) = Connected();
        var presets = new PresetStore(store);

        // A mapping the engine refuses - a stick driving a digital target without a direction -
        // so the reload that follows the store write throws and the rollback has to run.
        var broken = new SchemeBundle
        {
            Name = "坏方案",
            Presets =
            [
                new SchemePreset
                {
                    Slot = 1,
                    Profile = new GamepadProfile
                    {
                        Name = "坏配置",
                        Mappings =
                        [
                            new MappingEntry
                            {
                                Source = new StickBinding(GamepadStick.Left),
                                Target = new KeyPressTarget(GamepadKey.W),
                            },
                        ],
                    },
                },
            ],
        };
        WriteBundle("broken-scheme.json", broken);
        var before = presets.CaptureSlots("xinput:0");

        vm.ImportSchemeFrom(TempPath("broken-scheme.json"));
        await vm.ConfirmSchemeImportCommand.ExecuteAsync(null);

        Assert.True(vm.IsTransferError, vm.TransferMessage);
        Assert.Contains("导入失败", vm.TransferMessage);

        // The store is exactly where it started, and no undo is armed for an import that
        // never landed.
        Assert.Equal([1, 2, 3, 4], presets.SlotsInUse("xinput:0"));
        foreach (var (slot, profile) in before)
        {
            Assert.Equal(profile, presets.LoadSlot("xinput:0", slot));
        }

        Assert.False(vm.CanUndoSchemeImport);
    }

    [Fact]
    public void Import_Without_A_Controller_Reports_An_Error()
    {
        var provider = new FakeGamepadProvider();
        var store = new InMemoryProfileStore();
        var vm = new MainWindowViewModel(new EngineHost(provider, new RecordingOutputSink(), store), store);
        WriteBundle("portable.json", TwoPresetBundle());

        vm.ImportSchemeFrom(TempPath("portable.json"));

        Assert.True(vm.IsTransferError);
        Assert.Contains("导入失败", vm.TransferMessage);
        Assert.False(vm.IsSchemePreviewOpen);
    }

    [Fact]
    public void Export_Then_Import_Round_Trips_A_Devices_Scheme_Onto_Itself()
    {
        var (vm, _, _, store) = Connected();
        var presets = new PresetStore(store);

        vm.ExportSchemeTo(TempPath("roundtrip.json"));
        vm.ImportSchemeFrom(TempPath("roundtrip.json"));

        var preview = vm.SchemePreview!;
        Assert.All(preview.Rows, row => Assert.Equal(SchemePreviewRowKind.Replaced, row.Kind));
        Assert.Empty(preview.LayerLines);
    }

    // ---- Daemon mode -----------------------------------------------------------

    [Fact]
    public async Task The_Daemon_Import_Writes_The_Shared_Store_And_The_Snapshot_Follows()
    {
        var daemonHost = new FakeDaemonHost();
        var store = new InMemoryProfileStore();
        var statePath = Path.Combine(Path.GetTempPath(), "dezirojoy-vm-scheme-daemon", Guid.NewGuid().ToString("N"), "state.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var channel = new ServerChannel(server);
        server.StateChanged += channel.Push;
        server.InputSampled += channel.Push;
        var controller = new DaemonController(new ServerEndpoint(channel));
        await controller.ConnectAsync(TestContext.Current.CancellationToken);

        await using var vm = new MainWindowViewModel(
            new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store),
            store,
            daemon: controller,
            daemonRequired: true);

        daemonHost.AddDevice("xinput:0", "Xbox Wireless Controller");
        await WaitForConnectedDevice(vm, "xinput:0");

        // The device starts with nothing in the store, so the preview offers two additions.
        WriteBundle("daemon.json", TwoPresetBundle());
        vm.ImportSchemeFrom(TempPath("daemon.json"));
        Assert.All(vm.SchemePreview!.Rows, row => Assert.Equal(SchemePreviewRowKind.Added, row.Kind));

        await vm.ConfirmSchemeImportCommand.ExecuteAsync(null);

        Assert.False(vm.IsTransferError, vm.TransferMessage);
        Assert.Contains("已导入方案", vm.TransferMessage);

        // The shared store carries the scheme, and the daemon's snapshot body - which the
        // view model adopts - is the new base preset.
        var presets = new PresetStore(store);
        Assert.Equal([1, 2], presets.SlotsInUse("xinput:0"));
        Assert.Equal("导入的主配置", presets.LoadSlot("xinput:0", 1)!.Name);
        Assert.Equal("导入的主配置", vm.ActiveProfile!.Name);
        Assert.Contains(vm.Mappings, row => row.TargetText.Contains("Enter"));
        Assert.True(vm.CanUndoSchemeImport);
    }

    private static async Task WaitForConnectedDevice(MainWindowViewModel viewModel, string deviceId)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MainWindowViewModel.DaemonSnapshot)
                && viewModel.DaemonSnapshot?.Devices.Any(device => device.DeviceId == deviceId) == true)
            {
                reached.TrySetResult();
            }
        }

        viewModel.PropertyChanged += OnPropertyChanged;
        try
        {
            OnPropertyChanged(viewModel, new PropertyChangedEventArgs(nameof(MainWindowViewModel.DaemonSnapshot)));
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            viewModel.PropertyChanged -= OnPropertyChanged;
        }
    }

    private sealed class ServerEndpoint(ServerChannel channel) : IDaemonEndpoint
    {
        public Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
            => Task.FromResult<IDaemonChannel>(channel);
    }

    /// <summary>
    /// One client side of the daemon server: answers are produced synchronously on send and
    /// pushed broadcasts queue behind them, exactly like the named-pipe transport delivers
    /// them in order.
    /// </summary>
    private sealed class ServerChannel(DaemonServer server) : IDaemonChannel
    {
        private readonly object _gate = new();
        private readonly Queue<IpcMessage> _incoming = new();
        private TaskCompletionSource<IpcMessage>? _waiting;
        private bool _closed;

        public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            var answer = await server.ProcessAsync(message);
            Push(answer);
        }

        public Task<IpcMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_incoming.Count > 0)
                {
                    return Task.FromResult(_incoming.Dequeue());
                }

                if (_closed)
                {
                    return Task.FromException<IpcMessage>(new EndOfStreamException());
                }

                _waiting ??= new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                return AwaitReceiveAsync(_waiting.Task, cancellationToken);
            }
        }

        public void Push(IpcMessage message)
        {
            lock (_gate)
            {
                _incoming.Enqueue(message);
                _waiting?.TrySetResult(message);
                _waiting = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _closed = true;
                _waiting?.TrySetException(new EndOfStreamException());
                _waiting = null;
            }

            return ValueTask.CompletedTask;
        }

        private static async Task<IpcMessage> AwaitReceiveAsync(Task<IpcMessage> waiting, CancellationToken cancellationToken)
        {
            var completed = await Task.WhenAny(waiting, Task.Delay(Timeout.Infinite, cancellationToken));
            return await (completed == waiting ? waiting : Task.FromException<IpcMessage>(new OperationCanceledException(cancellationToken)));
        }
    }
}
