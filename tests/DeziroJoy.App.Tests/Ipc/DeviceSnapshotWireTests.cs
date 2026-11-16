using System.Text.Json;
using DeziroJoy.App.Ipc;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Ipc;

/// <summary>
/// The wire shape of <see cref="ConnectedDeviceSnapshot.Controls"/>: the U1 generic panel's
/// descriptions must survive the pipe, and a daemon that describes nothing must keep writing
/// exactly the bytes it wrote before the field existed - old clients ignore an unknown field,
/// and an old daemon's snapshots must read as "not described", never as "no controls".
/// </summary>
public class DeviceSnapshotWireTests
{
    private static IReadOnlyList<GamepadControlDescriptor> DescribedControls()
        => GamepadControlSet.Build(new GamepadControlDeclaration
        {
            Buttons = GamepadButtonMask.A | GamepadButtonMask.B | GamepadButtonMask.LeftBumper,
            Sticks = [GamepadStick.Left],
            ExtraButtons = [301],
        });

    [Fact]
    public void A_Snapshot_From_A_Daemon_That_Never_Described_Controls_Reads_As_Not_Described()
    {
        // A daemon that predates the field writes no Controls at all. The window must read that
        // as "not described yet" - never as a device with no controls, which would hide the
        // generic panel for a pad the platform simply has not written a description for. The
        // bytes are literal, in the wire's own shape: enums travel as numbers here.
        var restored = Assert.IsType<StateChangedMessage>(IpcSerializer.Deserialize(
            """{"kind":"state-changed","DaemonInstanceId":"daemon-old","Snapshot":{"Sequence":1,"Revision":0,"Running":true,"MappingEnabled":true,"InjectorPid":1,"ProfileName":"P","Devices":[{"DeviceId":"xinput:0","Name":"Xbox Wireless Controller","Layout":0}]}}"""));

        var device = Assert.Single(restored.Snapshot.Devices);
        Assert.Null(device.Controls);
    }

    [Fact]
    public void Described_Controls_Round_Trip_Through_The_Wire()
    {
        var message = new StateChangedMessage(
            "daemon-1",
            new StateSnapshot(1, 0, true, true, 1, null, "P", null, 0, 0, true)
            {
                Devices =
                [
                    new ConnectedDeviceSnapshot(
                        "sdl:guid-1",
                        "Unknown Third-Party Pad",
                        GamepadLayout.Generic,
                        DescribedControls()),
                ],
            });

        var json = IpcSerializer.Serialize(message);
        var restored = Assert.IsType<StateChangedMessage>(IpcSerializer.Deserialize(json));

        var device = Assert.Single(restored.Snapshot.Devices);
        var controls = Assert.IsAssignableFrom<IReadOnlyList<GamepadControlDescriptor>>(device.Controls);
        Assert.Equal(DescribedControls(), controls);

        // The build order is the panel contract: directions, main buttons, shoulders, then
        // extensions - a described control must not arrive reordered by the pipe.
        Assert.Equal(
            [GamepadControlZone.Directions, GamepadControlZone.MainButtons, GamepadControlZone.MainButtons, GamepadControlZone.Shoulders, GamepadControlZone.Extensions],
            controls.Select(control => control.Zone).ToArray());
        Assert.Contains(controls, control => control.Source is StickBinding { Stick: GamepadStick.Left });
        Assert.Contains(controls, control => control.Source is ButtonBinding { Code: 301 });
    }

    [Fact]
    public void A_Device_With_No_Description_Omits_The_Field_Entirely()
    {
        // Empty means "not described yet", so it travels as null and the field is not written:
        // the JSON stays byte-identical to what a daemon before the field produced.
        var message = new StateChangedMessage(
            "daemon-1",
            new StateSnapshot(1, 0, true, true, 1, null, "P", null, 0, 0, true)
            {
                Devices = [new ConnectedDeviceSnapshot("xinput:0", "Xbox Wireless Controller", GamepadLayout.Xbox)],
            });

        var json = IpcSerializer.Serialize(message);

        Assert.DoesNotContain("Controls", json, StringComparison.Ordinal);
        Assert.DoesNotContain("controls", json, StringComparison.Ordinal);

        var restored = Assert.IsType<StateChangedMessage>(IpcSerializer.Deserialize(json));
        Assert.Null(Assert.Single(restored.Snapshot.Devices).Controls);
    }

    [Fact]
    public void A_Described_Device_Writes_The_Field_An_Old_Client_Ignores()
    {
        // The forward-compatibility direction: a new daemon writes Controls, and an old client
        // that knows nothing about the field still deserializes the message - System.Text.Json
        // ignores unknown properties, so the snapshot reads exactly as it did before.
        var message = new StateChangedMessage(
            "daemon-1",
            new StateSnapshot(1, 0, true, true, 1, null, "P", null, 0, 0, true)
            {
                Devices =
                [
                    new ConnectedDeviceSnapshot("sdl:guid-1", "Unknown Pad", GamepadLayout.Generic, DescribedControls()),
                ],
            });

        var json = IpcSerializer.Serialize(message);

        using var document = JsonDocument.Parse(json);
        var device = document.RootElement.GetProperty("Snapshot").GetProperty("Devices")[0];
        Assert.True(device.TryGetProperty("Controls", out var controls));
        Assert.Equal(JsonValueKind.Array, controls.ValueKind);
        Assert.True(controls.GetArrayLength() > 0);
    }
}
