using System.IO.Pipes;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.Tests.Ipc;
using DeziroJoy.App.ViewModels;
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

        private async Task<IpcMessage> AwaitReceiveAsync(Task<IpcMessage> wait, CancellationToken cancellationToken)
        {
            await wait.WaitAsync(cancellationToken);
            return await ReceiveAsync(cancellationToken);
        }
    }
}
