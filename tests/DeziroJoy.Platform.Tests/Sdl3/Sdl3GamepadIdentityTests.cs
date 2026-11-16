using DeziroJoy.Platform.Sdl3;
using Xunit;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// Device identity composition (roadmap S2): SDL's instance id is run-local and never a
/// config key; the persistent id layers serial and path on top of the GUID so two identical
/// units of the same model stay distinguishable, and the honest fallbacks are exercised.
/// </summary>
public class Sdl3GamepadIdentityTests
{
    private const string XboxGuid = "03000000000000000000000000000000";

    [Fact]
    public void Device_Id_Combines_Guid_Serial_And_Path()
    {
        var identity = Compose(serial: "A1B2C3", path: "/dev/input/event5");

        Assert.Equal($"sdl:{XboxGuid}:A1B2C3:/dev/input/event5", identity.DeviceId);
        Assert.True(identity.IsStable);
    }

    [Fact]
    public void Same_Model_Units_With_Serials_Get_Distinct_Ids()
    {
        var first = Compose(serial: "unit-1", path: "/dev/input/event5");
        var second = Compose(serial: "unit-2", path: "/dev/input/event6");

        Assert.NotEqual(first.DeviceId, second.DeviceId);
    }

    [Fact]
    public void Units_Without_Serials_Are_Separated_By_Path()
    {
        var first = Compose(serial: "", path: "/dev/input/event5");
        var second = Compose(serial: "", path: "/dev/input/event6");

        Assert.Equal($"sdl:{XboxGuid}:/dev/input/event5", first.DeviceId);
        Assert.NotEqual(first.DeviceId, second.DeviceId);
        Assert.True(first.IsStable);
    }

    [Fact]
    public void Without_Serial_Or_Path_The_Instance_Id_Separates_Units_And_Flags_Instability()
    {
        var first = Compose(serial: "", path: "", instanceId: 7);
        var second = Compose(serial: "", path: "", instanceId: 8);

        Assert.Equal($"sdl:{XboxGuid}:instance-7", first.DeviceId);
        Assert.NotEqual(first.DeviceId, second.DeviceId);

        // The tradeoff is explicit, not silent: configs keyed on this id reattach within the
        // run but not across restarts, and the scan diagnostic says so.
        Assert.False(first.IsStable);
    }

    [Fact]
    public void Components_Are_Sanitized_So_They_Cannot_Forge_Segments()
    {
        var identity = Compose(serial: "a:b", path: "p:q");

        Assert.Equal($"sdl:{XboxGuid}:a_b:p_q", identity.DeviceId);
    }

    [Fact]
    public void Composition_Is_Deterministic()
    {
        var first = Compose(serial: "A1B2C3", path: "/dev/input/event5", instanceId: 7);
        var second = Compose(serial: "A1B2C3", path: "/dev/input/event5", instanceId: 8);

        // The instance id only enters the id when serial and path are both missing, so a
        // re-enumeration of the same device composes the same id.
        Assert.Equal(first.DeviceId, second.DeviceId);
    }

    [Fact]
    public void The_Sdl_Type_Is_Not_Part_Of_The_Device_Id()
    {
        // A mapping-database update that reclassifies a pad (Standard -> XboxOne) must not
        // silently orphan a saved profile, so the type stays a diagnostic field only.
        var before = Compose(type: 1);
        var after = Compose(type: 3);

        Assert.Equal(before.DeviceId, after.DeviceId);
        Assert.Equal("Standard", before.TypeName);
        Assert.Equal("XboxOne", after.TypeName);
    }

    private static Sdl3GamepadIdentity Compose(
        string serial = "",
        string path = "",
        int type = 3,
        uint instanceId = 7)
    {
        var info = FakeSdl3Api.MakeDevice(instanceId).Info with
        {
            Serial = serial,
            Path = path,
            Type = type,
            TypeName = type == 1 ? "Standard" : "XboxOne",
        };

        return Sdl3GamepadIdentity.Compose(info);
    }
}
