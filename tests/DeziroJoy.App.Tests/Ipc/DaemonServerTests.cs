using System.Buffers.Binary;
using System.Text;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Profiles;
using Xunit;

namespace DeziroJoy.App.Tests.Ipc;

/// <summary>
/// Controllable time, so the daemon instance id is deterministic and a restart is
/// distinguishable from the daemon it replaced.
/// </summary>
internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// The resident mapping runtime, faked. Records every call the daemon makes so a test can
/// assert the ack ordering contract (start acked only after the engine really started, stop
/// acked only after release) and the conflict refusal, with no real engine, mutex or pipe.
/// </summary>
internal sealed class FakeDaemonHost : IDaemonHost
{
    private readonly List<string> _deviceIds = [];

    public bool IsRunning { get; private set; }

    public bool IsMappingEnabled { get; private set; } = true;

    public bool IsInjectionSuspended { get; private set; }

    public bool HasController { get; set; }

    public int InjectorPid { get; set; } = 4242;

    public string? InjectorIntegrity { get; set; } = "high";

    public long InjectedEvents { get; set; } = 10;

    public long BlockedEvents { get; set; } = 2;

    /// <summary>When set, <see cref="Start"/> refuses exactly as a held mutex would.</summary>
    public bool RefuseStart { get; set; }

    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    public List<string> ReloadedDevices { get; } = [];

    public IReadOnlyList<string> ConnectedDeviceIds => _deviceIds;

    public void AddDevice(string deviceId) => _deviceIds.Add(deviceId);

    public void Start()
    {
        StartCalls++;

        if (RefuseStart)
        {
            throw new InjectionAlreadyActiveException("another process holds the injection mutex");
        }

        IsRunning = true;
    }

    public void StopMapping()
    {
        StopCalls++;
        IsRunning = false;
    }

    public void SetMappingEnabled(bool enabled) => IsMappingEnabled = enabled;

    public void SetInjectionSuspended(bool suspended) => IsInjectionSuspended = suspended;

    public void Reload(string deviceId, GamepadProfile profile) => ReloadedDevices.Add(deviceId);
}

/// <summary>
/// A stream that replays scripted bytes and records everything written to it, so the framed
/// transport can be exercised without a real named pipe. Reading past the script returns 0,
/// which is exactly how a disconnected peer presents itself.
/// </summary>
internal sealed class ScriptedStream : Stream
{
    private readonly Queue<byte> _script = new();

    private readonly List<byte> _written = [];

    public IReadOnlyList<byte> Written => _written;

    public void Script(IpcMessage message)
    {
        var payload = Encoding.UTF8.GetBytes(IpcSerializer.Serialize(message));
        var prefix = new byte[PipeFraming.LengthPrefixBytes];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        Enqueue(prefix);
        Enqueue(payload);
    }

    public string WrittenText() => Encoding.UTF8.GetString([.. _written]);

    private void Enqueue(byte[] bytes)
    {
        foreach (var value in bytes)
        {
            _script.Enqueue(value);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_script.Count == 0)
        {
            return 0;
        }

        var taken = 0;
        while (taken < count && _script.Count > 0)
        {
            buffer[offset + taken++] = _script.Dequeue();
        }

        return taken;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            _written.Add(buffer[offset + i]);
        }
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// The daemon-side contract of docs/ELEVATION-AND-DAEMON.md section 4: versioned handshake,
/// full snapshots, revision-guarded mutations, ack ordering, monotonic sequence, persistence
/// across restart, and the resident behaviours (a GUI disconnect never stops mapping, a
/// second daemon never competes, no spurious state is pushed).
/// </summary>
public class DaemonServerTests
{
    private static string ProfileJson(string name = "Xbox Default")
        => ProfileSerializer.Serialize(
            DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with { Name = name });

    private static DaemonStateStore NewStore()
        => new(Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():n}.json"));

