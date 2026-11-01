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

    private static async Task<(MainWindowViewModel ViewModel, FakeDaemonHost DaemonHost, ServerChannel Channel)> CreateDaemonViewModel()
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
        return (viewModel, daemonHost, channel);
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
