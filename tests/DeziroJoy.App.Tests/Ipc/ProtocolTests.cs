using System.Text.Json;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Profiles;
using Xunit;

namespace DeziroJoy.App.Tests.Ipc;

/// <summary>
/// The version-1 mapping IPC contract: pipe identity, the messages of section 4.4,
/// full snapshots and input samples, and the serializer that carries them. Wire shape only - no pipe,
/// no daemon, no client.
/// </summary>
public class ProtocolTests
{
    private static string ProfileJson() => ProfileSerializer.Serialize(
        DefaultProfileLibrary.CreateForLayout(GamepadLayout.Generic));

    [Fact]
    public void The_Pipe_Name_Is_DeziroJoy_Mapping_And_Not_The_Injection_Mutex()
    {
        Assert.Equal("DeziroJoy.Mapping", IpcContract.PipeName);
        Assert.NotEqual(InjectionMutex.DefaultName, IpcContract.PipeName);
        Assert.DoesNotContain("Injection", IpcContract.PipeName, StringComparison.Ordinal);
        Assert.DoesNotContain("Global\\", IpcContract.PipeName, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Windows_Pipe_Path_Is_The_Documented_Full_Path_Of_The_Same_Pipe()
    {
        Assert.Equal(@"\\.\pipe\DeziroJoy.Mapping", IpcContract.PipeWindowsPath);
        Assert.EndsWith(IpcContract.PipeName, IpcContract.PipeWindowsPath, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Protocol_Version_Is_One()
    {
        Assert.Equal(1, IpcContract.ProtocolVersion);
    }

    [Fact]
    public void Hello_Round_Trips_With_The_Client_Protocol_Version_And_Instance()
    {
        var message = new HelloMessage(
            "gui-1",
            IpcContract.ProtocolVersion,
            "client-instance-1",
            IpcSession.Current,
            SubscribeInputSamples: true);

        var restored = Assert.IsType<HelloMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(message)));

        Assert.Equal(message, restored);
        Assert.Equal(IpcContract.ProtocolVersion, restored.ProtocolVersion);
        Assert.Equal("client-instance-1", restored.ClientInstanceId);
        Assert.True(restored.SubscribeInputSamples);
    }

    [Fact]
    public void A_Legacy_Hello_Without_The_Input_Subscription_Does_Not_Subscribe()
    {
        var restored = Assert.IsType<HelloMessage>(IpcSerializer.Deserialize(
            $"{{\"kind\":\"hello\",\"ClientId\":\"gui-old\",\"ProtocolVersion\":{IpcContract.ProtocolVersion},\"ClientInstanceId\":\"old\",\"SessionId\":0}}"));

        Assert.False(restored.SubscribeInputSamples);
    }

    [Fact]
    public void HelloAck_Round_Trips_With_The_Negotiated_Protocol_Version()
    {
        var message = new HelloAckMessage(IpcContract.ProtocolVersion, true, "daemon-instance-1", 7);

        var restored = Assert.IsType<HelloAckMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(message)));

        Assert.Equal(message, restored);
        Assert.Equal(IpcContract.ProtocolVersion, restored.ProtocolVersion);
        Assert.True(restored.Accepted);
        Assert.Equal("daemon-instance-1", restored.DaemonInstanceId);
    }