    private static DaemonServer CreateServer(
        FakeDaemonHost host,
        DaemonStateStore stateStore,
        FakeClock? clock = null,
        IProfileStore? profileStore = null)
        => new(host, profileStore ?? new InMemoryProfileStore(), stateStore, clock ?? new FakeClock());

    private static (DaemonServer Server, DaemonStateStore Store, FakeDaemonHost Host) CreateRunning()
    {
        var store = NewStore();
        var host = new FakeDaemonHost();
        return (CreateServer(host, store), store, host);
    }

    [Fact]
    public async Task Hello_Is_Acked_With_The_Daemon_Instance_Id_And_Current_Revision()
    {
        var (server, _, _) = CreateRunning();

        var ack = Assert.IsType<HelloAckMessage>(
            await server.ProcessAsync(new HelloMessage("gui-1", IpcContract.ProtocolVersion, "client-1", IpcSession.Current)));

        Assert.True(ack.Accepted);
        Assert.Equal(IpcContract.ProtocolVersion, ack.ProtocolVersion);
        Assert.Equal(server.DaemonInstanceId, ack.DaemonInstanceId);
        Assert.Equal(0, ack.Revision);
    }

    [Fact]
    public async Task Hello_Refuses_A_Protocol_Version_The_Daemon_Does_Not_Speak()
    {
        var (server, _, _) = CreateRunning();

        var ack = Assert.IsType<HelloAckMessage>(
            await server.ProcessAsync(new HelloMessage("gui-1", IpcContract.ProtocolVersion + 1, "client-1", IpcSession.Current)));

        Assert.False(ack.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(ack.RejectReason));
    }

    [Fact]
    public void Windows_Handshake_Accepts_Only_The_Daemons_Own_Session()
    {
        Assert.True(DaemonServer.IsSessionAccepted(7, true, 7));
        Assert.False(DaemonServer.IsSessionAccepted(8, true, 7));
        Assert.True(DaemonServer.IsSessionAccepted(0, false, 7));
    }

    [Fact]
    public async Task GetState_Returns_A_Full_Snapshot_Carrying_The_Instance_Id_And_Injector_Surface()
    {
        var (server, _, host) = CreateRunning();
        host.HasController = true;

        var snapshot = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-1")));

        Assert.Equal("req-1", snapshot.RequestId);
        Assert.Equal(server.DaemonInstanceId, snapshot.DaemonInstanceId);
        Assert.Equal(4242, snapshot.Snapshot.InjectorPid);
        Assert.Equal("high", snapshot.Snapshot.InjectorIntegrity);
        Assert.Equal(10, snapshot.Snapshot.InjectedEvents);
        Assert.Equal(2, snapshot.Snapshot.BlockedEvents);
        Assert.True(snapshot.Snapshot.HasController);
        Assert.False(snapshot.Snapshot.Running);
    }

    [Fact]
    public async Task Apply_Stores_The_Profile_Body_And_Bumps_The_Revision()
    {
        var (server, store, host) = CreateRunning();
        host.AddDevice("pad-1");
        var profileJson = ProfileJson("Arena Shooter");

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new ApplyConfigurationMessage("req-1", 0, "Arena Shooter", profileJson)));

        Assert.True(ack.Accepted);
        Assert.Equal("req-1", ack.RequestId);
        Assert.Equal(1, ack.Revision);
        Assert.Contains("pad-1", host.ReloadedDevices);

