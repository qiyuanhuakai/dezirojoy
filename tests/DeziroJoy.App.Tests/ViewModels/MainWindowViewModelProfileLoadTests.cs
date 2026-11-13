using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.Tests.Ipc;
using DeziroJoy.App.ViewModels;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The daemon's profile body is the GUI's only view of the mapping table, and it is not
/// always a body this build can read whole: a resident daemon from another version, or a
/// hand-edited profile, can name a target no type here claims - the shape that produced
/// "Read unrecognized type discriminator id specialKey" - and a body that failed to parse
/// used to leave the table empty and undeletable. These pin both halves: an unknown target
/// still loads and stays editable, and a damaged body still shows what it can read.
/// </summary>
public sealed class MainWindowViewModelProfileLoadTests
{
    /// <summary>
    /// A profile a newer build wrote: the target's "$type" discriminator names a type no
    /// type in this build claims, and the body carries a field this build has never heard of.
    /// </summary>
    private const string FutureTargetJson = """
    {
      "SchemaVersion": 2,
      "Name": "future",
      "DeviceId": "pad-1",
      "Mappings": [
        {
          "Source": { "$type": "button", "Button": "RightBumper" },
          "Target": { "$type": "hapticPattern", "Intensity": 3 }
        }
      ]
    }
    """;

    /// <summary>
    /// The same body with one mapping this build cannot read at all - a source naming a
    /// button that does not exist - sitting beside one it can.
    /// </summary>
    private const string DamagedBodyJson = """
    {
      "SchemaVersion": 2,
      "Name": "damaged",
      "DeviceId": "pad-1",
      "Mappings": [
        {
          "Source": { "$type": "button", "Button": "NotAButton" },
          "Target": { "$type": "key", "Key": "Escape" }
        },
        {
          "Source": { "$type": "button", "Button": "A" },
          "Target": { "$type": "key", "Key": "Enter" }
        }
      ]
    }
    """;

    [Fact]
    public async Task A_Profile_With_An_Unknown_Target_Displays_Deletes_And_Re_Saves_Its_Body()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        var applied = Assert.IsType<MutationAckMessage>(await context.Server.ProcessAsync(
            new ApplyConfigurationMessage("req-1", 0, "future", FutureTargetJson)));
        Assert.True(applied.Accepted);

        await WaitForMappings(viewModel, 1);

        var row = Assert.Single(viewModel.Mappings);
        Assert.Contains("不支持的映射", row.TargetText);
        Assert.Contains("hapticPattern", row.TargetText);
        Assert.False(viewModel.IsTransferError);

        // An unrelated edit re-saves the profile through the daemon: the unknown target has
        // to survive that trip, or the user's mapping would be gone on the next restart.
        row.IsEnabled = false;
        await WaitForRevision(viewModel, 2);

        var afterEdit = Assert.IsType<StateChangedMessage>(
            await context.Server.ProcessAsync(new GetStateMessage("probe")));
        Assert.Contains("hapticPattern", afterEdit.Snapshot.ProfileJson);

        // And the row still deletes: the mapping this build cannot read is removable like
        // any other, which is what an unreadable target used to make impossible.
        row.DeleteCommand.Execute(null);
        await WaitForMappings(viewModel, 0);

        var afterDelete = Assert.IsType<StateChangedMessage>(
            await context.Server.ProcessAsync(new GetStateMessage("probe")));
        Assert.DoesNotContain("hapticPattern", afterDelete.Snapshot.ProfileJson);
        Assert.DoesNotContain("RightBumper", afterDelete.Snapshot.ProfileJson);
    }

    [Fact]
    public async Task A_Restored_Body_With_One_Unreadable_Mapping_Shows_The_Rest_And_Deletes()
    {
        var context = await CreateDaemonViewModel(
            new DaemonState(MappingEnabled: true, "damaged", DamagedBodyJson, Revision: 4));
        await using var viewModel = context.ViewModel;

        // The daemon resumed a body it cannot express in full and handed it over whole: the
        // GUI reads what it can, says what it could not, and stays usable.
        Assert.True(viewModel.IsTransferError);
        Assert.Contains("守护进程配置", viewModel.TransferMessage);
        Assert.Contains("无法解析", viewModel.TransferMessage);

        var row = Assert.Single(viewModel.Mappings);
        Assert.Contains("Enter", row.TargetText);

        row.DeleteCommand.Execute(null);
        await WaitForMappings(viewModel, 0);

        var afterDelete = Assert.IsType<StateChangedMessage>(
            await context.Server.ProcessAsync(new GetStateMessage("probe")));
        Assert.DoesNotContain("NotAButton", afterDelete.Snapshot.ProfileJson);
    }

    [Fact]
    public async Task A_Restored_Body_That_Is_Not_Json_Leaves_The_Table_Usable()
    {
        var context = await CreateDaemonViewModel(
            new DaemonState(MappingEnabled: true, "truncated", """{ "Name": "truncated" """, Revision: 4));
        await using var viewModel = context.ViewModel;

        Assert.True(viewModel.IsTransferError);
        Assert.Contains("守护进程配置", viewModel.TransferMessage);
        Assert.Contains("无法解析", viewModel.TransferMessage);
        Assert.Empty(viewModel.Mappings);
    }

    private static async Task<(MainWindowViewModel ViewModel, FakeDaemonHost Host, ServerChannel Channel, DaemonServer Server)>
        CreateDaemonViewModel(DaemonState? restored = null)
    {
        var daemonHost = new FakeDaemonHost { HasController = false };
        var store = new InMemoryProfileStore();
        var statePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dezirojoy-vm-profile-load-tests",
            Guid.NewGuid().ToString("N"),
            "state.json");
        var stateStore = new DaemonStateStore(statePath);
        if (restored is not null)
        {
            stateStore.Save(restored);
        }

        var server = new DaemonServer(daemonHost, store, stateStore);
        var channel = new ServerChannel(server);
        server.StateChanged += channel.Push;
        var controller = new DaemonController(new ServerEndpoint(channel));
        await controller.ConnectAsync(TestContext.Current.CancellationToken);

        var presentation = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
        var viewModel = new MainWindowViewModel(
            presentation,
            store,
            daemon: controller,
            daemonRequired: true);
        return (viewModel, daemonHost, channel, server);
    }

    private static async Task WaitForMappings(MainWindowViewModel viewModel, int count)
    {
        // The mapping table is an ObservableCollection, so a refresh reports
        // CollectionChanged rather than a property change: the wait hangs off the collection.
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        {
            if (viewModel.Mappings.Count == count)
            {
                filled.TrySetResult();
            }
        }

        viewModel.Mappings.CollectionChanged += OnCollectionChanged;
        try
        {
            if (viewModel.Mappings.Count != count)
            {
                await filled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            viewModel.Mappings.CollectionChanged -= OnCollectionChanged;
        }

        Assert.Equal(count, viewModel.Mappings.Count);
    }

    private static async Task WaitForRevision(MainWindowViewModel viewModel, long revision)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MainWindowViewModel.DaemonSnapshot)
                && viewModel.DaemonSnapshot?.Revision == revision)
            {
                reached.TrySetResult();
            }
        }

        viewModel.PropertyChanged += OnPropertyChanged;
        try
        {
            OnPropertyChanged(viewModel, new System.ComponentModel.PropertyChangedEventArgs(nameof(MainWindowViewModel.DaemonSnapshot)));
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

        private async Task<IpcMessage> AwaitReceiveAsync(Task<IpcMessage> wait, CancellationToken cancellationToken)
        {
            await wait.WaitAsync(cancellationToken);
            return await ReceiveAsync(cancellationToken);
        }
    }
}
