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

        await WaitForMappings(context, 1);

        var row = Assert.Single(viewModel.Mappings);
        Assert.Contains("不支持的映射", row.TargetText);
        Assert.Contains("hapticPattern", row.TargetText);
        Assert.False(viewModel.IsTransferError);

        // An unrelated edit re-saves the profile through the daemon: the unknown target has
        // to survive that trip, or the user's mapping would be gone on the next restart.
        // The toggle commits fire-and-forget, so the daemon's own publication is the
        // authoritative signal that the round-trip completed. Waiting on it - before waiting
        // for the view model to catch up - splits the one blind wait into the two things that
        // can actually break: the edit never reaching the daemon, and the daemon answering
        // while the view never adopts it. A timeout now names which half failed instead of
        // expiring with nothing to say.
        row.IsEnabled = false;
        await WaitForDaemonRevision(context, 2);
        await WaitForRevision(context, 2);

        var afterEdit = Assert.IsType<StateChangedMessage>(
            await context.Server.ProcessAsync(new GetStateMessage("probe")));
        Assert.Contains("hapticPattern", afterEdit.Snapshot.ProfileJson);

        // And the row still deletes: the mapping this build cannot read is removable like
        // any other, which is what an unreadable target used to make impossible. The delete
        // is a second fire-and-forget round-trip and gets the same two-sided wait as the
        // toggle: the daemon's publication of revision 3, the view model's adoption of it,
        // and only then the table's final count. Waiting for the count alone accepted a
        // transient empty - the delete's local apply refresh empties the table before the
        // daemon's own snapshot has been adopted, and on a loaded runner that snapshot can
        // land late and put the row back after the wait had already passed.
        row.DeleteCommand.Execute(null);
        await WaitForDaemonRevision(context, 3);
        await WaitForRevision(context, 3);
        await WaitForMappings(context, 0);

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

        // The delete gets the same two-sided wait as the toggle above: the restored body
        // was adopted at revision 4, so the delete lands as revision 5, and the table's
        // final count only means "deleted" once the view model has adopted that revision -
        // a count seen earlier can be the pre-delete table mid-refresh.
        row.DeleteCommand.Execute(null);
        await WaitForDaemonRevision(context, 5);
        await WaitForRevision(context, 5);
        await WaitForMappings(context, 0);

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

    private static async Task<DaemonTestContext> CreateDaemonViewModel(DaemonState? restored = null)
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
        return new DaemonTestContext(viewModel, daemonHost, channel, server, controller);
    }

    // The daemon round-trip is marshalled back through the test's synchronization context and
    // runs on the thread pool; on a loaded CI runner those continuations can be delayed well
    // past a tight fuse. The budget is generous so the wait absorbs scheduler contention rather
    // than failing a correct flow - a revision that never arrives still times out and fails,
    // and the wait that expires now reports which half of the round-trip stalled instead of
    // raising a bare TimeoutException.
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(30);

    private static async Task WaitForMappings(DaemonTestContext context, int count)
    {
        var viewModel = context.ViewModel;
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
                await filled.Task.WaitAsync(WaitBudget, TestContext.Current.CancellationToken);
            }
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"the mapping table never showed {count} row(s). {await DescribeAsync(context)}");
        }
        finally
        {
            viewModel.Mappings.CollectionChanged -= OnCollectionChanged;
        }

        Assert.Equal(count, viewModel.Mappings.Count);
    }

    /// <summary>
    /// Waits until the daemon itself has published a snapshot at or past <paramref name="revision"/>.
    /// This is the authoritative half of an edit's round-trip: the server publishes from the
    /// thread that processed the request, before the receive loop, the controller and the view
    /// model ever see it, so completing here proves the edit landed and was answered - it says
    /// nothing about whether the GUI caught up, which is what the view-model wait is for.
    /// </summary>
    private static async Task WaitForDaemonRevision(DaemonTestContext context, long revision)
    {
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateChanged(StateChangedMessage message)
        {
            if (message.Snapshot.Revision >= revision)
            {
                published.TrySetResult();
            }
        }

        context.Server.StateChanged += OnStateChanged;
        try
        {
            // Seed from the daemon's own state before waiting: a revision that was published
            // before this call - one that landed while earlier steps were still running - must
            // not be waited for a second time.
            var current = Assert.IsType<StateChangedMessage>(
                await context.Server.ProcessAsync(new GetStateMessage("wait-seed")));
            if (current.Snapshot.Revision >= revision)
            {
                return;
            }

            await published.Task.WaitAsync(WaitBudget, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"the daemon never published revision {revision}: the edit did not round-trip. {await DescribeAsync(context)}");
        }
        finally
        {
            context.Server.StateChanged -= OnStateChanged;
        }
    }

    private static async Task WaitForRevision(DaemonTestContext context, long revision)
    {
        var viewModel = context.ViewModel;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            // At-least rather than exact: the edit must have landed and bumped the revision,
            // and a snapshot that already moved past it still proves the round-trip completed.
            if (args.PropertyName == nameof(MainWindowViewModel.DaemonSnapshot)
                && viewModel.DaemonSnapshot?.Revision >= revision)
            {
                reached.TrySetResult();
            }
        }

        viewModel.PropertyChanged += OnPropertyChanged;
        try
        {
            OnPropertyChanged(viewModel, new System.ComponentModel.PropertyChangedEventArgs(nameof(MainWindowViewModel.DaemonSnapshot)));
            await reached.Task.WaitAsync(WaitBudget, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"the view model never adopted revision {revision}. {await DescribeAsync(context)}");
        }
        finally
        {
            viewModel.PropertyChanged -= OnPropertyChanged;
        }
    }

    /// <summary>
    /// Everything a stalled wait needs to say why, gathered from both ends of the round-trip:
    /// the revision each side holds, whether the connection is still up and what it last
    /// complained about, what the view model is showing the user, and whether the machine the
    /// wait ran on had any threads left to run it on.
    /// </summary>
    private static async Task<string> DescribeAsync(DaemonTestContext context)
    {
        var viewModel = context.ViewModel;
        var controller = context.Controller;
        return $"daemon revision {await ProbeDaemonRevisionAsync(context)}, " +
            $"view model revision {Describe(viewModel.DaemonSnapshot?.Revision)}, " +
            $"connection {controller.State} (controller revision {controller.Revision}, " +
            $"last error {controller.LastError ?? "none"}), " +
            $"transfer error {viewModel.IsTransferError} (\"{viewModel.TransferMessage}\"), " +
            $"mappings in view {viewModel.Mappings.Count}, " +
            $"thread pool threads {System.Threading.ThreadPool.ThreadCount} " +
            $"pending {System.Threading.ThreadPool.PendingWorkItemCount}";
    }

    private static async Task<string> ProbeDaemonRevisionAsync(DaemonTestContext context)
    {
        try
        {
            var probe = Assert.IsType<StateChangedMessage>(
                await context.Server.ProcessAsync(new GetStateMessage("diagnostic")));
            return probe.Snapshot.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception exception)
        {
            return $"unreadable ({exception.Message})";
        }
    }

    private static string Describe(long? revision)
        => revision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";

    private sealed record DaemonTestContext(
        MainWindowViewModel ViewModel,
        FakeDaemonHost Host,
        ServerChannel Channel,
        DaemonServer Server,
        DaemonController Controller);

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