    [Fact]
    public void A_Version_Mismatch_Is_Represented_As_A_Refusal_With_A_Reason()
    {
        var ack = new HelloAckMessage(
            IpcContract.ProtocolVersion + 1,
            false,
            "daemon-instance-1",
            0,
            "the client speaks an older protocol version");

        var restored = Assert.IsType<HelloAckMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(ack)));

        Assert.Equal(ack, restored);
        Assert.False(restored.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(restored.RejectReason));
    }

    [Fact]
    public void GetState_Round_Trips()
    {
        var message = new GetStateMessage("req-1");

        var restored = Assert.IsType<GetStateMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(message)));

        Assert.Equal(message, restored);
    }

    [Fact]
    public void ApplyConfiguration_Round_Trips_The_Profile_Name_And_Body_Verbatim()
    {
        var profileJson = ProfileJson();
        var message = new ApplyConfigurationMessage("req-1", 7, "Xbox Default", profileJson);

        var restored = Assert.IsType<ApplyConfigurationMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(message)));

        Assert.Equal(message, restored);
        Assert.Equal("Xbox Default", restored.ProfileName);
        Assert.Equal(profileJson, restored.ProfileJson);
    }

    [Fact]
    public void ApplyConfiguration_Carries_The_Profile_Name_And_Body_And_No_File_Path()
    {
        var message = new ApplyConfigurationMessage("req-1", 7, "Xbox Default", ProfileJson());

        using var document = JsonDocument.Parse(IpcSerializer.Serialize(message));
        var properties = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["ExpectedRevision", "ProfileJson", "ProfileName", "RequestId", "kind"],
            properties);
    }

    [Fact]
    public void Start_Round_Trips_With_The_Expected_Revision()
    {
        var message = new StartMessage("req-1", 7);

        var restored = Assert.IsType<StartMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(message)));

        Assert.Equal(message, restored);
        Assert.Equal(7, restored.ExpectedRevision);
    }

    [Fact]
    public void Stop_Round_Trips()
    {
        var message = new StopMessage("req-1");

        var restored = Assert.IsType<StopMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(message)));

        Assert.Equal(message, restored);
    }

    [Fact]
    public void Pause_And_Editor_Suspension_Round_Trip_As_Distinct_Messages()
    {
        var pause = new SetMappingEnabledMessage("req-pause", false);
        var suspend = new SetInjectionSuspendedMessage("req-suspend", true);

        var restoredPause = Assert.IsType<SetMappingEnabledMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(pause)));
        var restoredSuspend = Assert.IsType<SetInjectionSuspendedMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(suspend)));

        Assert.Equal(pause, restoredPause);
        Assert.Equal(suspend, restoredSuspend);
        Assert.Contains("set-mapping-enabled", IpcSerializer.Serialize(pause));
        Assert.Contains("set-injection-suspended", IpcSerializer.Serialize(suspend));
    }

    [Fact]
    public void SwitchPresetSlot_Round_Trips_The_Request_Device_And_Slot()
    {
        var message = new SwitchPresetSlotMessage("req-1", "pad-1", 2);

        var restored = Assert.IsType<SwitchPresetSlotMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(message)));

        Assert.Equal(message, restored);
        Assert.Equal("pad-1", restored.DeviceId);
        Assert.Equal(2, restored.Slot);
        Assert.Contains("switch-preset-slot", IpcSerializer.Serialize(message));
    }

    [Fact]
    public void ShutdownDaemon_Round_Trips()
    {
        var message = new ShutdownDaemonMessage("shutdown-1");

        var restored = Assert.IsType<ShutdownDaemonMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(message)));

        Assert.Equal(message, restored);
        Assert.Contains("shutdown-daemon", IpcSerializer.Serialize(message));
    }

    [Fact]
    public void MutationAck_Echoes_The_Request_Id_Across_The_Wire()
    {
        var request = new ApplyConfigurationMessage("req-42", 7, "Xbox Default", ProfileJson());
        var ack = new MutationAckMessage(request.RequestId, true, 8, 9);

        var restored = Assert.IsType<MutationAckMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(ack)));

        Assert.Equal(request.RequestId, restored.RequestId);
        Assert.True(restored.Accepted);
        Assert.Equal(8, restored.Revision);
        Assert.Equal(9, restored.Sequence);
    }

    [Fact]
    public void A_Revision_Mismatch_Ack_Carries_The_Code_The_Current_Revision_And_An_Explanation()
    {
        var ack = new MutationAckMessage(
            "req-1",
            false,
            9,
            12,
            IpcErrorCodes.RevisionMismatch,
            "expected revision 7, the daemon is at 9");

        var restored = Assert.IsType<MutationAckMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(ack)));

        Assert.False(restored.Accepted);
        Assert.Equal("revision-mismatch", restored.ErrorCode);
        Assert.Equal(9, restored.Revision);
        Assert.False(string.IsNullOrWhiteSpace(restored.ErrorMessage));
    }

    [Fact]
    public void StateChanged_Round_Trips_The_Full_Snapshot()
    {
        var message = new StateChangedMessage("daemon-instance-1", new StateSnapshot(
            Sequence: 42,
            Revision: 7,
            Running: true,
            MappingEnabled: true,
            InjectorPid: 1234,
            InjectorIntegrity: "high",
            ProfileName: "Xbox Default",
            LastError: "one event was blocked",
            InjectedEvents: 1000,
            BlockedEvents: 5,
            HasController: true));

        var restored = Assert.IsType<StateChangedMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(message)));

        Assert.Equal(message.DaemonInstanceId, restored.DaemonInstanceId);
        Assert.Equal(message.Snapshot.Sequence, restored.Snapshot.Sequence);
        Assert.Equal(message.Snapshot.Revision, restored.Snapshot.Revision);
        Assert.Equal(message.Snapshot.InjectedEvents, restored.Snapshot.InjectedEvents);
        Assert.Empty(restored.Snapshot.Devices);
    }

    [Fact]
    public void StateSnapshot_Defaults_The_Active_Preset_Slot_To_One()
    {
        var snapshot = new StateSnapshot(0, 0, false, false, 0, null, string.Empty, null, 0, 0, false);

        Assert.Equal(1, snapshot.ActivePresetSlot);
    }

    [Fact]
    public void StateChanged_Round_Trips_The_Active_Preset_Slot()
    {
        var message = new StateChangedMessage("daemon-instance-1", new StateSnapshot(
            Sequence: 42,
            Revision: 7,
            Running: true,
            MappingEnabled: true,
            InjectorPid: 1234,
            InjectorIntegrity: "high",
            ProfileName: "Xbox Default",
            LastError: null,
            InjectedEvents: 1000,
            BlockedEvents: 5,
            HasController: true,
            ActivePresetSlot: 3)
        {
            Devices = [new ConnectedDeviceSnapshot("pad-1", "Xbox Wireless Controller", GamepadLayout.Xbox)],
        });

        var restored = Assert.IsType<StateChangedMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(message)));

        Assert.Equal(3, restored.Snapshot.ActivePresetSlot);
        var device = Assert.Single(restored.Snapshot.Devices);
        Assert.Equal("pad-1", device.DeviceId);
        Assert.Equal("Xbox Wireless Controller", device.Name);
        Assert.Equal(GamepadLayout.Xbox, device.Layout);
    }

    [Fact]
    public void StateChanged_Round_Trips_An_Idle_Snapshot_With_Absent_Fields()
    {
        var message = new StateChangedMessage("daemon-instance-1", new StateSnapshot(
            Sequence: 0,
            Revision: 0,
            Running: false,
            MappingEnabled: false,
            InjectorPid: 0,
            InjectorIntegrity: null,
            ProfileName: string.Empty,
            LastError: null,
            InjectedEvents: 0,
            BlockedEvents: 0,
            HasController: false));

        var restored = Assert.IsType<StateChangedMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(message)));

        Assert.Equal(message.DaemonInstanceId, restored.DaemonInstanceId);
        Assert.Equal(message.Snapshot.Sequence, restored.Snapshot.Sequence);
        Assert.Null(restored.Snapshot.LastError);
        Assert.Null(restored.Snapshot.InjectorIntegrity);
        Assert.Empty(restored.Snapshot.Devices);
    }

    [Fact]
    public void StateChanged_Round_Trips_The_Daemon_Instance_Id_So_A_Restart_Is_Distinguishable()
    {
        var before = new StateChangedMessage("daemon-instance-1", new StateSnapshot(
            1, 1, true, true, 1234, "high", "Xbox Default", null, 10, 0, true));
        var after = new StateChangedMessage("daemon-instance-2", new StateSnapshot(
            1, 1, true, true, 1234, "high", "Xbox Default", null, 10, 0, true));

        var restoredBefore = Assert.IsType<StateChangedMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(before)));
        var restoredAfter = Assert.IsType<StateChangedMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(after)));

        Assert.Equal("daemon-instance-1", restoredBefore.DaemonInstanceId);
        Assert.Equal("daemon-instance-2", restoredAfter.DaemonInstanceId);
        Assert.NotEqual(restoredBefore.DaemonInstanceId, restoredAfter.DaemonInstanceId);
    }

    [Fact]
    public void InputSample_Round_Trips_Buttons_Axes_And_Raw_Codes()
    {
        var message = new InputSampleMessage(
            "daemon-instance-1",
            42,
            "evdev:pad",
            GamepadButtonMask.A | GamepadButtonMask.DPadLeft,
            0.25f,
            -0.5f,
            0.75f,
            -1f,
            [301, 302]);

        var restored = Assert.IsType<InputSampleMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(message)));
        var state = restored.ToGamepadState();

        Assert.Equal(message.DaemonInstanceId, restored.DaemonInstanceId);
        Assert.Equal(42, restored.Sequence);
        Assert.Equal("evdev:pad", restored.DeviceId);
        Assert.Equal(GamepadButtonMask.A | GamepadButtonMask.DPadLeft, state.PressedButtons);
        Assert.Equal(0.25f, state.LeftStickX);
        Assert.Equal(-0.5f, state.LeftStickY);
        Assert.Equal([301, 302], state.ExtraButtons);
    }

    [Fact]
    public void Error_Round_Trips_And_Echoes_The_Request_Id()
    {
        var message = new ErrorMessage("req-7", "daemon-busy", "another mutation is in flight");

        var restored = Assert.IsType<ErrorMessage>(
            IpcSerializer.Deserialize(IpcSerializer.Serialize(message)));

        Assert.Equal(message, restored);
        Assert.Equal("req-7", restored.RequestId);
    }

    [Fact]
    public void Deserialize_Rejects_An_Unknown_Message_Kind()
    {
        Assert.Throws<JsonException>(
            () => IpcSerializer.Deserialize("""{"kind":"reboot-pc"}"""));
    }

    [Fact]
    public void Deserialize_Rejects_Json_Null()
    {
        Assert.Throws<JsonException>(() => IpcSerializer.Deserialize("null"));
    }
}
