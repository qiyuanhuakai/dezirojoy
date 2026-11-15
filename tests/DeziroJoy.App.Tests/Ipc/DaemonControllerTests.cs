using System.IO.Pipes;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Ipc;

public sealed class DaemonControllerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Connect_Performs_Ready_Handshake_And_Full_State_In_Order()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);

        var snapshot = await controller.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DaemonConnectionState.Connected, controller.State);
        Assert.Equal(0, snapshot.Sequence);
        Assert.Equal(1, endpoint.EnsureCalls);
        Assert.Equal(1, endpoint.OpenCalls);
        Assert.True(endpoint.LastAllowLaunch);
        Assert.Collection(
            endpoint.Channel.Sent,
            message => Assert.True(Assert.IsType<HelloMessage>(message).SubscribeInputSamples),
            message => Assert.IsType<GetStateMessage>(message));
    }

    [Fact]
    public async Task Apply_And_Start_Use_The_Daemons_Current_Revision()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);

        endpoint.Channel.OnSend = message => message switch
        {
            ApplyConfigurationMessage apply => new MutationAckMessage(apply.RequestId, true, 1, 1),
            StartMessage start => new MutationAckMessage(start.RequestId, true, 1, 2),
            _ => null,
        };

        var applied = await controller.ApplyAsync("Arena", "{}", controller.Revision, controller.DaemonInstanceId!, TestContext.Current.CancellationToken);
        var started = await controller.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MutationOutcome.Accepted, applied.Outcome);
        Assert.Equal(MutationOutcome.Accepted, started.Outcome);
        Assert.Equal(0, Assert.Single(endpoint.Channel.Sent.OfType<ApplyConfigurationMessage>()).ExpectedRevision);
        Assert.Equal(1, Assert.Single(endpoint.Channel.Sent.OfType<StartMessage>()).ExpectedRevision);
    }

    [Fact]
    public async Task SwitchPresetSlot_Sends_The_Message_And_Returns_The_Ack()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        endpoint.Channel.OnSend = message => message is SwitchPresetSlotMessage request
            ? new MutationAckMessage(request.RequestId, true, 1, 1)
            : null;

        var result = await controller.SwitchPresetSlotAsync("pad-1", 2, TestContext.Current.CancellationToken);

        Assert.Equal(MutationOutcome.Accepted, result.Outcome);
        var sent = Assert.Single(endpoint.Channel.Sent.OfType<SwitchPresetSlotMessage>());
        Assert.Equal("pad-1", sent.DeviceId);
        Assert.Equal(2, sent.Slot);
    }

    [Fact]
    public async Task SetNativeGamepadPaused_Sends_The_Message_And_Returns_The_Ack()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        endpoint.Channel.OnSend = message => message is SetNativeGamepadPausedMessage request
            ? new MutationAckMessage(request.RequestId, true, 1, 1)
            : null;

        var paused = await controller.SetNativeGamepadPausedAsync(true, TestContext.Current.CancellationToken);
        var resumed = await controller.SetNativeGamepadPausedAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(MutationOutcome.Accepted, paused.Outcome);
        Assert.Equal(MutationOutcome.Accepted, resumed.Outcome);
        Assert.Equal(
            [true, false],
            endpoint.Channel.Sent.OfType<SetNativeGamepadPausedMessage>().Select(message => message.Paused));
    }

    [Fact]
    public async Task A_Revision_Conflict_Is_Refused_And_Never_Replayed()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        endpoint.Channel.OnSend = message => message is ApplyConfigurationMessage apply
            ? new MutationAckMessage(apply.RequestId, false, 7, 4, IpcErrorCodes.RevisionMismatch, "stale")
            : null;

        var result = await controller.ApplyAsync("Arena", "{}", controller.Revision, controller.DaemonInstanceId!, TestContext.Current.CancellationToken);

        Assert.Equal(MutationOutcome.Refused, result.Outcome);
        Assert.Equal(IpcErrorCodes.RevisionMismatch, result.ErrorCode);
        Assert.Equal(7, result.Revision);
        Assert.Single(endpoint.Channel.Sent.OfType<ApplyConfigurationMessage>());
    }

    [Fact]
    public async Task Only_One_Mutation_Is_In_Flight()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        endpoint.Channel.OnSend = null;

        var applyTask = controller.ApplyAsync("Arena", "{}", controller.Revision, controller.DaemonInstanceId!, TestContext.Current.CancellationToken);
        var startTask = controller.StartAsync(TestContext.Current.CancellationToken);
        var apply = await endpoint.Channel.WaitForSendAsync<ApplyConfigurationMessage>(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(endpoint.Channel.Sent, message => message is StartMessage);

        endpoint.Channel.Push(new MutationAckMessage(apply.RequestId, true, 1, 1));
        var start = await endpoint.Channel.WaitForSendAsync<StartMessage>(TestContext.Current.CancellationToken);
        endpoint.Channel.Push(new MutationAckMessage(start.RequestId, true, 1, 2));

        Assert.Equal(MutationOutcome.Accepted, (await applyTask).Outcome);
        Assert.Equal(MutationOutcome.Accepted, (await startTask).Outcome);
    }

    [Fact]
    public async Task A_Queued_Apply_Keeps_Its_Original_Revision_And_Is_Not_Sent_After_A_Conflict()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        endpoint.Channel.OnSend = message => message switch
        {
            ApplyConfigurationMessage apply => new MutationAckMessage(apply.RequestId, true, 1, 2),
            _ => null,
        };

        var firstMutation = controller.SetMappingEnabledAsync(false, TestContext.Current.CancellationToken);
        var firstRequest = await endpoint.Channel.WaitForSendAsync<SetMappingEnabledMessage>(
            TestContext.Current.CancellationToken);
        var applyTask = controller.ApplyAsync("Stale", "{}", 0, "daemon-1", TestContext.Current.CancellationToken);

        var externalRevision = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.SnapshotChanged += snapshot =>
        {
            if (snapshot.Revision == 1)
            {
                externalRevision.TrySetResult();
            }
        };
        endpoint.Channel.Push(State("daemon-1", 1, revision: 1));
        await externalRevision.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        endpoint.Channel.Push(new MutationAckMessage(firstRequest.RequestId, true, 1, 2));
        var result = await applyTask;

        Assert.Equal(MutationOutcome.Refused, result.Outcome);
        Assert.Equal(IpcErrorCodes.RevisionMismatch, result.ErrorCode);
        Assert.DoesNotContain(endpoint.Channel.Sent, message => message is ApplyConfigurationMessage);
        Assert.Equal(MutationOutcome.Accepted, (await firstMutation).Outcome);

        var fromOldDaemon = await controller.ApplyAsync(
            "Stale", "{}", 1, "daemon-old", TestContext.Current.CancellationToken);
        Assert.Equal(MutationOutcome.Refused, fromOldDaemon.Outcome);
        Assert.DoesNotContain(endpoint.Channel.Sent, message => message is ApplyConfigurationMessage);
    }

    [Fact]
    public async Task Disconnect_During_A_Mutation_Returns_Unknown_Without_Replaying_Or_Stopping()
    {
        var endpoint = new FakeEndpoint();
        var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        endpoint.Channel.OnSend = null;

        var mutation = controller.ApplyAsync("Arena", "{}", controller.Revision, controller.DaemonInstanceId!, TestContext.Current.CancellationToken);
        await endpoint.Channel.WaitForSendAsync<ApplyConfigurationMessage>(TestContext.Current.CancellationToken);
        endpoint.Channel.Close();
        var result = await mutation;

        Assert.Equal(MutationOutcome.Unknown, result.Outcome);
        Assert.Single(endpoint.Channel.Sent.OfType<ApplyConfigurationMessage>());
        Assert.Equal(DaemonConnectionState.Disconnected, controller.State);

        await controller.DisposeAsync();
        Assert.DoesNotContain(endpoint.Channel.Sent, message => message is StopMessage);
    }

    [Fact]
    public async Task A_Stale_Push_Is_Dropped_And_A_Fresh_Push_Is_Adopted()
    {
        var endpoint = new FakeEndpoint { InitialSequence = 5 };
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        var adopted = new List<StateSnapshot>();
        var fresh = new TaskCompletionSource<StateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.SnapshotChanged += snapshot =>
        {
            adopted.Add(snapshot);
            if (snapshot.Sequence == 6)
            {
                fresh.TrySetResult(snapshot);
            }
        };

        endpoint.Channel.Push(State("daemon-1", 4));
        endpoint.Channel.Push(State("daemon-1", 6));
        await fresh.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(6, controller.Snapshot!.Sequence);
        Assert.Equal([6], adopted.Select(snapshot => snapshot.Sequence).ToArray());
    }

    [Fact]
    public async Task Input_Samples_Are_Cached_Stale_Frames_Dropped_And_Coalescing_Gaps_Accepted()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint, heartbeatInterval: TimeSpan.FromSeconds(10));
        await ConnectAsync(controller);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.InputSampled += sample =>
        {
            if (sample.Sequence == 5)
            {
                received.TrySetResult();
            }
        };

        endpoint.Channel.Push(new InputSampleMessage("daemon-1", 3, "pad-1", GamepadButtonMask.None, 0, 0, 0, 0));
        endpoint.Channel.Push(new InputSampleMessage("daemon-1", 2, "pad-1", GamepadButtonMask.B, 0, 0, 0, 0));
        endpoint.Channel.Push(new InputSampleMessage("daemon-1", 5, "pad-1", GamepadButtonMask.A, 0.5f, 0, 0, 0));
        await received.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        var sample = Assert.Single(controller.InputSamples);
        Assert.Equal(5, sample.Sequence);
        Assert.Equal(GamepadButtonMask.A, sample.PressedButtons);
        Assert.Equal(0.5f, sample.LeftStickX);
    }

    [Fact]
    public async Task A_Late_Authoritative_Answer_Does_Not_Roll_Back_A_Newer_Push()
    {
        var endpoint = new FakeEndpoint { InitialSequence = 5 };
        await using var controller = new DaemonController(
            endpoint,
            heartbeatInterval: TimeSpan.FromSeconds(10));
        await ConnectAsync(controller);
        var adopted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.SnapshotChanged += snapshot =>
        {
            if (snapshot.Sequence == 6)
            {
                adopted.TrySetResult();
            }
        };
        endpoint.Channel.Push(State("daemon-1", 6));
        await adopted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        endpoint.Channel.OnSend = message => message is GetStateMessage getState
            ? State("daemon-1", 5, getState.RequestId)
            : null;

        var returned = await controller.GetStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(6, returned.Sequence);
        Assert.Equal(6, controller.Snapshot!.Sequence);
    }

    [Fact]
    public async Task Concurrent_Authoritative_Continuations_Cannot_Adopt_Over_A_Newer_Push()
    {
        const int ReadCount = 128;
        var endpoint = new FakeEndpoint { InitialSequence = 5 };
        await using var controller = new DaemonController(endpoint, heartbeatInterval: TimeSpan.FromSeconds(10));
        await ConnectAsync(controller);
        endpoint.Channel.OnSend = null;
        var firstOlderAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newerPushAdopted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rolledBack = 0;
        controller.SnapshotChanged += snapshot =>
        {
            if (snapshot.Sequence == 6)
            {
                firstOlderAnswer.TrySetResult();
            }

            if (snapshot.Sequence == 7)
            {
                newerPushAdopted.TrySetResult();
            }
            else if (newerPushAdopted.Task.IsCompleted)
            {
                Interlocked.Exchange(ref rolledBack, 1);
            }
        };

        var reads = Enumerable.Range(0, ReadCount)
            .Select(_ => controller.GetStateAsync(TestContext.Current.CancellationToken))
            .ToArray();
        await endpoint.Channel.WaitForSendCountAsync<GetStateMessage>(
            ReadCount + 1,
            TestContext.Current.CancellationToken);

        var requests = endpoint.Channel.Sent.OfType<GetStateMessage>().Skip(1).ToList();
        Assert.Equal(ReadCount, requests.Count);
        foreach (var request in requests)
        {
            endpoint.Channel.Push(State("daemon-1", 6, request.RequestId));
        }

        // The older answers are in flight: the first continuation adopts one while the rest
        // are still queued behind it, which is the race the newer push has to win.
        await firstOlderAnswer.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        endpoint.Channel.Push(State("daemon-1", 7));

        // The push travels the receive loop asynchronously, so the reads completing says
        // nothing about it: wait for the adoption itself before judging the outcome.
        await newerPushAdopted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await Task.WhenAll(reads).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(7, controller.Snapshot!.Sequence);
        Assert.Equal(0, rolledBack);
    }

    [Fact]
    public async Task A_Sequence_Gap_Triggers_One_Authoritative_Reread()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        endpoint.Channel.OnSend = message => message is GetStateMessage getState
            ? State("daemon-1", 2, getState.RequestId)
            : null;
        var reread = new TaskCompletionSource<StateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.SnapshotChanged += snapshot =>
        {
            if (snapshot.Sequence == 2)
            {
                reread.TrySetResult(snapshot);
            }
        };

        endpoint.Channel.Push(State("daemon-1", 2));
        await reread.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(2, controller.Snapshot!.Sequence);
        Assert.Equal(2, endpoint.Channel.Sent.OfType<GetStateMessage>().Count());
    }

    [Fact]
    public async Task A_Foreign_Instance_Ends_The_Session_Without_Adopting_Its_State()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Disconnected += () => disconnected.TrySetResult();

        endpoint.Channel.Push(State("daemon-2", 1));
        await disconnected.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(DaemonConnectionState.Disconnected, controller.State);
        Assert.Equal(0, controller.Snapshot!.Sequence);
    }

    [Fact]
    public async Task Heartbeat_Reads_Authoritative_State_Without_Replaying_Mutations()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(
            endpoint,
            heartbeatInterval: TimeSpan.FromMilliseconds(10),
            reconnectDelay: TimeSpan.FromSeconds(10));
        await ConnectAsync(controller);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.SnapshotChanged += _ => refreshed.TrySetResult();

        await refreshed.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.True(endpoint.Channel.Sent.OfType<GetStateMessage>().Count() >= 2);
        Assert.DoesNotContain(endpoint.Channel.Sent, message => message is ApplyConfigurationMessage);
        Assert.Equal(DaemonConnectionState.Connected, controller.State);
    }

    [Fact]
    public async Task Disconnect_Automatically_Reconnects_And_Adopts_The_New_Daemon_Instance()
    {
        var endpoint = new FakeEndpoint { RenewChannelOnOpen = true };
        await using var controller = new DaemonController(
            endpoint,
            heartbeatInterval: TimeSpan.FromSeconds(10),
            reconnectDelay: TimeSpan.FromMilliseconds(10));
        await ConnectAsync(controller);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Connected += () =>
        {
            if (controller.DaemonInstanceId == "daemon-2")
            {
                reconnected.TrySetResult();
            }
        };
        endpoint.InstanceId = "daemon-2";

        endpoint.Channel.Close();
        await reconnected.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(2, endpoint.OpenCalls);
        Assert.Equal(DaemonConnectionState.Connected, controller.State);
        Assert.Equal("daemon-2", controller.DaemonInstanceId);
        Assert.Equal(0, controller.Snapshot!.Sequence);
    }

    [Fact]
    public async Task Graceful_Shutdown_Returns_Accepted_And_Does_Not_Auto_Reconnect()
    {
        var endpoint = new FakeEndpoint { RenewChannelOnOpen = true };
        await using var controller = new DaemonController(
            endpoint,
            reconnectDelay: TimeSpan.FromMilliseconds(10));
        await ConnectAsync(controller);
        endpoint.Channel.OnSend = message => message is ShutdownDaemonMessage shutdown
            ? new MutationAckMessage(shutdown.RequestId, true, 0, 0)
            : null;

        Assert.True(await controller.RequestShutdownAsync(TestContext.Current.CancellationToken));
        endpoint.Channel.Close();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Single(endpoint.Channel.Sent.OfType<ShutdownDaemonMessage>());
        Assert.Equal(1, endpoint.OpenCalls);
    }

    [Fact]
    public async Task A_Daemon_Still_Starting_Is_Waited_For_With_Probe_Only_Retries()
    {
        // The daemon the supervisor just spawned opens its pipe a few hundred milliseconds
        // after the process starts, so the first probe - the same single 250 ms probe the
        // endpoint makes - reads "no daemon". The connect must wait for it, and every wait
        // probe must be probe-only: a retry that relaunched would prompt (UAC on Windows)
        // for a daemon that is already on its way.
        var endpoint = new FakeEndpoint { BecomeReadyAfterEnsureCalls = 4 };
        await using var controller = new DaemonController(endpoint, startupGrace: TimeSpan.FromSeconds(3));

        var snapshot = await controller.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DaemonConnectionState.Connected, controller.State);
        Assert.Equal(0, snapshot.Sequence);
        Assert.Equal([true, false, false, false], endpoint.AllowLaunchCalls);
    }

    [Fact]
    public async Task A_Daemon_That_Never_Answers_Fails_After_The_Startup_Grace()
    {
        // The wait is bounded in real time: a machine with no daemon coming still gets its
        // error window, after the grace rather than after one probe. Three probes because the
        // grace is checked after each attempt in elapsed time - the second lands at ~250 ms,
        // still inside a 300 ms grace, and the third at ~500 ms ends it.
        var endpoint = new FakeEndpoint { Ready = false };
        await using var controller = new DaemonController(
            endpoint,
            startupGrace: TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<DaemonUnavailableException>(
            () => controller.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(DaemonConnectionState.Disconnected, controller.State);
        Assert.Equal(3, endpoint.EnsureCalls);
        Assert.Equal(0, endpoint.OpenCalls);
        Assert.Equal([true, false, false], endpoint.AllowLaunchCalls);
    }

    [Fact]
    public async Task A_Probe_Slower_Than_The_Grace_Spends_Real_Time_Not_Attempts()
    {
        // The grace is elapsed time, not a fictional counter: a probe that itself costs more
        // than the whole grace ends the wait after that one probe, instead of buying another
        // interval of patience per attempt and running the wait far past its budget.
        var endpoint = new FakeEndpoint { Ready = false, ProbeDelay = TimeSpan.FromMilliseconds(500) };
        await using var controller = new DaemonController(
            endpoint,
            startupGrace: TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<DaemonUnavailableException>(
            () => controller.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(DaemonConnectionState.Disconnected, controller.State);
        Assert.Equal(1, endpoint.EnsureCalls);
        Assert.Equal(0, endpoint.OpenCalls);
        Assert.Equal([true], endpoint.AllowLaunchCalls);
    }

    [Fact]
    public async Task A_Slow_Endpoint_That_Becomes_Ready_Connects_Within_The_Real_Grace()
    {
        // The other side of the same accounting: a slow probe that turns ready inside the
        // grace still connects, and only the first attempt may launch.
        var endpoint = new FakeEndpoint
        {
            BecomeReadyAfterEnsureCalls = 2,
            ProbeDelay = TimeSpan.FromMilliseconds(400),
        };
        await using var controller = new DaemonController(endpoint, startupGrace: TimeSpan.FromSeconds(3));

        var snapshot = await controller.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DaemonConnectionState.Connected, controller.State);
        Assert.Equal(0, snapshot.Sequence);
        Assert.Equal(2, endpoint.EnsureCalls);
        Assert.Equal([true, false], endpoint.AllowLaunchCalls);
    }

    [Fact]
    public async Task An_Open_Reset_By_A_Replaced_Daemon_Instance_Is_Retried_Within_The_Grace()
    {
        // The daemon serves one connection per pipe instance and replaces it afterwards, so
        // an open that lands on the instance the readiness probe just left behind is reset
        // by the daemon. The connect must ride the same grace the probe does instead of
        // opening the window in the connection-error state over a daemon that is serving.
        var endpoint = new FakeEndpoint { BecomeReadyAfterEnsureCalls = 2, FailFirstOpens = 1 };
        await using var controller = new DaemonController(endpoint, startupGrace: TimeSpan.FromSeconds(3));

        var snapshot = await controller.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DaemonConnectionState.Connected, controller.State);
        Assert.Equal(0, snapshot.Sequence);
        Assert.Equal(2, endpoint.OpenCalls);
        Assert.Equal([true, false, false], endpoint.AllowLaunchCalls);
    }

    [Fact]
    public async Task A_Daemon_Whose_Pipe_Opens_Late_Is_Connected_Instead_Of_Reported_Missing()
    {
        // The reported failure, end to end over a real pipe: the daemon process exists and
        // its engine is up, but the mapping pipe opens only after the endpoint's probe
        // window. Hermetic - a GUID pipe name no daemon on this machine can answer - and
        // the serve starts late on purpose, so the first probe must fail.
        var pipeName = $"DeziroJoy.Test.{Guid.NewGuid():N}";
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(
            new FakeDaemonHost(),
            new InMemoryProfileStore(),
            new DaemonStateStore(statePath));
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var serve = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken);
            await DaemonHost.ServePipeAsync(server, cancellation.Token, pipeName);
        }, TestContext.Current.CancellationToken);

        try
        {
            await using var controller = new DaemonController(
                new LatePipeEndpoint(pipeName),
                startupGrace: TimeSpan.FromSeconds(3));

            var snapshot = await controller.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.Equal(DaemonConnectionState.Connected, controller.State);
            Assert.NotNull(controller.DaemonInstanceId);
            Assert.NotNull(snapshot);
        }
        finally
        {
            await cancellation.CancelAsync();
            await serve;
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// The production endpoint's shape: probe the pipe, and on Linux never launch - the
    /// supervisor owns the launch, exactly as <see cref="NamedPipeDaemonEndpoint"/> does
    /// off Windows.
    /// </summary>
    private sealed class LatePipeEndpoint(string pipeName) : IDaemonEndpoint
    {
        public async Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken)
        {
            try
            {
                var pipe = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await using (pipe)
                {
                    await pipe.ConnectAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
                    return pipe.IsConnected;
                }
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        public async Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
        {
            var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(TimeSpan.FromSeconds(5), cancellationToken);
                return new NamedPipeDaemonChannel(pipe);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }
    }

    private static async Task ConnectAsync(DaemonController controller)
    {
        await controller.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DaemonConnectionState.Connected, controller.State);
    }

    private static StateChangedMessage State(string instanceId, long sequence, string? requestId = null, long revision = 0)
        => new(instanceId, new StateSnapshot(sequence, revision, false, true, 4242, "high", "Default", null, 0, 0, false), requestId);

    private sealed class FakeEndpoint : IDaemonEndpoint
    {
        public FakeDaemonChannel Channel { get; private set; }

        public bool Ready { get; set; } = true;

        /// <summary>
        /// Becomes ready once this many <see cref="EnsureReadyAsync"/> calls have been made;
        /// 0 leaves <see cref="Ready"/> authoritative. Stands in for a daemon whose pipe
        /// opens a few hundred milliseconds after the process starts.
        /// </summary>
        public int BecomeReadyAfterEnsureCalls { get; set; }

        /// <summary>
        /// How long one readiness probe takes to answer. Stands in for an endpoint whose
        /// probe costs more than one probe interval, so the grace is spent in real time.
        /// </summary>
        public TimeSpan ProbeDelay { get; set; }

        /// <summary>Every allowLaunch flag the controller passed, in order.</summary>
        public List<bool> AllowLaunchCalls { get; } = [];

        /// <summary>
        /// Throws from this many <see cref="OpenChannelAsync"/> calls before letting one
        /// through, as the daemon's replaced pipe instance resets a connect that landed on
        /// it. 0 opens normally.
        /// </summary>
        public int FailFirstOpens { get; set; }

        public bool RenewChannelOnOpen { get; set; }

        public string InstanceId { get; set; } = "daemon-1";

        public long InitialSequence { get; set; }

        public int EnsureCalls { get; private set; }

        public int OpenCalls { get; private set; }

        public bool LastAllowLaunch { get; private set; }

        public FakeEndpoint()
        {
            Channel = CreateChannel();
        }

        private FakeDaemonChannel CreateChannel()
        {
            return new FakeDaemonChannel
            {
                OnSend = message => message switch
                {
                    HelloMessage => new HelloAckMessage(IpcContract.ProtocolVersion, true, InstanceId, 0),
                    GetStateMessage getState => State(InstanceId, InitialSequence, getState.RequestId),
                    _ => null,
                },
            };
        }

        public async Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken)
        {
            EnsureCalls++;
            LastAllowLaunch = allowLaunch;
            AllowLaunchCalls.Add(allowLaunch);
            if (ProbeDelay > TimeSpan.Zero)
            {
                await Task.Delay(ProbeDelay, cancellationToken).ConfigureAwait(false);
            }

            var ready = BecomeReadyAfterEnsureCalls > 0
                ? EnsureCalls >= BecomeReadyAfterEnsureCalls
                : Ready;
            return ready;
        }

        public Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
        {
            OpenCalls++;
            if (FailFirstOpens > 0)
            {
                FailFirstOpens--;
                throw new IOException("broken pipe");
            }

            if (RenewChannelOnOpen && OpenCalls > 1)
            {
                Channel = CreateChannel();
            }

            return Task.FromResult<IDaemonChannel>(Channel);
        }
    }

    private sealed class FakeDaemonChannel : IDaemonChannel
    {
        private readonly object _gate = new();
        private readonly Queue<IpcMessage> _incoming = new();
        private TaskCompletionSource<IpcMessage>? _waiting;
        private bool _closed;

        public List<IpcMessage> Sent { get; } = [];

        public Func<IpcMessage, IpcMessage?>? OnSend { get; set; }

        private event Action<IpcMessage>? SentObserved;

        public Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    throw new EndOfStreamException();
                }

                Sent.Add(message);
            }

            SentObserved?.Invoke(message);
            var response = OnSend?.Invoke(message);
            if (response is not null)
            {
                Push(response);
            }

            return Task.CompletedTask;
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

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                _waiting?.TrySetException(new EndOfStreamException());
                _waiting = null;
            }
        }

        public async Task<T> WaitForSendAsync<T>(CancellationToken cancellationToken) where T : IpcMessage
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(IpcMessage message)
            {
                if (message is T expected)
                {
                    completion.TrySetResult(expected);
                }
            }

            SentObserved += Handler;
            try
            {
                lock (_gate)
                {
                    if (Sent.OfType<T>().FirstOrDefault() is { } existing)
                    {
                        return existing;
                    }
                }

                return await completion.Task.WaitAsync(TestTimeout, cancellationToken);
            }
            finally
            {
                SentObserved -= Handler;
            }
        }

        /// <summary>
        /// Waits until at least <paramref name="count"/> messages of type <typeparamref name="T"/>
        /// have been sent. Event-driven so a caller never has to poll or sleep for sends whose
        /// timing it does not control.
        /// </summary>
        public async Task WaitForSendCountAsync<T>(int count, CancellationToken cancellationToken)
            where T : IpcMessage
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = 0;
            void Handler(IpcMessage message)
            {
                if (message is T && Interlocked.Increment(ref observed) >= count)
                {
                    completion.TrySetResult();
                }
            }

            SentObserved += Handler;
            try
            {
                lock (_gate)
                {
                    if (Sent.OfType<T>().Count() >= count)
                    {
                        return;
                    }
                }

                await completion.Task.WaitAsync(TestTimeout, cancellationToken);
            }
            finally
            {
                SentObserved -= Handler;
            }
        }

        public ValueTask DisposeAsync()
        {
            Close();
            return ValueTask.CompletedTask;
        }

        private async Task<IpcMessage> AwaitReceiveAsync(Task<IpcMessage> wait, CancellationToken cancellationToken)
        {
            await wait.WaitAsync(cancellationToken);
            return await ReceiveAsync(cancellationToken);
        }
    }
}
