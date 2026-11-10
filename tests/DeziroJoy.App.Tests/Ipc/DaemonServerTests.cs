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
    private readonly Dictionary<string, ConnectedDeviceSnapshot> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GamepadProfile> _profiles = new(StringComparer.Ordinal);

    public bool IsRunning { get; private set; }

    public bool IsMappingEnabled { get; private set; } = true;

    public bool IsInjectionSuspended { get; private set; }

    public event Action<bool>? InjectionSuspensionChanged;

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

    public IReadOnlyList<ConnectedDeviceSnapshot> ConnectedDevices
        => _deviceIds.Select(deviceId => _devices[deviceId]).ToList();

    public event Action? DevicesChanged;

    public event Action<string, IGamepadState>? InputSampled;

    public string? FailReloadDevice { get; set; }

    public string? PresenceDeviceId { get; set; }

    public IGamepadState PresenceState { get; set; } = GamepadState.Neutral;

    public void PublishInputSample(string deviceId, IGamepadState state)
        => InputSampled?.Invoke(deviceId, state);

    public void SamplePresence()
    {
        if (PresenceDeviceId is not null)
        {
            PublishInputSample(PresenceDeviceId, PresenceState);
        }
    }

    public GamepadProfile? GetProfile(string deviceId) => _profiles.GetValueOrDefault(deviceId);

    public void AddDevice(
        string deviceId,
        string name = "Test Controller",
        GamepadLayout layout = GamepadLayout.Xbox)
    {
        _deviceIds.Add(deviceId);
        _devices[deviceId] = new ConnectedDeviceSnapshot(deviceId, name, layout);
        DevicesChanged?.Invoke();
    }

    public void RemoveDevice(string deviceId)
    {
        _deviceIds.Remove(deviceId);
        _devices.Remove(deviceId);
        DevicesChanged?.Invoke();
    }

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

    public void SetInjectionSuspended(bool suspended)
    {
        IsInjectionSuspended = suspended;
        InjectionSuspensionChanged?.Invoke(suspended);
    }

    public void Reload(string deviceId, GamepadProfile profile)
    {
        if (deviceId == FailReloadDevice)
        {
            throw new InvalidOperationException("injected reload failure");
        }

        ReloadedDevices.Add(deviceId);
        _profiles[deviceId] = profile;
    }

    public void SwitchPresetSlot(int slot, IReadOnlyDictionary<string, GamepadProfile> profiles)
    {
        var previous = profiles.Keys.ToDictionary(deviceId => deviceId, GetProfile, StringComparer.Ordinal);
        try
        {
            foreach (var (deviceId, profile) in profiles)
            {
                // Mirror EngineHost.SwitchPresetSlot: the runtime is tagged with the slot it moves
                // to, so a device left off the app-wide slot is observable through GetProfile.
                Reload(deviceId, profile with { PresetSlot = slot });
            }
        }
        catch
        {
            FailReloadDevice = null;
            foreach (var (deviceId, profile) in previous)
            {
                if (profile is not null && ReloadedDevices.Contains(deviceId))
                {
                    Reload(deviceId, profile);
                }
            }

            throw;
        }
    }
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

/// <summary>A bidirectional scripted stream whose writer and end-of-stream can be held by a test.</summary>
internal sealed class BlockingWriteStream : Stream
{
    private readonly object _gate = new();
    private readonly Queue<byte> _script = new();
    private readonly List<byte> _written = [];
    private readonly TaskCompletionSource _writeBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseWrites = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _blockWrites;
    private bool _finishedReading;

    public Task WriteBlocked => _writeBlocked.Task;

    public void Script(IpcMessage message)
    {
        var payload = Encoding.UTF8.GetBytes(IpcSerializer.Serialize(message));
        var prefix = new byte[PipeFraming.LengthPrefixBytes];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        lock (_gate)
        {
            foreach (var value in prefix.Concat(payload))
            {
                _script.Enqueue(value);
            }
        }
    }

    public void BlockFurtherWrites() => _blockWrites = true;

    public void ReleaseWrites()
    {
        _blockWrites = false;
        _releaseWrites.TrySetResult();
    }

    public void Finish()
    {
        _finishedReading = true;
        _finished.TrySetResult();
    }

    public IReadOnlyList<IpcMessage> WrittenMessages()
    {
        byte[] bytes;
        lock (_gate)
        {
            bytes = [.. _written];
        }

        var messages = new List<IpcMessage>();
        var offset = 0;
        while (offset + PipeFraming.LengthPrefixBytes <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, PipeFraming.LengthPrefixBytes));
            offset += PipeFraming.LengthPrefixBytes;
            if (offset + length > bytes.Length)
            {
                break;
            }