        var persisted = store.Load();
        Assert.NotNull(persisted);
        Assert.Equal("Arena Shooter", persisted.ProfileName);
        Assert.Equal(profileJson, persisted.ProfileJson);
        Assert.Equal(1, persisted.Revision);
    }

    [Fact]
    public async Task Apply_With_A_Stale_Revision_Is_Refused_And_Changes_Nothing()
    {
        var (server, store, host) = CreateRunning();
        await server.ProcessAsync(new ApplyConfigurationMessage("req-1", 0, "First", ProfileJson("First")));

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new ApplyConfigurationMessage("req-2", 0, "Second", ProfileJson("Second"))));

        Assert.False(ack.Accepted);
        Assert.Equal(IpcErrorCodes.RevisionMismatch, ack.ErrorCode);
        Assert.Equal(1, ack.Revision);
        Assert.False(string.IsNullOrWhiteSpace(ack.ErrorMessage));
        Assert.Empty(host.ReloadedDevices);
        Assert.Equal("First", store.Load()!.ProfileName);
    }

    [Fact]
    public async Task Apply_With_Unparsable_Profile_Json_Is_Refused_Without_Bumping_The_Revision()
    {
        var (server, _, _) = CreateRunning();

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new ApplyConfigurationMessage("req-1", 0, "Broken", "{ not json")));

        Assert.False(ack.Accepted);
        Assert.Equal(DaemonErrorCodes.InvalidProfile, ack.ErrorCode);
        Assert.Equal(0, ack.Revision);
    }

    [Fact]
    public async Task Start_Acks_Only_After_The_Engine_And_Mutex_Are_Actually_Up()
    {
        var (server, _, host) = CreateRunning();

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new StartMessage("req-1", 0)));

        Assert.True(ack.Accepted);
        Assert.Equal(1, host.StartCalls);
        Assert.True(host.IsRunning);
        Assert.True(host.IsMappingEnabled);

        var snapshot = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-2")));
        Assert.True(snapshot.Snapshot.Running);
    }

    [Fact]
    public async Task Start_Is_Refused_When_Another_Process_Holds_The_Injection_Mutex()
    {
        var (server, _, host) = CreateRunning();
        host.RefuseStart = true;

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new StartMessage("req-1", 0)));

        Assert.False(ack.Accepted);
        Assert.Equal(DaemonErrorCodes.InjectionActive, ack.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(ack.ErrorMessage));
        Assert.False(host.IsRunning);

        var snapshot = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-2")));
        Assert.False(snapshot.Snapshot.Running);
    }

    [Fact]
    public async Task Start_With_A_Stale_Revision_Is_Refused_Before_The_Engine_Is_Touched()
    {
        var (server, _, host) = CreateRunning();
        await server.ProcessAsync(new ApplyConfigurationMessage("req-0", 0, "First", ProfileJson("First")));

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new StartMessage("req-1", 0)));

        Assert.False(ack.Accepted);
        Assert.Equal(IpcErrorCodes.RevisionMismatch, ack.ErrorCode);
        Assert.Equal(0, host.StartCalls);
    }

    [Fact]
    public async Task Stop_Acks_Only_After_The_Engine_Has_Released()
    {
        var (server, _, host) = CreateRunning();
        await server.ProcessAsync(new StartMessage("req-0", 0));

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new StopMessage("req-1")));

        Assert.True(ack.Accepted);
        Assert.Equal(1, host.StopCalls);
        Assert.False(host.IsRunning);

        var snapshot = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-2")));
        Assert.False(snapshot.Snapshot.Running);
    }

    [Fact]
    public async Task Every_State_Change_Advances_The_Monotonic_Sequence()
    {
        var (server, _, _) = CreateRunning();

        var initial = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-0")));
        var afterApply = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new ApplyConfigurationMessage("req-1", 0, "P", ProfileJson())));
        var afterStart = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new StartMessage("req-2", 1)));
        var afterStop = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new StopMessage("req-3")));

        Assert.Equal(0, initial.Snapshot.Sequence);
        Assert.Equal(1, afterApply.Sequence);
        Assert.Equal(2, afterStart.Sequence);
        Assert.Equal(3, afterStop.Sequence);
    }

    [Fact]
    public async Task A_Mutation_Publishes_Exactly_One_Snapshot_And_A_Pull_Publishes_None()
    {
        var (server, _, _) = CreateRunning();
        var pushes = new List<StateChangedMessage>();
        server.StateChanged += push => pushes.Add(push);

        await server.ProcessAsync(new HelloMessage("gui-1", IpcContract.ProtocolVersion, "client-1", IpcSession.Current));
        await server.ProcessAsync(new GetStateMessage("req-1"));
        Assert.Empty(pushes);

        await server.ProcessAsync(new ApplyConfigurationMessage("req-2", 0, "P", ProfileJson()));
        Assert.Single(pushes);
        Assert.Equal(1, pushes[0].Snapshot.Revision);
    }

    [Fact]
    public async Task A_Refused_Mutation_Publishes_No_State()
    {
        var (server, _, host) = CreateRunning();
        host.RefuseStart = true;
        var pushes = new List<StateChangedMessage>();
        server.StateChanged += push => pushes.Add(push);

        await server.ProcessAsync(new StartMessage("req-1", 0));

        Assert.Empty(pushes);
    }

    [Fact]
    public void A_First_Run_With_No_Persisted_State_Starts_Mapping()
    {
        var (server, _, host) = CreateRunning();

        server.ResumeDesiredState();

        Assert.True(server.DesiredMappingEnabled);
        Assert.Equal(1, host.StartCalls);
        Assert.True(host.IsRunning);
    }

    [Fact]
    public async Task Stop_When_The_Engine_Is_Already_Stopped_Persists_The_Desired_Off_State()
    {
        var (server, store, host) = CreateRunning();

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new StopMessage("req-1")));

        Assert.True(ack.Accepted);
        Assert.Equal(0, host.StopCalls);
        Assert.False(server.DesiredMappingEnabled);
        Assert.False(store.Load()!.MappingEnabled);
    }

    [Fact]
    public async Task A_Restarted_Daemon_Resumes_The_Persisted_Profile_Revision_And_Enabled_State()
    {
        var store = NewStore();
        var clock = new FakeClock();
        var first = new FakeDaemonHost();
        first.AddDevice("pad-1");

        var daemonOne = CreateServer(first, store, clock);
        await daemonOne.ProcessAsync(new ApplyConfigurationMessage("req-1", 0, "Arena Shooter", ProfileJson("Arena Shooter")));
        await daemonOne.ProcessAsync(new StartMessage("req-2", 1));
        var firstInstanceId = daemonOne.DaemonInstanceId;

        clock.Advance(TimeSpan.FromMinutes(5));
        var second = new FakeDaemonHost();
        second.AddDevice("pad-1");
        var daemonTwo = CreateServer(second, store, clock);

        Assert.NotEqual(firstInstanceId, daemonTwo.DaemonInstanceId);

        var snapshot = Assert.IsType<StateChangedMessage>(
            await daemonTwo.ProcessAsync(new GetStateMessage("req-3")));
        Assert.Equal("Arena Shooter", snapshot.Snapshot.ProfileName);
        Assert.Equal(1, snapshot.Snapshot.Revision);
        Assert.True(daemonTwo.DesiredMappingEnabled);

        daemonTwo.ResumeDesiredState();

        Assert.True(second.IsRunning);
        Assert.Contains("pad-1", second.ReloadedDevices);
    }

    [Fact]
    public async Task A_Restarted_Daemon_Stays_Stopped_When_Mapping_Was_Left_Disabled()
    {
        var store = NewStore();
        var daemonOne = CreateServer(new FakeDaemonHost(), store);
        await daemonOne.ProcessAsync(new ApplyConfigurationMessage("req-1", 0, "P", ProfileJson()));
        await daemonOne.ProcessAsync(new StartMessage("req-2", 1));
        await daemonOne.ProcessAsync(new StopMessage("req-3"));

        var second = new FakeDaemonHost();
        var daemonTwo = CreateServer(second, store);
        daemonTwo.ResumeDesiredState();

        Assert.False(daemonTwo.DesiredMappingEnabled);
        Assert.False(second.IsRunning);
        Assert.Equal(0, second.StartCalls);
    }

    [Fact]
    public async Task A_Gui_Disconnect_Leaves_The_Mapping_Running()
    {
        var (server, _, host) = CreateRunning();
        var stream = new ScriptedStream();
        stream.Script(new HelloMessage("gui-1", IpcContract.ProtocolVersion, "client-1", IpcSession.Current));
        stream.Script(new StartMessage("req-1", 0));

        await server.ServeConnectionAsync(stream, TestContext.Current.CancellationToken);

        Assert.True(host.IsRunning);
        Assert.Equal(0, host.StopCalls);
        Assert.Contains("hello-ack", stream.WrittenText());
    }

    [Fact]
    public async Task A_Second_Daemon_Cannot_Take_The_Injection_Slot_From_The_First()
    {
        var (server, _, host) = CreateRunning();
        await server.ProcessAsync(new StartMessage("req-1", 0));

        var rivalHost = new FakeDaemonHost { RefuseStart = true };
        var rival = CreateServer(rivalHost, NewStore());

        var ack = Assert.IsType<MutationAckMessage>(
            await rival.ProcessAsync(new StartMessage("req-2", 0)));

        Assert.False(ack.Accepted);
        Assert.Equal(DaemonErrorCodes.InjectionActive, ack.ErrorCode);
        Assert.False(rivalHost.IsRunning);
        Assert.True(host.IsRunning);
    }

    [Fact]
    public async Task Pause_And_Resume_Change_Dispatch_Without_Stopping_The_Engine()
    {
        var (server, _, host) = CreateRunning();
        await server.ProcessAsync(new StartMessage("req-1", 0));

        var paused = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new SetMappingEnabledMessage("req-2", false)));

        Assert.True(paused.Accepted);
        Assert.True(host.IsRunning);
        Assert.False(host.IsMappingEnabled);

        var snapshot = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-3")));
        Assert.True(snapshot.Snapshot.Running);
        Assert.False(snapshot.Snapshot.MappingEnabled);

        await server.ProcessAsync(new SetMappingEnabledMessage("req-4", true));
        Assert.True(host.IsMappingEnabled);
        Assert.Equal(0, host.StopCalls);
    }

    [Fact]
    public async Task Editor_Suspension_Is_Acked_Without_Publishing_A_Configuration_Change()
    {
        var (server, _, host) = CreateRunning();
        var pushes = new List<StateChangedMessage>();
        server.StateChanged += pushes.Add;

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new SetInjectionSuspendedMessage("req-1", true)));

        Assert.True(ack.Accepted);
        Assert.True(host.IsInjectionSuspended);
        Assert.Empty(pushes);
    }

    [Fact]
    public async Task Start_When_Already_Running_Resumes_Paused_Dispatch()
    {
        var (server, _, host) = CreateRunning();
        await server.ProcessAsync(new StartMessage("req-1", 0));
        host.SetMappingEnabled(false);

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new StartMessage("req-2", 0)));

        Assert.True(ack.Accepted);
        Assert.True(host.IsMappingEnabled);
        Assert.Equal(1, host.StartCalls);
    }

    [Fact]
    public void The_Windows_Pipe_Is_Restricted_To_The_Current_User()
    {
        var windows = DaemonHost.MappingPipeOptions(true);
        var otherPlatforms = DaemonHost.MappingPipeOptions(false);

        Assert.True(windows.HasFlag(System.IO.Pipes.PipeOptions.CurrentUserOnly));
        Assert.False(otherPlatforms.HasFlag(System.IO.Pipes.PipeOptions.CurrentUserOnly));
        Assert.True(windows.HasFlag(System.IO.Pipes.PipeOptions.Asynchronous));
        Assert.True(otherPlatforms.HasFlag(System.IO.Pipes.PipeOptions.Asynchronous));
    }

    [Fact]
    public void The_Pipe_Name_Is_The_Control_Channel_And_Not_The_Injection_Mutex()
    {
        Assert.Equal("DeziroJoy.Mapping", IpcContract.PipeName);
        Assert.NotEqual(InjectionMutex.DefaultName, IpcContract.PipeName);
    }
}
