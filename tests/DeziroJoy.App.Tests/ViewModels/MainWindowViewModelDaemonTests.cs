using System.IO.Pipes;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.Tests.Ipc;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

public sealed class MainWindowViewModelDaemonTests
{
    [Fact]
    public async Task Start_And_Stop_Project_The_Authoritative_Daemon_Snapshot()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        Assert.Equal(1, context.DaemonHost.StartCalls);
        Assert.True(viewModel.IsEngineRunning);
        Assert.Equal(MappingOwner.ResidentDaemon, viewModel.MappingOwner);
        Assert.Equal(InjectorIntegrityLevel.Elevated, viewModel.InjectorIntegrity);
        Assert.Contains("4242", viewModel.InjectorDescription);
        Assert.Equal(10, viewModel.DaemonSnapshot!.InjectedEvents);
        Assert.Equal(2, viewModel.DaemonSnapshot.BlockedEvents);

        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        Assert.Equal(1, context.DaemonHost.StopCalls);
        Assert.False(viewModel.IsEngineRunning);
        Assert.Equal(MappingOwner.None, viewModel.MappingOwner);
    }

    [Fact]
    public async Task Pause_And_Editor_Suspension_Are_Distinct_And_Do_Not_Stop_The_Engine()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        await viewModel.ToggleMappingCommand.ExecuteAsync(null);
        await viewModel.SetEditorInjectionSuspended(true);

        Assert.True(context.DaemonHost.IsRunning);
        Assert.False(context.DaemonHost.IsMappingEnabled);
        Assert.True(context.DaemonHost.IsInjectionSuspended);
        Assert.Equal(0, context.DaemonHost.StopCalls);
    }

    [Fact]
    public async Task Leaving_Scope_Releases_Only_This_Connections_Editor_Lease()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        var suspended = WaitForSuspension(context.DaemonHost, true);
        viewModel.AddMappingCommand.Execute(null);
        await suspended;
        Assert.True(viewModel.IsEditing);

        var released = WaitForSuspension(context.DaemonHost, false);
        viewModel.SetPhysicalInputScope(false);
        await released;

        Assert.True(context.DaemonHost.IsRunning, "leaving scope must not stop the resident mapping");
        Assert.True(context.DaemonHost.IsMappingEnabled, "leaving scope must not touch the user's pause");
        Assert.Equal(0, context.DaemonHost.StopCalls);

        var restored = WaitForSuspension(context.DaemonHost, true);
        viewModel.SetPhysicalInputScope(true);
        await restored;

        Assert.True(context.DaemonHost.IsInjectionSuspended, "reentry restores the lease for the retained draft");
    }

    [Fact]
    public async Task Closing_The_Editor_While_Out_Of_Scope_Leaves_The_Lease_Released()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        var suspended = WaitForSuspension(context.DaemonHost, true);
        viewModel.AddMappingCommand.Execute(null);
        await suspended;

        var released = WaitForSuspension(context.DaemonHost, false);
        viewModel.SetPhysicalInputScope(false);
        await released;

        viewModel.CancelEditorCommand.Execute(null);

        Assert.False(viewModel.IsEditing);
        Assert.False(context.DaemonHost.IsInjectionSuspended, "a discarded draft must not re-arm the lease");
    }

    [Fact]
    public async Task ApplyProfile_Waits_For_The_Daemons_Acceptance()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;
        var profile = new GamepadProfile { Name = "Arena", Mappings = [] };

        var applied = await viewModel.ApplyProfile(profile);

        Assert.True(applied);
        Assert.Same(profile, viewModel.ActiveProfile);
        Assert.Equal(1, viewModel.DaemonSnapshot!.Revision);
    }

    [Fact]
    public async Task A_Newer_Daemon_Revision_Rejects_A_Stale_Draft_And_Rebases_After_Cancel()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;
        var original = new GamepadProfile { Name = "Original", Mappings = [] };
        Assert.True(await viewModel.ApplyProfile(original));

        viewModel.AddMappingCommand.Execute(null);
        var external = new GamepadProfile { Name = "External", Mappings = [] };
        var externalAck = Assert.IsType<MutationAckMessage>(
            await context.Server.ProcessAsync(new ApplyConfigurationMessage(
                "external", 1, external.Name, ProfileSerializer.Serialize(external))));
        Assert.True(externalAck.Accepted);
        await WaitForDaemonRevision(viewModel, 2);

        Assert.Equal(2, viewModel.DaemonSnapshot!.Revision);
        Assert.Equal("Original", viewModel.ActiveProfile!.Name);
        Assert.True(viewModel.IsEditing);

        var applied = await viewModel.ApplyProfile(original with { Name = "Stale" });

        Assert.False(applied);
        Assert.Equal("Original", viewModel.ActiveProfile.Name);
        var authoritative = Assert.IsType<StateChangedMessage>(
            await context.Server.ProcessAsync(new GetStateMessage("verify")));
        Assert.Equal(2, authoritative.Snapshot.Revision);
        Assert.Equal("External", authoritative.Snapshot.ProfileName);
        Assert.True(viewModel.IsTransferError);

        viewModel.CancelEditorCommand.Execute(null);

        Assert.False(viewModel.IsEditing);
        Assert.Equal("External", viewModel.ActiveProfile.Name);
    }

    [Fact]
    public async Task Disposing_The_ViewModel_Does_Not_Stop_The_Resident_Mapping()
    {
        var context = await CreateDaemonViewModel();
        var viewModel = context.ViewModel;
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        await viewModel.DisposeAsync();

        Assert.True(context.DaemonHost.IsRunning);
        Assert.Equal(0, context.DaemonHost.StopCalls);
    }

    [Fact]
    public async Task Daemon_Required_Without_A_Connection_Never_Starts_A_Local_Engine()
    {
        var provider = new FakeGamepadProvider();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, new RecordingOutputSink(), store);
        await using var viewModel = new MainWindowViewModel(host, store, daemonRequired: true);

        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        Assert.False(host.IsRunning);
        Assert.Equal(MappingOwner.None, viewModel.MappingOwner);
        Assert.Contains("未启动本地注入回退", viewModel.TransferMessage);
        Assert.Equal("提权守护进程不可用；未启用本地注入回退", viewModel.StatusMessage);
    }

    [Fact]
    public async Task A_Daemon_Snapshot_With_A_Controller_Reports_Presence()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        context.DaemonHost.HasController = true;
        context.DaemonHost.AddDevice(
            "ds4-1",
            "DualShock 4 Wireless Controller",
            DeziroJoy.Core.Input.GamepadLayout.DualShock);
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasController);
        Assert.Equal("DualShock 4 Wireless Controller · DualShock 布局", viewModel.StatusMessage);
        Assert.DoesNotContain("由常驻守护进程映射", viewModel.StatusMessage);
    }

    [Fact]
    public async Task A_Daemon_Snapshot_Without_A_Controller_Reports_No_Gamepad()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        context.DaemonHost.HasController = false;
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasController);
        Assert.Equal("未检测到手柄，请连接手柄", viewModel.StatusMessage);
    }

    [Fact]
    public async Task The_Sync_Tick_Does_Not_Erase_Daemon_Presence()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        context.DaemonHost.HasController = true;
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasController);

        viewModel.SyncControllers();

        Assert.True(viewModel.HasController);
        Assert.True(viewModel.Visualizer.IsConnected);
    }

    [Fact]
    public async Task Losing_The_Controller_Through_The_Daemon_Reports_Asleep()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        context.DaemonHost.HasController = true;
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasController);

        // The pause toggle publishes a snapshot without refreshing the status itself, so
        // the wording the true->false transition produces is the one that survives.
        context.DaemonHost.HasController = false;
        await viewModel.ToggleMappingCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasController);
        Assert.Equal("手柄已断开连接，可能已进入休眠，请按手柄电源键唤醒；若仍无反应请检查连接", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Reconnect_Runs_The_Full_Handshake_And_State_Read_Without_A_Local_Fallback()
    {
        var daemonHost = new FakeDaemonHost();
        var store = new InMemoryProfileStore();
        var statePath = Path.Combine(Path.GetTempPath(), "dezirojoy-vm-daemon-tests", Guid.NewGuid().ToString("N"), "state.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var channel = new ServerChannel(server);
        server.StateChanged += channel.Push;
        var endpoint = new ServerEndpoint(channel) { Ready = false };
        var controller = new DaemonController(endpoint);
        var presentation = new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store);
        await using var viewModel = new MainWindowViewModel(presentation, store, daemon: controller, daemonRequired: true);

        await viewModel.ToggleEngineCommand.ExecuteAsync(null);
        Assert.False(presentation.IsRunning);
        Assert.True(viewModel.IsTransferError);

        endpoint.Ready = true;
        await viewModel.ReconnectDaemonCommand.ExecuteAsync(null);

        Assert.Equal(DaemonConnectionState.Connected, controller.State);
        Assert.NotNull(viewModel.DaemonSnapshot);
        Assert.False(presentation.IsRunning);
        Assert.False(viewModel.IsTransferError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconnect_Restores_An_Open_Editor_Lease_Without_Changing_Mapping_Pause(bool restartDaemon)
    {
        var pipeName = $"DeziroJoy.Test.{Guid.NewGuid():N}";
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var clock = new FakeClock();
        var daemonHost = new FakeDaemonHost();
        var server = new DaemonServer(daemonHost, new InMemoryProfileStore(), new DaemonStateStore(statePath), clock);
        var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var acceptTask = DaemonHost.ServePipeAsync(server, serverCancellation.Token, pipeName);
        var endpoint = new PipeEndpoint(pipeName);
        await using var controller = new DaemonController(endpoint);

        try
        {
            await controller.ConnectAsync(TestContext.Current.CancellationToken);
            var presentation = new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), new InMemoryProfileStore());
            await using var viewModel = new MainWindowViewModel(presentation, new InMemoryProfileStore(), daemon: controller, daemonRequired: true);
            await viewModel.ToggleEngineCommand.ExecuteAsync(null);

            var suspended = WaitForSuspension(daemonHost, true);
            viewModel.AddMappingCommand.Execute(null);
            await suspended;
            Assert.True(viewModel.IsEditing);

            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.Disconnected += () => disconnected.TrySetResult();
            var released = WaitForSuspension(daemonHost, false);
            await endpoint.LastChannel!.DisposeAsync();
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await released;

            Assert.False(daemonHost.IsInjectionSuspended);
            Assert.True(daemonHost.IsRunning);
            Assert.True(daemonHost.IsMappingEnabled);

            if (restartDaemon)
            {
                await serverCancellation.CancelAsync();
                await acceptTask;
                serverCancellation.Dispose();
                clock.Advance(TimeSpan.FromSeconds(1));
                daemonHost = new FakeDaemonHost();
                daemonHost.Start();
                server = new DaemonServer(daemonHost, new InMemoryProfileStore(), new DaemonStateStore(statePath), clock);
                serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                acceptTask = DaemonHost.ServePipeAsync(server, serverCancellation.Token, pipeName);
            }

            await viewModel.ReconnectDaemonCommand.ExecuteAsync(null);

            Assert.True(daemonHost.IsInjectionSuspended);
            Assert.True(daemonHost.IsRunning);
            Assert.True(daemonHost.IsMappingEnabled);
        }
        finally
        {
            await serverCancellation.CancelAsync();
            await acceptTask;
            serverCancellation.Dispose();
        }
    }

    [Fact]
    public async Task Daemon_Input_Reaches_Navigation_Capture_And_Visualizer_Without_A_Local_Provider()
    {
        var pipeName = $"DeziroJoy.Test.{Guid.NewGuid():N}";
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1", "Xbox Wireless Controller");
        var store = new InMemoryProfileStore();
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var acceptTask = DaemonHost.ServePipeAsync(server, serverCancellation.Token, pipeName);
        await using var controller = new DaemonController(new PipeEndpoint(pipeName));

        try
        {
            await controller.ConnectAsync(TestContext.Current.CancellationToken);
            var presentation = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
            await using var viewModel = new MainWindowViewModel(presentation, store, daemon: controller, daemonRequired: true);
            await viewModel.ToggleEngineCommand.ExecuteAsync(null);
            await WaitForSuspension(daemonHost, true);

            Assert.Null(viewModel.ActiveController);
            Assert.Empty(viewModel.Controllers);

            daemonHost.PublishInputSample("pad-1", GamepadState.Neutral);
            daemonHost.PublishInputSample(
                "pad-1",
                GamepadState.Create(GamepadButtonMask.A, 0.5f, 0, 0, 0));
            await WaitForCondition(() => viewModel.SelectedSource is ButtonBinding { Button: GamepadButton.A });

            Assert.True(viewModel.Visualizer.IsAPressed);
            Assert.Equal(0.5f, viewModel.Visualizer.LeftStickX);
            Assert.True(daemonHost.IsInjectionSuspended);

            viewModel.AddMappingCommand.Execute(null);
            daemonHost.PublishInputSample("pad-1", GamepadState.Neutral);
            daemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));
            await WaitForCondition(() => viewModel.Editor.SelectedButton == GamepadButton.B && !viewModel.Editor.IsCapturingButton);

            Assert.True(viewModel.IsEditing);
            Assert.True(daemonHost.IsInjectionSuspended);
        }
        finally
        {
            await serverCancellation.CancelAsync();
            await acceptTask;
            serverCancellation.Dispose();
        }
    }

    [Fact]
    public async Task Interleaved_Daemon_Devices_Keep_Independent_Navigation_And_Capture_Edges()
    {
        var pipeName = $"DeziroJoy.Test.{Guid.NewGuid():N}";
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1", "Controller One");
        daemonHost.AddDevice("pad-2", "Controller Two");
        var store = new InMemoryProfileStore();
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var acceptTask = DaemonHost.ServePipeAsync(server, serverCancellation.Token, pipeName);
        await using var controller = new DaemonController(new PipeEndpoint(pipeName));

        try
        {
            await controller.ConnectAsync(TestContext.Current.CancellationToken);
            var presentation = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
            await using var viewModel = new MainWindowViewModel(presentation, store, daemon: controller, daemonRequired: true);
            await viewModel.ToggleEngineCommand.ExecuteAsync(null);
            await WaitForSuspension(daemonHost, true);
            Assert.Empty(viewModel.Controllers);

            daemonHost.PublishInputSample("pad-1", GamepadState.Neutral);
            daemonHost.PublishInputSample("pad-2", GamepadState.Neutral);
            daemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));
            await WaitForCondition(() => viewModel.SelectedSource is ButtonBinding { Button: GamepadButton.A });

            daemonHost.PublishInputSample("pad-2", GamepadState.Create(GamepadButtonMask.None, 0.6f, 0, 0, 0));
            daemonHost.PublishInputSample("pad-2", GamepadState.Create(GamepadButtonMask.B, 0.7f, 0, 0, 0));
            await WaitForCondition(() => viewModel.SelectedSource is ButtonBinding { Button: GamepadButton.B });

            daemonHost.PublishInputSample("pad-2", GamepadState.Create(GamepadButtonMask.B, 0.8f, 0, 0, 0));
            daemonHost.PublishInputSample("pad-1", GamepadState.Neutral);
            daemonHost.PublishInputSample("pad-2", GamepadState.Create(GamepadButtonMask.B, 0.9f, 0, 0, 0));
            daemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));
            await WaitForCondition(() => viewModel.SelectedSource is ButtonBinding { Button: GamepadButton.A });

            viewModel.AddMappingCommand.Execute(null);
            daemonHost.PublishInputSample("pad-1", GamepadState.Neutral);
            daemonHost.PublishInputSample("pad-2", GamepadState.Neutral);
            daemonHost.PublishInputSample("pad-2", GamepadState.Create(GamepadButtonMask.None, 0.5f, 0, 0, 0));
            daemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));
            await WaitForCondition(() => viewModel.Editor.SelectedButton == GamepadButton.A && !viewModel.Editor.IsCapturingButton);

            viewModel.Editor.ToggleCaptureButtonCommand.Execute(null);
            daemonHost.PublishInputSample("pad-1", GamepadState.Neutral);
            daemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.None, 0.5f, 0, 0, 0));
            daemonHost.PublishInputSample("pad-2", GamepadState.Create(GamepadButtonMask.B, 0.6f, 0, 0, 0));
            await WaitForCondition(() => viewModel.Editor.SelectedButton == GamepadButton.B && !viewModel.Editor.IsCapturingButton);

            Assert.True(daemonHost.IsInjectionSuspended);
        }
        finally
        {
            await serverCancellation.CancelAsync();
            await acceptTask;
            serverCancellation.Dispose();
        }
    }

    [Fact]
    public async Task Daemon_Save_Uses_The_Snapshot_Device_And_Active_Slot_Without_A_Local_Runtime()
    {
        var context = await CreateDaemonPresetViewModel();
        await using var viewModel = context.ViewModel;
        Assert.Null(viewModel.ActiveController);
        Assert.Empty(viewModel.Controllers);
        Assert.NotNull(viewModel.ActiveProfile);
        await viewModel.SwitchPresetSlotCommand.ExecuteAsync(4);
        var untouched = new Dictionary<int, string>
        {
            [1] = ProfileSerializer.Serialize(context.Presets.LoadSlot("pad-1", 1)!),
            [2] = ProfileSerializer.Serialize(context.Presets.LoadSlot("pad-1", 2)!),
            [3] = ProfileSerializer.Serialize(context.Presets.LoadSlot("pad-1", 3)!),
        };
        viewModel.ActiveProfile = viewModel.ActiveProfile with { Name = "Saved daemon slot four" };

        viewModel.SaveProfileCommand.Execute(null);

        Assert.Equal("Saved daemon slot four", context.Presets.LoadSlot("pad-1", 4)!.Name);
        foreach (var (slot, expected) in untouched)
        {
            Assert.Equal(expected, ProfileSerializer.Serialize(context.Presets.LoadSlot("pad-1", slot)!));
        }
    }

    [Fact]
    public async Task Malformed_Daemon_Profile_Blocks_Save_And_Import_Without_Writing_Any_Slot()
    {
        var context = await CreateDaemonPresetViewModel();
        await using var viewModel = context.ViewModel;
        Assert.NotNull(viewModel.ActiveProfile);
        viewModel.DaemonSnapshot = viewModel.DaemonSnapshot! with { ProfileJson = "{not-json" };
        var before = context.Presets.SlotsInUse("pad-1")
            .ToDictionary(
                slot => slot,
                slot => ProfileSerializer.Serialize(context.Presets.LoadSlot("pad-1", slot)!));
        var path = Path.Combine(Path.GetTempPath(), $"dezirojoy-malformed-import-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, ProfileSerializer.Serialize(viewModel.ActiveProfile with { Name = "Must not import" }));

        try
        {
            viewModel.SaveProfileCommand.Execute(null);
            viewModel.ImportFrom(path);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.True(viewModel.IsTransferError);
        foreach (var (slot, expected) in before)
        {
            Assert.Equal(expected, ProfileSerializer.Serialize(context.Presets.LoadSlot("pad-1", slot)!));
        }
    }

    [Fact]
    public async Task Daemon_Import_Saves_The_Active_Slot_Once_And_Does_Not_Overwrite_Slot_One()
    {
        var context = await CreateDaemonPresetViewModel();
        await using var viewModel = context.ViewModel;
        var presets = context.Presets;
        Assert.Null(viewModel.ActiveController);
        Assert.Empty(viewModel.Controllers);
        await viewModel.SwitchPresetSlotCommand.ExecuteAsync(2);
        var untouched = new Dictionary<int, string>
        {
            [1] = ProfileSerializer.Serialize(presets.LoadSlot("pad-1", 1)!),
            [3] = ProfileSerializer.Serialize(presets.LoadSlot("pad-1", 3)!),
            [4] = ProfileSerializer.Serialize(presets.LoadSlot("pad-1", 4)!),
        };
        var path = Path.Combine(Path.GetTempPath(), $"dezirojoy-daemon-import-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, ProfileSerializer.Serialize(new GamepadProfile
        {
            Name = "Imported daemon slot two",
            DeviceId = "another-device",
            Mappings = [],
        }));

        try
        {
            viewModel.ImportFrom(path);
            await WaitForCondition(() => viewModel.ActiveProfile?.Name == "Imported daemon slot two");
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal("Imported daemon slot two", presets.LoadSlot("pad-1", 2)!.Name);
        foreach (var (slot, expected) in untouched)
        {
            Assert.Equal(expected, ProfileSerializer.Serialize(presets.LoadSlot("pad-1", slot)!));
        }
    }

    [Fact]
    public async Task AddPreset_In_Daemon_Mode_Creates_A_Switchable_Persistent_Slot()
    {
        var context = await CreateDaemonPresetViewModel();
        await using var viewModel = context.ViewModel;
        var presets = context.Presets;
        Assert.Equal(4, viewModel.PresetSlots.Count);

        await viewModel.AddPresetCommand.ExecuteAsync(null);
        await WaitForCondition(() => viewModel.ActivePresetSlot == 5);

        Assert.Equal(5, viewModel.ActivePresetSlot);
        Assert.Equal(5, viewModel.PresetSlots.Count);
        Assert.Equal(5, viewModel.PresetSlots.Single(item => item.IsActive).Slot);

        // The new slot is stored on its own key and the daemon switched the app-wide slot to it.
        Assert.NotNull(presets.LoadSlot("pad-1", 5));
        Assert.Contains(5, viewModel.Editor.SpecialSlotOptions);
        Assert.Equal(5, viewModel.DaemonSnapshot!.ActivePresetSlot);
    }

    [Fact]
    public async Task Disconnecting_The_Profile_Device_Rebases_To_The_Remaining_Device_Before_Editing()
    {
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1");
        daemonHost.AddDevice("pad-2");
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        var pad1 = presets.EnsureDefaults(new GamepadDescriptor("pad-1", "Controller One")) with { Name = "One" };
        var pad2 = presets.EnsureDefaults(new GamepadDescriptor("pad-2", "Controller Two")) with { Name = "Two" };
        presets.SaveSlot("pad-1", 1, pad1);
        presets.SaveSlot("pad-2", 1, pad2);
        daemonHost.Reload("pad-1", pad1);
        daemonHost.Reload("pad-2", pad2);
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var channel = new ServerChannel(server);
        server.StateChanged += channel.Push;
        var controller = new DaemonController(new ServerEndpoint(channel));
        await controller.ConnectAsync(TestContext.Current.CancellationToken);
        var presentation = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
        await using var viewModel = new MainWindowViewModel(presentation, store, daemon: controller, daemonRequired: true);
        Assert.Equal("pad-1", viewModel.ActiveProfile!.DeviceId);
        await viewModel.SwitchPresetSlotCommand.ExecuteAsync(2);
        var revisionBeforeDisconnect = viewModel.DaemonSnapshot!.Revision;
        var pad1BeforeEdit = ProfileSerializer.Serialize(presets.LoadSlot("pad-1", 2)!);

        viewModel.AddMappingCommand.Execute(null);
        daemonHost.RemoveDevice("pad-1");
        await WaitForCondition(() => viewModel.DaemonSnapshot?.Devices.SingleOrDefault()?.DeviceId == "pad-2");

        Assert.Equal(revisionBeforeDisconnect, viewModel.DaemonSnapshot.Revision);
        Assert.True(viewModel.IsEditing);
        Assert.Equal("pad-1", viewModel.ActiveProfile.DeviceId);
        viewModel.CancelEditorCommand.Execute(null);
        await WaitForCondition(() => viewModel.ActiveProfile?.DeviceId == "pad-2");
        Assert.Equal(2, viewModel.ActivePresetSlot);

        daemonHost.ReloadedDevices.Clear();
        var applied = await viewModel.ApplyProfile(viewModel.ActiveProfile with { Name = "Edited remaining pad" });

        Assert.True(applied);
        Assert.Equal("pad-2", Assert.Single(daemonHost.ReloadedDevices));
        Assert.Equal("Edited remaining pad", presets.LoadSlot("pad-2", 2)!.Name);
        Assert.Equal(pad1BeforeEdit, ProfileSerializer.Serialize(presets.LoadSlot("pad-1", 2)!));
        Assert.Equal(revisionBeforeDisconnect + 1, viewModel.DaemonSnapshot!.Revision);
    }

    [Fact]
    public async Task Switching_A_Preset_In_Daemon_Mode_Does_Not_Wait_For_A_Local_Controller()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        // The presentation host is a NoGamepadProvider seam, so no local controller ever
        // exists here - exactly the state that made the picker silently dead.
        Assert.Null(viewModel.ActiveController);

        await viewModel.SwitchPresetSlotCommand.ExecuteAsync(2);

        var sent = Assert.Single(context.Channel.Sent.OfType<SwitchPresetSlotMessage>());
        Assert.Equal(2, sent.Slot);
        Assert.Equal(string.Empty, sent.DeviceId);
        Assert.False(viewModel.IsTransferError);

        var authoritative = Assert.IsType<StateChangedMessage>(
            await context.Server.ProcessAsync(new GetStateMessage("verify")));
        Assert.Equal(2, authoritative.Snapshot.ActivePresetSlot);
    }

    [Fact]
    public async Task Switching_A_Preset_In_Daemon_Mode_Adopts_The_Snapshots_Slot()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        await viewModel.SwitchPresetSlotCommand.ExecuteAsync(2);

        Assert.Equal(2, viewModel.DaemonSnapshot!.ActivePresetSlot);
        Assert.Equal(2, viewModel.ActivePresetSlot);
    }

    private static async Task WaitForCondition(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private static async Task WaitForSuspension(FakeDaemonHost host, bool expected)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(bool suspended)
        {
            if (suspended == expected)
            {
                changed.TrySetResult();
            }
        }

        host.InjectionSuspensionChanged += OnChanged;
        try
        {
            OnChanged(host.IsInjectionSuspended);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            host.InjectionSuspensionChanged -= OnChanged;
        }
    }

    private static async Task WaitForDaemonRevision(MainWindowViewModel viewModel, long revision)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MainWindowViewModel.DaemonSnapshot)
                && viewModel.DaemonSnapshot?.Revision == revision)
            {
                received.TrySetResult();
            }
        }

        viewModel.PropertyChanged += OnPropertyChanged;
        try
        {
            OnPropertyChanged(viewModel, new System.ComponentModel.PropertyChangedEventArgs(nameof(MainWindowViewModel.DaemonSnapshot)));
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            viewModel.PropertyChanged -= OnPropertyChanged;
        }
    }

    private static async Task<(MainWindowViewModel ViewModel, PresetStore Presets)> CreateDaemonPresetViewModel()
    {
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1");
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.EnsureDefaults(new GamepadDescriptor("pad-1", "Xbox Wireless Controller"));
        daemonHost.Reload("pad-1", presets.LoadSlot("pad-1", 1)!);
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var channel = new ServerChannel(server);
        server.StateChanged += channel.Push;
        server.InputSampled += channel.Push;
        var controller = new DaemonController(new ServerEndpoint(channel));
        await controller.ConnectAsync(TestContext.Current.CancellationToken);
        var presentation = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
        var viewModel = new MainWindowViewModel(presentation, store, daemon: controller, daemonRequired: true);
        return (viewModel, presets);
    }

    private static async Task<(MainWindowViewModel ViewModel, FakeDaemonHost DaemonHost, ServerChannel Channel, DaemonServer Server)> CreateDaemonViewModel()
    {
        var daemonHost = new FakeDaemonHost();
        var store = new InMemoryProfileStore();
        var statePath = Path.Combine(
            Path.GetTempPath(),
            "dezirojoy-vm-daemon-tests",
            Guid.NewGuid().ToString("N"),
            "state.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var channel = new ServerChannel(server);
        server.StateChanged += channel.Push;
        server.InputSampled += channel.Push;
        var controller = new DaemonController(new ServerEndpoint(channel));
        await controller.ConnectAsync(TestContext.Current.CancellationToken);

        var presentation = new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store);
        var viewModel = new MainWindowViewModel(
            presentation,
            store,
            daemon: controller,
            daemonRequired: true);
        return (viewModel, daemonHost, channel, server);
    }

    private sealed class PipeEndpoint(string pipeName) : IDaemonEndpoint
    {
        public NamedPipeDaemonChannel? LastChannel { get; private set; }

        public Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(TimeSpan.FromSeconds(5), cancellationToken);
                LastChannel = new NamedPipeDaemonChannel(pipe);
                return LastChannel;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }
    }

    private sealed class ServerEndpoint(ServerChannel channel) : IDaemonEndpoint
    {
        public bool Ready { get; set; } = true;

        public Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken) => Task.FromResult(Ready);

        public Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
            => Task.FromResult<IDaemonChannel>(channel);
    }

    private sealed class ServerChannel(DaemonServer server) : IDaemonChannel
    {
        private readonly object _gate = new();
        private readonly Queue<IpcMessage> _incoming = new();
        private TaskCompletionSource<IpcMessage>? _waiting;
        private bool _closed;

        /// <summary>Every message the client sent, in order, so a test can assert what reached the daemon.</summary>
        public List<IpcMessage> Sent { get; } = [];

        public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
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

        private async Task<IpcMessage> AwaitReceiveAsync(Task<IpcMessage> wait, CancellationToken cancellationToken)
        {
            await wait.WaitAsync(cancellationToken);
            return await ReceiveAsync(cancellationToken);
        }
    }
}