            messages.Add(IpcSerializer.Deserialize(Encoding.UTF8.GetString(bytes, offset, length)));
            offset += length;
        }

        return messages;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_script.Count > 0)
            {
                var taken = 0;
                while (taken < buffer.Length && _script.Count > 0)
                {
                    buffer.Span[taken++] = _script.Dequeue();
                }

                return taken;
            }

            if (_finishedReading)
            {
                return 0;
            }
        }

        await _finished.Task.WaitAsync(cancellationToken);
        return 0;
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_blockWrites)
        {
            _writeBlocked.TrySetResult();
            await _releaseWrites.Task.WaitAsync(cancellationToken);
        }

        lock (_gate)
        {
            foreach (var value in buffer.Span)
            {
                _written.Add(value);
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

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
    public async Task Device_Changes_Push_Real_Identity_Without_A_Configuration_Revision()
    {
        var (server, _, host) = CreateRunning();
        var pushes = new List<StateChangedMessage>();
        server.StateChanged += pushes.Add;
        host.HasController = true;

        host.AddDevice("ds4-1", "DualShock 4 Wireless Controller", GamepadLayout.DualShock);

        var push = Assert.Single(pushes);
        Assert.Equal(0, push.Snapshot.Revision);
        Assert.Equal(1, push.Snapshot.Sequence);
        var device = Assert.Single(push.Snapshot.Devices);
        Assert.Equal("ds4-1", device.DeviceId);
        Assert.Equal("DualShock 4 Wireless Controller", device.Name);
        Assert.Equal(GamepadLayout.DualShock, device.Layout);

        host.HasController = false;
        host.RemoveDevice("ds4-1");
        Assert.Equal(2, pushes.Count);
        Assert.Empty(pushes[1].Snapshot.Devices);
        await server.ProcessAsync(new GetStateMessage("verify"));
    }

    [Fact]
    public async Task Input_Samples_Publish_Only_Changes_With_A_Monotonic_Independent_Sequence()
    {
        var (server, _, host) = CreateRunning();
        host.AddDevice("pad-1");
        var stateSequence = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("before"))).Snapshot.Sequence;
        var samples = new List<InputSampleMessage>();
        server.InputSampled += samples.Add;
        var neutral = GamepadState.Create(GamepadButtonMask.None, 0, 0, 0, 0, [301]);

        host.PublishInputSample("pad-1", neutral);
        host.PublishInputSample("pad-1", neutral);
        host.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0.5f, 0, 0, 0, [301]));
        host.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0.5f, 0, 0, 0, [301]));

        Assert.Equal(2, samples.Count);
        Assert.Equal([1, 2], samples.Select(sample => sample.Sequence).ToArray());
        Assert.Equal(GamepadButtonMask.A, samples[1].PressedButtons);
        Assert.Equal(stateSequence, Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("verify"))).Snapshot.Sequence);
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

        var snapshot = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-state")));
        Assert.Equal(profileJson, snapshot.Snapshot.ProfileJson);
    }

    [Fact]
    public async Task Apply_Reloads_Only_The_Profiles_Target_Device()
    {
        var (server, _, host) = CreateRunning();
        host.AddDevice("pad-1");
        host.AddDevice("pad-2");
        var profile = DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "Second",
            DeviceId = "pad-2",
        };

        await server.ProcessAsync(new ApplyConfigurationMessage(
            "req-1", 0, "Second", ProfileSerializer.Serialize(profile)));

        Assert.Equal(["pad-2"], host.ReloadedDevices);
    }

    [Fact]
    public async Task SwitchPresetSlot_Loads_The_Slot_Profile_Reloads_Only_The_Target_Device_And_Bumps_The_Revision()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.SaveSlot("pad-1", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "主配置",
            DeviceId = "pad-1",
        });
        presets.SaveSlot("pad-1", 2, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "第二套",
            DeviceId = "pad-1",
        });
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        host.AddDevice("pad-2");
        var server = CreateServer(host, NewStore(), profileStore: store);
        var pushes = new List<StateChangedMessage>();
        server.StateChanged += push => pushes.Add(push);

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new SwitchPresetSlotMessage("req-1", "pad-1", 2)));

        Assert.True(ack.Accepted);
        Assert.Equal("req-1", ack.RequestId);
        Assert.Equal(1, ack.Revision);
        Assert.Equal(["pad-1"], host.ReloadedDevices);
        Assert.Equal("第二套", host.GetProfile("pad-1")!.Name);

        var push = Assert.Single(pushes);
        Assert.Equal(2, push.Snapshot.ActivePresetSlot);
        Assert.Equal(1, push.Snapshot.Revision);
    }

    [Fact]
    public async Task Failed_Switch_Rolls_Back_And_Keeps_The_Previous_Global_Slot()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        foreach (var deviceId in new[] { "pad-1", "pad-2" })
        {
            presets.SaveSlot(deviceId, 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
            {
                Name = $"Original {deviceId}",
                DeviceId = deviceId,
            });
            presets.SaveSlot(deviceId, 2, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
            {
                Name = $"Replacement {deviceId}",
                DeviceId = deviceId,
            });
        }

        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        host.AddDevice("pad-2");
        host.Reload("pad-1", presets.LoadSlot("pad-1", 1)!);
        host.Reload("pad-2", presets.LoadSlot("pad-2", 1)!);
        host.FailReloadDevice = "pad-2";
        var server = CreateServer(host, NewStore(), profileStore: store);

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new SwitchPresetSlotMessage("req-rollback", "pad-1", 2)));

        Assert.False(ack.Accepted);
        Assert.Equal("preset-switch-failed", ack.ErrorCode);
        Assert.Equal("Original pad-1", host.GetProfile("pad-1")!.Name);
        Assert.Equal("Original pad-2", host.GetProfile("pad-2")!.Name);
        var snapshot = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("verify")));
        Assert.Equal(1, snapshot.Snapshot.ActivePresetSlot);
        Assert.Equal(0, snapshot.Snapshot.Revision);
    }

    [Fact]
    public async Task Every_Snapshot_Carries_The_Active_Preset_Slot_After_A_Switch()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.SaveSlot("pad-1", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "主配置",
            DeviceId = "pad-1",
        });
        presets.SaveSlot("pad-1", 2, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "第二套",
            DeviceId = "pad-1",
        });
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        var server = CreateServer(host, NewStore(), profileStore: store);

        var before = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-before")));
        Assert.Equal(1, before.Snapshot.ActivePresetSlot);

        await server.ProcessAsync(new SwitchPresetSlotMessage("req-switch", "pad-1", 2));

        var after = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-after")));
        Assert.Equal(2, after.Snapshot.ActivePresetSlot);
        Assert.Equal("第二套", after.Snapshot.ProfileName);
    }

    [Fact]
    public async Task Apply_Stores_Into_The_Active_Preset_Slot_Rather_Than_The_Bare_Device_Key()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        var slotOne = DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "主配置",
            DeviceId = "pad-1",
        };
        var slotTwo = DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "第二套",
            DeviceId = "pad-1",
        };
        presets.SaveSlot("pad-1", 1, slotOne);
        presets.SaveSlot("pad-1", 2, slotTwo);
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        var server = CreateServer(host, NewStore(), profileStore: store);
        await server.ProcessAsync(new HelloMessage("gui-1", IpcContract.ProtocolVersion, "client-1", IpcSession.Current));
        await server.ProcessAsync(new SwitchPresetSlotMessage("req-s", "pad-1", 2));

        var edited = DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "第三套",
            DeviceId = "pad-1",
        };
        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new ApplyConfigurationMessage(
                "req-a", 1, "第三套", ProfileSerializer.Serialize(edited))));

        Assert.True(ack.Accepted);
        Assert.Equal("第三套", presets.LoadSlot("pad-1", 2)!.Name);
        Assert.Equal("主配置", presets.LoadSlot("pad-1", 1)!.Name);
    }

    [Fact]
    public async Task Switching_The_Preset_Slot_Publishes_The_Slot_Profile_Body()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        var slotOne = DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "主配置",
            DeviceId = "pad-1",
        };
        var slotTwo = DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "第二套",
            DeviceId = "pad-1",
        };
        presets.SaveSlot("pad-1", 1, slotOne);
        presets.SaveSlot("pad-1", 2, slotTwo);
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        var server = CreateServer(host, NewStore(), profileStore: store);
        await server.ProcessAsync(new HelloMessage("gui-1", IpcContract.ProtocolVersion, "client-1", IpcSession.Current));
        await server.ProcessAsync(new ApplyConfigurationMessage(
            "req-a", 0, "主配置", ProfileSerializer.Serialize(slotOne)));
        await server.ProcessAsync(new SwitchPresetSlotMessage("req-s", "pad-1", 2));

        var snapshot = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-state")));

        Assert.Equal("第二套", snapshot.Snapshot.ProfileName);
        Assert.Equal(ProfileSerializer.Serialize(slotTwo), snapshot.Snapshot.ProfileJson);
    }

    [Fact]
    public async Task SwitchPresetSlot_Rejects_A_Slot_Below_One()
    {
        var (server, _, host) = CreateRunning();
        host.AddDevice("pad-1");

        var error = Assert.IsType<ErrorMessage>(
            await server.ProcessAsync(new SwitchPresetSlotMessage("req-1", "pad-1", 0)));

        Assert.Equal("req-1", error.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(error.ErrorCode));
        Assert.Empty(host.ReloadedDevices);
    }

    [Fact]
    public async Task SwitchPresetSlot_Accepts_A_Slot_Beyond_The_Initial_Four()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.SaveSlot("pad-1", 5, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "第五套",
            DeviceId = "pad-1",
        });
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        var server = CreateServer(host, NewStore(), profileStore: store);

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new SwitchPresetSlotMessage("req-1", "pad-1", 5)));

        Assert.True(ack.Accepted);
        Assert.Equal("第五套", host.GetProfile("pad-1")!.Name);
        var snapshot = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("verify")));
        Assert.Equal(5, snapshot.Snapshot.ActivePresetSlot);
    }

    [Fact]
    public async Task An_Edit_To_A_Slot_Survives_Switching_Away_And_Back_While_Other_Slots_Stay_Put()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        foreach (var slot in new[] { 1, 2 })
        {
            presets.SaveSlot("pad-1", slot, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
            {
                Name = $"Slot {slot}",
                DeviceId = "pad-1",
            });
        }
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        var server = CreateServer(host, NewStore(), profileStore: store);

        // Commit an edit while slot 1 is active.
        await server.ProcessAsync(new SwitchPresetSlotMessage("s1", "pad-1", 1));
        var edited = DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "Slot 1 edited",
            DeviceId = "pad-1",
        };
        var apply = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new ApplyConfigurationMessage(
                "a1", 1, "Slot 1 edited", ProfileSerializer.Serialize(edited))));
        Assert.True(apply.Accepted);

        // Switch away and back.
        await server.ProcessAsync(new SwitchPresetSlotMessage("s2", "pad-1", 2));
        await server.ProcessAsync(new SwitchPresetSlotMessage("s3", "pad-1", 1));

        Assert.Equal("Slot 1 edited", presets.LoadSlot("pad-1", 1)!.Name);
        Assert.Equal("Slot 2", presets.LoadSlot("pad-1", 2)!.Name);
    }

    [Fact]
    public async Task SwitchPresetSlot_Rejects_A_Slot_With_No_Stored_Configuration()
    {
        var (server, _, host) = CreateRunning();
        host.AddDevice("pad-1");

        var error = Assert.IsType<ErrorMessage>(
            await server.ProcessAsync(new SwitchPresetSlotMessage("req-1", "pad-1", 2)));

        Assert.Equal("req-1", error.RequestId);
        Assert.Equal("no-configuration", error.ErrorCode);
        Assert.Equal("no configuration", error.Message);
        Assert.Empty(host.ReloadedDevices);
    }

    [Fact]
    public async Task Switching_The_Preset_Slot_Applies_To_Every_Connected_Device_That_Holds_It()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.SaveSlot("pad-1", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "主配置",
            DeviceId = "pad-1",
        });
        presets.SaveSlot("pad-1", 2, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "第二套",
            DeviceId = "pad-1",
        });
        presets.SaveSlot("pad-2", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "pad-2 主配置",
            DeviceId = "pad-2",
        });
        presets.SaveSlot("pad-2", 2, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "pad-2 第二套",
            DeviceId = "pad-2",
        });
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        host.AddDevice("pad-2");
        var server = CreateServer(host, NewStore(), profileStore: store);
        var pushes = new List<StateChangedMessage>();
        server.StateChanged += push => pushes.Add(push);

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new SwitchPresetSlotMessage("req-1", "pad-1", 2)));

        Assert.True(ack.Accepted);
        Assert.Equal(["pad-1", "pad-2"], host.ReloadedDevices);
        Assert.Equal("第二套", host.GetProfile("pad-1")!.Name);
        Assert.Equal("pad-2 第二套", host.GetProfile("pad-2")!.Name);

        var push = Assert.Single(pushes);
        Assert.Equal(2, push.Snapshot.ActivePresetSlot);
    }

    [Fact]
    public async Task The_Active_Preset_Slot_Is_One_App_Wide_Scalar()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.SaveSlot("pad-1", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "主配置",
            DeviceId = "pad-1",
        });
        presets.SaveSlot("pad-1", 2, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "第二套",
            DeviceId = "pad-1",
        });
        presets.SaveSlot("pad-1", 3, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "第三套",
            DeviceId = "pad-1",
        });
        presets.SaveSlot("pad-2", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "pad-2 主配置",
            DeviceId = "pad-2",
        });
        presets.SaveSlot("pad-2", 2, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "pad-2 第二套",
            DeviceId = "pad-2",
        });
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        host.AddDevice("pad-2");
        var server = CreateServer(host, NewStore(), profileStore: store);

        await server.ProcessAsync(new SwitchPresetSlotMessage("req-1", "pad-1", 2));

        var afterFirst = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-state-1")));
        Assert.Equal(2, afterFirst.Snapshot.ActivePresetSlot);

        await server.ProcessAsync(new SwitchPresetSlotMessage("req-2", "pad-2", 3));

        var afterSecond = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-state-2")));
        Assert.Equal(3, afterSecond.Snapshot.ActivePresetSlot);
    }

    [Fact]
    public async Task Restart_Restores_The_Persisted_Global_Preset_Slot_And_Its_Profiles()
    {
        var stateStore = NewStore();
        var profileStore = new InMemoryProfileStore();
        var presets = new PresetStore(profileStore);
        presets.SaveSlot("pad-1", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "Primary",
            DeviceId = "pad-1",
        });
        presets.SaveSlot("pad-1", 3, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "Third",
            DeviceId = "pad-1",
        });
        var clock = new FakeClock();
        var firstHost = new FakeDaemonHost();
        firstHost.AddDevice("pad-1");
        var first = CreateServer(firstHost, stateStore, clock, profileStore);
        await first.ProcessAsync(new SwitchPresetSlotMessage("switch", "pad-1", 3));

        clock.Advance(TimeSpan.FromSeconds(1));
        var secondHost = new FakeDaemonHost();
        secondHost.AddDevice("pad-1");
        var second = CreateServer(secondHost, stateStore, clock, profileStore);
        second.ResumeDesiredState();

        Assert.Equal(3, stateStore.Load()!.ActivePresetSlot);
        Assert.Equal("Third", secondHost.GetProfile("pad-1")!.Name);
        var snapshot = Assert.IsType<StateChangedMessage>(await second.ProcessAsync(new GetStateMessage("state")));
        Assert.Equal(3, snapshot.Snapshot.ActivePresetSlot);
    }

    [Fact]
    public async Task DeletePresetSlot_Removes_The_Slot_Bumps_The_Revision_And_Publishes()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        foreach (var slot in new[] { 1, 2, 3 })
        {
            presets.SaveSlot("pad-1", slot, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
            {
                Name = $"Slot {slot}",
                DeviceId = "pad-1",
            });
        }

        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        var server = CreateServer(host, NewStore(), profileStore: store);
        var pushes = new List<StateChangedMessage>();
        server.StateChanged += push => pushes.Add(push);

        // Slot 1 is active, so deleting slot 2 leaves the active slot untouched.
        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new DeletePresetSlotMessage("req-del", "pad-1", 2)));

        Assert.True(ack.Accepted);
        Assert.Equal(1, ack.Revision);
        Assert.Equal([1, 3], presets.SlotsInUse("pad-1"));
        Assert.Null(store.Load("preset2:pad-1"));

        var push = Assert.Single(pushes);
        Assert.Equal(1, push.Snapshot.ActivePresetSlot);
        Assert.Equal(1, push.Snapshot.Revision);
    }

    private static GamepadProfile ProfileWithLeader(string name, string deviceId, int slotA, int slotB)
        => DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = name,
            DeviceId = deviceId,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.Back),
                    Target = new SpecialKeyTarget(SpecialKeyKind.Leader, PrecisionMode.Toggle, LeaderSwitchMode.Press, SlotA: slotA, SlotB: slotB),
                    Enabled = true,
                },
            ],
        };

    private static SpecialKeyTarget LeaderOf(GamepadProfile profile)
        => profile.Mappings.Select(mapping => mapping.Target)
            .OfType<SpecialKeyTarget>().Single(target => target.Kind == SpecialKeyKind.Leader);

    [Fact]
    public async Task Deleting_The_Active_Slot_Reloads_The_Repaired_Fallback_Profile()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.SaveSlot("pad-1", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with { Name = "One", DeviceId = "pad-1" });
        // The fallback slot 2 carries a leader 2<->3; deleting the active slot 3 must repair it.
        presets.SaveSlot("pad-1", 2, ProfileWithLeader("Two", "pad-1", slotA: 2, slotB: 3));
        presets.SaveSlot("pad-1", 3, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with { Name = "Three", DeviceId = "pad-1" });
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        var server = CreateServer(host, NewStore(), profileStore: store);
        await server.ProcessAsync(new SwitchPresetSlotMessage("s", "pad-1", 3));

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new DeletePresetSlotMessage("d", "pad-1", 3)));

        Assert.True(ack.Accepted);
        // The runtime was reloaded onto the fallback slot 2 with its leader repaired to flip
        // between two surviving slots (2 and 1), not the deleted slot 3.
        var runtimeLeader = LeaderOf(host.GetProfile("pad-1")!);
        Assert.Equal(2, runtimeLeader.SlotA);
        Assert.Equal(1, runtimeLeader.SlotB);

        // The published body carries the same repaired profile, so a client re-adopting it cannot
        // write the deleted-slot reference back.
        var snapshot = Assert.IsType<StateChangedMessage>(await server.ProcessAsync(new GetStateMessage("v")));
        var bodyLeader = LeaderOf(ProfileSerializer.Deserialize(snapshot.Snapshot.ProfileJson!));
        Assert.Equal(1, bodyLeader.SlotB);
    }

    [Fact]
    public async Task Deleting_A_NonActive_Slot_Repairs_And_Reloads_The_Active_Profile()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        // Slot 1 is active and carries a leader 1<->3; deleting the non-active slot 3 repairs it.
        presets.SaveSlot("pad-1", 1, ProfileWithLeader("One", "pad-1", slotA: 1, slotB: 3));
        presets.SaveSlot("pad-1", 2, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with { Name = "Two", DeviceId = "pad-1" });
        presets.SaveSlot("pad-1", 3, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with { Name = "Three", DeviceId = "pad-1" });
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        host.Reload("pad-1", presets.LoadSlot("pad-1", 1)!);
        var server = CreateServer(host, NewStore(), profileStore: store);

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new DeletePresetSlotMessage("d", "pad-1", 3)));

        Assert.True(ack.Accepted);
        Assert.Equal([1, 2], presets.SlotsInUse("pad-1"));

        // The active slot is unchanged, but its repaired profile was reloaded into the runtime and
        // published as the body (leader retargeted from the deleted slot 3 to the survivor 2).
        var runtimeLeader = LeaderOf(host.GetProfile("pad-1")!);
        Assert.Equal(1, runtimeLeader.SlotA);
        Assert.Equal(2, runtimeLeader.SlotB);
        var snapshot = Assert.IsType<StateChangedMessage>(await server.ProcessAsync(new GetStateMessage("v")));
        Assert.Equal(1, snapshot.Snapshot.ActivePresetSlot);
        Assert.Equal(2, LeaderOf(ProfileSerializer.Deserialize(snapshot.Snapshot.ProfileJson!)).SlotB);
    }

    [Fact]
    public async Task Deleting_A_NonActive_Slot_Rolls_Back_When_The_Repair_Reload_Fails()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.SaveSlot("pad-1", 1, ProfileWithLeader("One", "pad-1", slotA: 1, slotB: 3));
        presets.SaveSlot("pad-1", 2, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with { Name = "Two", DeviceId = "pad-1" });
        presets.SaveSlot("pad-1", 3, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with { Name = "Three", DeviceId = "pad-1" });
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        host.Reload("pad-1", presets.LoadSlot("pad-1", 1)!);
        host.FailReloadDevice = "pad-1";
        var server = CreateServer(host, NewStore(), profileStore: store);

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new DeletePresetSlotMessage("d", "pad-1", 3)));

        // The reload of the repaired active profile failed, so the whole delete is rolled back:
        // the store is restored exactly and the active slot never moved.
        Assert.False(ack.Accepted);
        Assert.Equal("preset-delete-failed", ack.ErrorCode);
        Assert.Equal([1, 2, 3], presets.SlotsInUse("pad-1"));
        Assert.Equal(3, LeaderOf(presets.LoadSlot("pad-1", 1)!).SlotB);
        var snapshot = Assert.IsType<StateChangedMessage>(await server.ProcessAsync(new GetStateMessage("v")));
        Assert.Equal(1, snapshot.Snapshot.ActivePresetSlot);
    }

    [Fact]
    public async Task Deleting_A_Slot_A_Device_Is_Running_Moves_That_Device_Even_When_It_Is_Not_The_Active_Slot()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        foreach (var deviceId in new[] { "pad-A", "pad-B" })
        {
            presets.EnsureDefaults(new GamepadDescriptor(deviceId, deviceId));
        }

        var host = new FakeDaemonHost();
        host.AddDevice("pad-A");
        host.AddDevice("pad-B");
        var server = CreateServer(host, NewStore(), profileStore: store);

        // 1. Delete B's non-active slot 2 (the app-wide slot is still 1).
        Assert.True(Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new DeletePresetSlotMessage("d1", "pad-B", 2))).Accepted);
        Assert.Equal([1, 3, 4], presets.SlotsInUse("pad-B"));

        // 2. Select global slot 3 - both devices reload onto it.
        await server.ProcessAsync(new SwitchPresetSlotMessage("s3", "pad-A", 3));
        Assert.Equal(3, host.GetProfile("pad-B")!.PresetSlot);

        // 3. Delete A's active slot 3: the app-wide slot moves to 2, but B lacks slot 2, so B
        //    keeps running slot 3 (the supported missing-slot semantics).
        Assert.True(Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new DeletePresetSlotMessage("d3", "pad-A", 3))).Accepted);
        Assert.Equal(2, Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("g1"))).Snapshot.ActivePresetSlot);
        Assert.Equal(3, host.GetProfile("pad-B")!.PresetSlot);

        // 4. Now delete B's slot 3 - the very slot B is running, even though it is not the
        //    app-wide active slot. B must be moved to a survivor rather than left executing a
        //    profile whose storage was just removed.
        Assert.True(Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new DeletePresetSlotMessage("d4", "pad-B", 3))).Accepted);
        Assert.Equal([1, 4], presets.SlotsInUse("pad-B"));
        Assert.Equal(1, host.GetProfile("pad-B")!.PresetSlot);
        // The app-wide slot did not move - only B's runtime did.
        Assert.Equal(2, Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("g2"))).Snapshot.ActivePresetSlot);
    }

    [Fact]
    public async Task Deleting_The_Active_Slot_Moves_The_App_To_A_Surviving_Slot()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        foreach (var slot in new[] { 1, 2, 3 })
        {
            presets.SaveSlot("pad-1", slot, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
            {
                Name = $"Slot {slot}",
                DeviceId = "pad-1",
            });
        }

        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        var server = CreateServer(host, NewStore(), profileStore: store);
        await server.ProcessAsync(new SwitchPresetSlotMessage("req-switch", "pad-1", 3));

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new DeletePresetSlotMessage("req-del", "pad-1", 3)));

        Assert.True(ack.Accepted);
        // The active slot 3 is gone and the app moved to the highest surviving slot below it.
        Assert.Equal([1, 2], presets.SlotsInUse("pad-1"));
        Assert.Equal("Slot 2", host.GetProfile("pad-1")!.Name);
        Assert.Contains("pad-1", host.ReloadedDevices);

        var snapshot = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("verify")));
        Assert.Equal(2, snapshot.Snapshot.ActivePresetSlot);
        Assert.Contains("Slot 2", snapshot.Snapshot.ProfileName);
    }

    [Fact]
    public async Task DeletePresetSlot_Refuses_The_Primary_Slot()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.SaveSlot("pad-1", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "Primary",
            DeviceId = "pad-1",
        });
        presets.SaveSlot("pad-1", 2, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "Second",
            DeviceId = "pad-1",
        });
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        var server = CreateServer(host, NewStore(), profileStore: store);

        var error = Assert.IsType<ErrorMessage>(
            await server.ProcessAsync(new DeletePresetSlotMessage("req-del", "pad-1", 1)));

        Assert.Equal("primary-preset", error.ErrorCode);
        Assert.Equal([1, 2], presets.SlotsInUse("pad-1"));
        var snapshot = Assert.IsType<StateChangedMessage>(await server.ProcessAsync(new GetStateMessage("v")));
        Assert.Equal(0, snapshot.Snapshot.Revision);
    }

    [Fact]
    public async Task DeletePresetSlot_Refuses_The_Last_Remaining_Preset()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.SaveSlot("pad-1", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "Only",
            DeviceId = "pad-1",
        });
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        var server = CreateServer(host, NewStore(), profileStore: store);

        var error = Assert.IsType<ErrorMessage>(
            await server.ProcessAsync(new DeletePresetSlotMessage("req-del", "pad-1", 2)));

        Assert.Equal("last-preset", error.ErrorCode);
        Assert.Equal([1], presets.SlotsInUse("pad-1"));
    }

    [Fact]
    public async Task DeletePresetSlot_Reports_Nothing_Stored_For_An_Empty_Slot()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.SaveSlot("pad-1", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with { DeviceId = "pad-1" });
        presets.SaveSlot("pad-1", 2, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with { DeviceId = "pad-1" });
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        var server = CreateServer(host, NewStore(), profileStore: store);

        var error = Assert.IsType<ErrorMessage>(
            await server.ProcessAsync(new DeletePresetSlotMessage("req-del", "pad-1", 5)));

        Assert.Equal("no-configuration", error.ErrorCode);
        Assert.Equal([1, 2], presets.SlotsInUse("pad-1"));
    }

    [Fact]
    public async Task DeletePresetSlot_Rejects_A_Slot_Below_One()
    {
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        var server = CreateServer(host, NewStore());

        var error = Assert.IsType<ErrorMessage>(
            await server.ProcessAsync(new DeletePresetSlotMessage("req-del", "pad-1", 0)));

        Assert.Equal("invalid-slot", error.ErrorCode);
    }

    [Fact]
    public async Task Deleting_The_Active_Slot_Rolls_Back_When_The_Switch_Fails()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.SaveSlot("pad-1", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "Primary",
            DeviceId = "pad-1",
        });
        presets.SaveSlot("pad-1", 2, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "Second",
            DeviceId = "pad-1",
        });
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        host.Reload("pad-1", presets.LoadSlot("pad-1", 1)!);
        var server = CreateServer(host, NewStore(), profileStore: store);
        await server.ProcessAsync(new SwitchPresetSlotMessage("req-switch", "pad-1", 2));
        host.FailReloadDevice = "pad-1";

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new DeletePresetSlotMessage("req-del", "pad-1", 2)));

        // The switch to the fallback failed, so the delete is rolled back and slot 2 survives.
        Assert.False(ack.Accepted);
        Assert.Equal("preset-delete-failed", ack.ErrorCode);
        Assert.Equal([1, 2], presets.SlotsInUse("pad-1"));
        var snapshot = Assert.IsType<StateChangedMessage>(await server.ProcessAsync(new GetStateMessage("v")));
        Assert.Equal(2, snapshot.Snapshot.ActivePresetSlot);
    }

    [Fact]
    public async Task DeletePresetSlot_Repairs_The_Leader_And_Persists_The_Slot_Set_Across_A_Restart()
    {
        var stateStore = NewStore();
        var profileStore = new InMemoryProfileStore();
        var presets = new PresetStore(profileStore);
        presets.SaveSlot("pad-1", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "Primary",
            DeviceId = "pad-1",
        });
        presets.SaveSlot("pad-1", 3, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "Third",
            DeviceId = "pad-1",
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.Back),
                    Target = new SpecialKeyTarget(SpecialKeyKind.Leader, PrecisionMode.Toggle, LeaderSwitchMode.Press, SlotA: 2, SlotB: 3),
                    Enabled = true,
                },
            ],
        });
        var clock = new FakeClock();
        var firstHost = new FakeDaemonHost();
        firstHost.AddDevice("pad-1");
        var first = CreateServer(firstHost, stateStore, clock, profileStore);

        await first.ProcessAsync(new SwitchPresetSlotMessage("switch", "pad-1", 3));
        var ack = Assert.IsType<MutationAckMessage>(
            await first.ProcessAsync(new DeletePresetSlotMessage("delete", "pad-1", 3)));
        Assert.True(ack.Accepted);

        // The leader in slot 3 named slot 2 and itself; with slot 3 gone it is repaired onto
        // the surviving slot 1.
        Assert.Equal([1], presets.SlotsInUse("pad-1"));

        clock.Advance(TimeSpan.FromSeconds(1));
        var secondHost = new FakeDaemonHost();
        secondHost.AddDevice("pad-1");
        var second = CreateServer(secondHost, stateStore, clock, profileStore);
        second.ResumeDesiredState();

        // The deleted slot stays gone and the persisted active slot (a survivor) is restored.
        Assert.Equal([1], presets.SlotsInUse("pad-1"));
        var snapshot = Assert.IsType<StateChangedMessage>(await second.ProcessAsync(new GetStateMessage("state")));
        Assert.Equal(1, snapshot.Snapshot.ActivePresetSlot);
        Assert.Equal("Primary", secondHost.GetProfile("pad-1")!.Name);
    }

    [Fact]
    public async Task Deleting_The_Active_Slot_On_One_Device_Keeps_Other_Devices_Running()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        foreach (var deviceId in new[] { "pad-1", "pad-2" })
        {
            foreach (var slot in new[] { 1, 2, 3 })
            {
                presets.SaveSlot(deviceId, slot, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
                {
                    Name = $"{deviceId} Slot {slot}",
                    DeviceId = deviceId,
                });
            }
        }

        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        host.AddDevice("pad-2");
        var server = CreateServer(host, NewStore(), profileStore: store);
        await server.ProcessAsync(new SwitchPresetSlotMessage("switch", "pad-1", 3));

        // pad-1 deletes its own slot 3; pad-2 keeps its slot 3 in the store.
        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new DeletePresetSlotMessage("delete", "pad-1", 3)));

        Assert.True(ack.Accepted);
        Assert.Equal([1, 2], presets.SlotsInUse("pad-1"));
        Assert.Equal([1, 2, 3], presets.SlotsInUse("pad-2"));
        // The app-wide active slot moved off the deleted one, and both devices were reloaded
        // onto a slot they still hold.
        Assert.Equal("pad-1 Slot 2", host.GetProfile("pad-1")!.Name);
        Assert.Equal("pad-2 Slot 2", host.GetProfile("pad-2")!.Name);
        var snapshot = Assert.IsType<StateChangedMessage>(await server.ProcessAsync(new GetStateMessage("v")));
        Assert.Equal(2, snapshot.Snapshot.ActivePresetSlot);
    }

    [Fact]
    public async Task A_Leader_Request_From_Inside_The_Daemon_Moves_The_App_Wide_Slot()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.SaveSlot("pad-1", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "主配置",
            DeviceId = "pad-1",
        });
        presets.SaveSlot("pad-1", 2, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "第二套",
            DeviceId = "pad-1",
        });
        presets.SaveSlot("pad-2", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "pad-2 主配置",
            DeviceId = "pad-2",
        });
        presets.SaveSlot("pad-2", 2, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "pad-2 第二套",
            DeviceId = "pad-2",
        });
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        host.AddDevice("pad-2");
        var server = CreateServer(host, NewStore(), profileStore: store);
        var pushes = new List<StateChangedMessage>();
        server.StateChanged += push => pushes.Add(push);

        server.RequestPresetSlotSwitch(2);

        Assert.Equal(["pad-1", "pad-2"], host.ReloadedDevices);
        Assert.Equal("第二套", host.GetProfile("pad-1")!.Name);
        Assert.Equal("pad-2 第二套", host.GetProfile("pad-2")!.Name);

        var push = Assert.Single(pushes);
        Assert.Equal(2, push.Snapshot.ActivePresetSlot);

        var snapshot = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-state")));
        Assert.Equal(2, snapshot.Snapshot.ActivePresetSlot);
    }

    [Fact]
    public async Task A_Switch_With_No_Device_Connected_Still_Moves_The_App_Wide_Slot()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.SaveSlot("pad-1", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "主配置",
            DeviceId = "pad-1",
        });
        presets.SaveSlot("pad-1", 2, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "第二套",
            DeviceId = "pad-1",
        });
        var host = new FakeDaemonHost();
        var server = CreateServer(host, NewStore(), profileStore: store);

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new SwitchPresetSlotMessage("req-1", "pad-1", 2)));

        Assert.True(ack.Accepted);
        Assert.Empty(host.ReloadedDevices);

        var snapshot = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-state")));
        Assert.Equal(2, snapshot.Snapshot.ActivePresetSlot);
    }

    [Fact]
    public async Task A_Switch_With_No_Device_Holding_The_Slot_Is_Refused()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.SaveSlot("pad-1", 1, DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "主配置",
            DeviceId = "pad-1",
        });
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        var server = CreateServer(host, NewStore(), profileStore: store);

        var error = Assert.IsType<ErrorMessage>(
            await server.ProcessAsync(new SwitchPresetSlotMessage("req-1", "pad-1", 2)));

        Assert.Equal("req-1", error.RequestId);
        Assert.Equal("no-configuration", error.ErrorCode);
        Assert.Equal("no configuration", error.Message);
        Assert.Empty(host.ReloadedDevices);

        var snapshot = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-state")));
        Assert.Equal(1, snapshot.Snapshot.ActivePresetSlot);
    }

    [Fact]
    public async Task Shutdown_Is_Acknowledged_Before_The_Transport_Requests_Process_Exit()
    {
        var (server, _, _) = CreateRunning();
        var stream = new ScriptedStream();
        stream.Script(new HelloMessage("tray", IpcContract.ProtocolVersion, "tray-1", IpcSession.Current));
        stream.Script(new ShutdownDaemonMessage("shutdown-1"));
        var requested = false;
        server.ShutdownRequested += () =>
        {
            requested = true;
            Assert.Contains("shutdown-1", stream.WrittenText());
            Assert.Contains("\"Accepted\":true", stream.WrittenText());
        };

        await server.ServeConnectionAsync(stream, TestContext.Current.CancellationToken);

        Assert.True(requested);
    }

    [Fact]
    public async Task Transport_Refuses_A_Preset_Slot_Switch_Before_An_Accepted_Hello()
    {
        var (server, _, host) = CreateRunning();
        host.AddDevice("pad-1");
        var stream = new ScriptedStream();
        stream.Script(new SwitchPresetSlotMessage("req-switch", "pad-1", 2));

        await server.ServeConnectionAsync(stream, TestContext.Current.CancellationToken);

        Assert.Contains("handshake-required", stream.WrittenText());
        Assert.Empty(host.ReloadedDevices);
    }

    [Fact]
    public async Task Snapshot_Reads_The_Host_Profile_Before_The_First_Ipc_Apply()
    {
        var (server, _, host) = CreateRunning();
        host.AddDevice("pad-1");
        host.Reload("pad-1", DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic) with
        {
            Name = "Host profile",
            DeviceId = "pad-1",
        });

        var snapshot = Assert.IsType<StateChangedMessage>(
            await server.ProcessAsync(new GetStateMessage("req-state")));

        Assert.Equal("Host profile", snapshot.Snapshot.ProfileName);
        Assert.Contains("Host profile", snapshot.Snapshot.ProfileJson);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Input_Samples_Are_Opt_In_So_Legacy_Clients_Never_See_The_New_Kind(
        bool subscribe,
        bool expectSample)
    {
        var (server, _, host) = CreateRunning();
        host.PresenceDeviceId = "pad-1";
        host.PresenceState = GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0);
        host.PublishInputSample(host.PresenceDeviceId, host.PresenceState);
        var stream = new ScriptedStream();
        stream.Script(new HelloMessage(
            "gui-1",
            IpcContract.ProtocolVersion,
            "client-1",
            IpcSession.Current,
            subscribe));

        await server.ServeConnectionAsync(stream, TestContext.Current.CancellationToken);
        for (var attempt = 0; expectSample && attempt < 200 && !stream.WrittenText().Contains("input-sample", StringComparison.Ordinal); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Contains("hello-ack", stream.WrittenText());
        Assert.Equal(expectSample, stream.WrittenText().Contains("input-sample", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Blocked_Writer_Preserves_Per_Device_Baselines_Analog_Values_And_Button_Edges()
    {
        var (server, _, host) = CreateRunning();
        host.AddDevice("pad-1");
        host.AddDevice("pad-2");
        var stream = new BlockingWriteStream();
        stream.Script(new HelloMessage(
            "gui-1",
            IpcContract.ProtocolVersion,
            "client-1",
            IpcSession.Current,
            SubscribeInputSamples: true));
        var serving = server.ServeConnectionAsync(stream, TestContext.Current.CancellationToken);

        try
        {
            for (var attempt = 0;
                attempt < 200 && !stream.WrittenMessages().Any(message => message is HelloAckMessage);
                attempt++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.Contains(stream.WrittenMessages(), message => message is HelloAckMessage);
            stream.BlockFurtherWrites();
            host.PublishInputSample("pad-1", GamepadState.Neutral);
            await stream.WriteBlocked.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            host.PublishInputSample("pad-2", GamepadState.Neutral);
            host.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.None, 0.25f, 0, 0, 0));
            host.PublishInputSample("pad-2", GamepadState.Create(GamepadButtonMask.None, -0.5f, 0, 0, 0));
            host.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0.25f, 0, 0, 0));
            host.PublishInputSample("pad-2", GamepadState.Create(GamepadButtonMask.B, -0.5f, 0, 0, 0));
            host.PublishInputSample("pad-1", GamepadState.Neutral);
            host.PublishInputSample("pad-2", GamepadState.Neutral);
            stream.ReleaseWrites();

            for (var attempt = 0;
                attempt < 200 && stream.WrittenMessages().OfType<InputSampleMessage>().Count() != 8;
                attempt++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            stream.ReleaseWrites();
            stream.Finish();
        }

        await serving;
        var samples = stream.WrittenMessages().OfType<InputSampleMessage>().ToList();
        Assert.Equal(8, samples.Count);
        Assert.Equal(
            ["pad-1", "pad-2", "pad-1", "pad-2", "pad-1", "pad-2", "pad-1", "pad-2"],
            samples.Select(sample => sample.DeviceId).ToArray());
        Assert.Equal(
            [GamepadButtonMask.None, GamepadButtonMask.None, GamepadButtonMask.None, GamepadButtonMask.None,
                GamepadButtonMask.A, GamepadButtonMask.B, GamepadButtonMask.None, GamepadButtonMask.None],
            samples.Select(sample => sample.PressedButtons).ToArray());
        Assert.Equal(0.25f, samples[2].LeftStickX);
        Assert.Equal(-0.5f, samples[3].LeftStickX);
        Assert.Equal(Enumerable.Range(1, 8).Select(value => (long)value), samples.Select(sample => sample.Sequence));
    }

    [Fact]
    public async Task Transport_Refuses_Control_Before_An_Accepted_Hello()
    {
        var (server, _, host) = CreateRunning();
        var stream = new ScriptedStream();
        stream.Script(new StartMessage("req-start", 0));

        await server.ServeConnectionAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(0, host.StartCalls);
        Assert.Contains("handshake-required", stream.WrittenText());
    }

    [Fact]
    public async Task Transport_Closes_After_A_Rejected_Hello()
    {
        var (server, _, host) = CreateRunning();
        var stream = new ScriptedStream();
        stream.Script(new HelloMessage("gui-1", IpcContract.ProtocolVersion + 1, "client-1", IpcSession.Current));
        stream.Script(new StartMessage("req-start", 0));

        await server.ServeConnectionAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(0, host.StartCalls);
        Assert.Contains("hello-ack", stream.WrittenText());
        Assert.DoesNotContain("mutation-ack", stream.WrittenText());
    }

    [Fact]
    public async Task Transport_Uses_The_Peer_Session_Not_The_Declared_Session()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows session identity is Windows-only.");
            return;
        }

        var (server, _, _) = CreateRunning();
        var stream = new ScriptedStream();
        stream.Script(new HelloMessage("gui-1", IpcContract.ProtocolVersion, "client-1", IpcSession.Current));

        await server.ServeConnectionAsync(
            stream,
            peerSessionId: IpcSession.Current + 1,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("\"Accepted\":false", stream.WrittenText());
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
    public async Task Editor_Suspension_Is_Scoped_To_Its_Connection()
    {
        var (server, _, host) = CreateRunning();

        await server.ProcessAsync(new SetInjectionSuspendedMessage("req-a", true), "client-a");
        await server.ProcessAsync(new SetInjectionSuspendedMessage("req-b", true), "client-b");
        server.ReleaseConnection("client-a");

        Assert.True(host.IsInjectionSuspended);

        server.ReleaseConnection("client-b");
        Assert.False(host.IsInjectionSuspended);
    }

    [Fact]
    public async Task Disconnect_Releases_Editor_Suspension_Without_Stopping_Or_Unpausing()
    {
        var (server, _, host) = CreateRunning();
        await server.ProcessAsync(new StartMessage("req-start", 0));
        await server.ProcessAsync(new SetMappingEnabledMessage("req-pause", false));
        var stream = new ScriptedStream();
        stream.Script(new HelloMessage("gui-1", IpcContract.ProtocolVersion, "client-1", IpcSession.Current));
        stream.Script(new SetInjectionSuspendedMessage("req-suspend", true));

        await server.ServeConnectionAsync(stream, TestContext.Current.CancellationToken);

        Assert.True(host.IsRunning);
        Assert.False(host.IsMappingEnabled);
        Assert.False(host.IsInjectionSuspended);
        Assert.Equal(0, host.StopCalls);
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
    public void The_Windows_Pipe_Acl_Shares_The_Current_User_Across_Integrity_Levels()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("PipeSecurity is Windows-only.");
            return;
        }

        var sid = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1000");
        var security = DaemonHost.CreateMappingPipeSecurity(sid.Value);
        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.IO.Pipes.PipeAccessRule>()
            .ToArray();

        var rule = Assert.Single(rules);
        Assert.Equal(sid, rule.IdentityReference);
        Assert.Equal(
            System.IO.Pipes.PipeAccessRights.ReadWrite
                | System.IO.Pipes.PipeAccessRights.CreateNewInstance
                | System.IO.Pipes.PipeAccessRights.Synchronize,
            rule.PipeAccessRights);
        Assert.Equal(System.Security.AccessControl.AccessControlType.Allow, rule.AccessControlType);
    }

    [Fact]
    public void The_Pipe_Name_Is_The_Control_Channel_And_Not_The_Injection_Mutex()
    {
        Assert.Equal("DeziroJoy.Mapping", IpcContract.PipeName);
        Assert.NotEqual(InjectionMutex.DefaultName, IpcContract.PipeName);
    }
}
