using DeziroJoy.Core.Input;
using DeziroJoy.Platform.Sdl3;
using SDL3;
using Xunit;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// Tests that talk to the real SDL runtime. SDL's device list and mapping database are
/// process-global, so these must not run at the same time as any other test that attaches a
/// device or adds a mapping - hence the shared collection.
/// </summary>
[CollectionDefinition("DeziroJoy.SdlNative")]
public abstract class SdlNativeGroup;

/// <summary>
/// The mapping-supplement flow against the real SDL runtime, driven by a virtual joystick.
/// <para>
/// A virtual joystick is a real SDL device: it enumerates, it carries a GUID, and SDL decides
/// whether it is a standard gamepad by looking that GUID up in its mapping database. That makes
/// it the only way to exercise this flow without hardware - and it is what caught the bug where
/// every plain joystick was recorded as already having a standard mapping, which made
/// <c>joysticks</c> list nothing and left <c>map</c> with no device to offer. The scripted fake
/// computed the flag from the gamepad-id set, which is the correct semantics; only the real
/// implementation disagreed with it, so only a real device could show the difference.
/// </para>
/// <para>
/// The GUID comes from the device, not from the test, and the mapping is the one
/// <see cref="Sdl3MappingCaptureSession"/> actually produces - so the assertions cover the whole
/// chain from "SDL sees a plain joystick" to "the daemon's own scan sees a standard gamepad".
/// </para>
/// </summary>
[Collection("DeziroJoy.SdlNative")]
public sealed class Sdl3JoystickEndToEndTests : IDisposable
{
    /// <summary>
    /// SDL's mapping database is process-global and cannot be un-added from, so every test maps
    /// a device of its own: the vendor id is the test's index, which yields a distinct GUID (the
    /// GUID carries a CRC derived from it, exactly like a real DInput device's). Two tests that
    /// shared a device would silently map each other's hardware.
    /// </summary>
    private const ushort ProductId = 0x0009;

    private static ushort VendorIdFor(int test) => (ushort)(0x2810 + test);

    private readonly List<uint> _attached = [];
    private readonly string _directory;

    public Sdl3JoystickEndToEndTests()
    {
        _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dezirojoy-sdl-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        foreach (var instanceId in _attached)
        {
            SDL.DetachVirtualJoystick(instanceId);
        }

        _attached.Clear();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static bool RuntimeAvailableForTest() => RuntimeAvailable();

    private static bool RuntimeAvailable()
    {
        try
        {
            return SDL.Init(SDL.InitFlags.Gamepad | SDL.InitFlags.Joystick);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attaches one virtual joystick with no standard mapping. SDL refuses a descriptor that was
    /// not initialized the way it expects, so the version field is the struct's own size - the
    /// same convention every SDL interface struct uses.
    /// </summary>
    private uint AttachPlainJoystick(ushort vendorId)
    {
        var descriptor = new SDL.VirtualJoystickDesc
        {
            Version = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<SDL.VirtualJoystickDesc>(),
            Type = SDL.JoystickType.Unknown,
            VendorID = vendorId,
            ProductID = ProductId,
            NAxes = 4,
            NButtons = 12,
            NBalls = 0,
            NHats = 1,
            // No button or axis mask: a mask lets SDL synthesize a standard layout, which would
            // make this device a gamepad before the test has mapped anything.
            ButtonMask = 0,
            AxisMask = 0,
        };

        var instanceId = SDL.AttachVirtualJoystick(in descriptor);
        Assert.NotEqual(0u, instanceId);
        _attached.Add(instanceId);
        return instanceId;
    }

    private static string GuidOf(uint instanceId)
    {
        Span<byte> buffer = stackalloc byte[33];
        SDL.GUIDToString(SDL.GetJoystickGUIDForID(instanceId), buffer, buffer.Length);
        return System.Text.Encoding.UTF8.GetString(buffer[..32]);
    }

    /// <summary>A mapping for every required control, as the capture flow would produce it.</summary>
    private static string CapturedMapping(string guid, string platform)
    {
        var session = new Sdl3MappingCaptureSession();
        foreach (var step in session.Steps)
        {
            session.TryAssign(step, TokenFor(step));
        }

        return session.BuildMapping(guid, "Virtual Test Pad", platform);
    }

    private static string TokenFor(Sdl3MappingStep step) => step.Role switch
    {
        Sdl3MappingStepRole.Trigger => "+a4",
        Sdl3MappingStepRole.Stick => "a0",
        _ => step.Key switch
        {
            "dpup" => "h0.1",
            "dpdown" => "h0.4",
            "dpleft" => "h0.8",
            "dpright" => "h0.2",
            _ => "b0",
        },
    };

    [Fact]
    public void An_Unmapped_Device_Is_Listed_With_The_Evidence_A_Mapping_Is_Written_Against()
    {
        if (!RuntimeAvailable())
        {
            return;
        }

        var instanceId = AttachPlainJoystick(VendorIdFor(2));

        var scan = Sdl3JoystickCatalog.Scan();

        Assert.True(scan.Initialized);
        var listed = Assert.Single(scan.Unmapped);
        Assert.Equal(instanceId, listed.InstanceId);
        Assert.False(listed.HasStandardMapping);
        Assert.Equal(GuidOf(instanceId), listed.GuidString);
        Assert.Equal(VendorIdFor(2), listed.Vendor);
        Assert.Equal(ProductId, listed.Product);
        Assert.Equal(12, listed.ButtonCount);
        Assert.Equal(4, listed.AxisCount);
        Assert.Equal(1, listed.HatCount);
        Assert.Contains("map", scan.Hint);
    }

    [Fact]
    public void A_Device_Sdl_Already_Maps_Is_Not_Offered_For_Mapping()
    {
        if (!RuntimeAvailable())
        {
            return;
        }

        var instanceId = AttachPlainJoystick(VendorIdFor(3));
        var guid = GuidOf(instanceId);

        Assert.True(Sdl3JoystickCatalog.ApplyMapping(
            CapturedMapping(guid, Sdl3JoystickCatalog.PlatformName),
            guid).Applied);

        // A second unit of the same model: same GUID, so the mapping covers it too.
        var second = AttachPlainJoystick(VendorIdFor(3));
        Assert.Equal(guid, GuidOf(second));

        var scan = Sdl3JoystickCatalog.Scan();

        Assert.DoesNotContain(scan.Unmapped, joystick => joystick.InstanceId == second);
        Assert.Contains(scan.Joysticks, joystick => joystick.InstanceId == second && joystick.HasStandardMapping);
        Assert.True(Sdl3JoystickCatalog.IsGamepad(second));
    }

    [Fact]
    public void Saving_The_Captured_Mapping_Makes_The_Device_A_Standard_Gamepad()
    {
        if (!RuntimeAvailable())
        {
            return;
        }

        var instanceId = AttachPlainJoystick(VendorIdFor(4));
        var guid = GuidOf(instanceId);
        var platform = Sdl3JoystickCatalog.PlatformName;
        var mapping = CapturedMapping(guid, platform);

        // The mapping the capture flow built is one SDL accepts, key for key.
        var entry = Sdl3MappingFile.TryBuild(guid, "Virtual Test Pad", mapping);
        Assert.NotNull(entry);

        var path = System.IO.Path.Combine(_directory, Sdl3MappingFile.FileName);
        Assert.True(Sdl3MappingFile.Save(path, entry!).Saved);

        var applied = Sdl3JoystickCatalog.ApplyMapping(mapping, guid);
        Assert.True(applied.Applied, applied.Error);
        Assert.NotNull(applied.RegisteredMapping);

        // SDL applies a runtime mapping immediately, including to a device it had already
        // enumerated as a plain joystick - so this is answerable now, not after a restart.
        Assert.True(Sdl3JoystickCatalog.IsGamepad(instanceId));
    }

    [Fact]
    public void The_Daemon_Scan_Recognizes_The_Mapped_Device()
    {
        if (!RuntimeAvailable())
        {
            return;
        }

        var instanceId = AttachPlainJoystick(VendorIdFor(5));
        var guid = GuidOf(instanceId);
        var path = System.IO.Path.Combine(_directory, Sdl3MappingFile.FileName);

        var entry = Sdl3MappingFile.TryBuild(
            guid,
            "Virtual Test Pad",
            CapturedMapping(guid, Sdl3JoystickCatalog.PlatformName));
        Assert.True(Sdl3MappingFile.Save(path, entry!).Saved);

        // The daemon's own path: a fresh provider that loads the persisted file before its first
        // enumeration. If the mapping were loaded after enumerating, the first scan would still
        // see a plain joystick and the device would be invisible until the next one.
        var diagnostic = Sdl3GamepadProvider.ProbeOnce(new Sdl3MappingFileSource(path));

        Assert.Equal(Sdl3ScanOutcome.GamepadsFound, diagnostic.Outcome);
        Assert.Equal(1, diagnostic.AppliedMappingCount);
        Assert.True(diagnostic.HasAppliedMappings);
        Assert.Null(diagnostic.MappingLoadError);
        Assert.Contains(diagnostic.Devices, device => device.Name == "Virtual Test Pad");
        Assert.Contains(diagnostic.Devices, device => device.GuidString == guid);
    }

    [Fact]
    public void The_Mapped_Device_Describes_Its_Controls_From_What_Sdl_Declares()
    {
        if (!RuntimeAvailable())
        {
            return;
        }

        var instanceId = AttachPlainJoystick(VendorIdFor(6));
        var guid = GuidOf(instanceId);
        var path = System.IO.Path.Combine(_directory, Sdl3MappingFile.FileName);

        var entry = Sdl3MappingFile.TryBuild(
            guid,
            "Virtual Test Pad",
            CapturedMapping(guid, Sdl3JoystickCatalog.PlatformName));
        Assert.True(Sdl3MappingFile.Save(path, entry!).Saved);

        var descriptor = Sdl3GamepadProvider.ProbeOnce(new Sdl3MappingFileSource(path));
        var device = descriptor.Devices.Single(item => item.GuidString == guid);

        // Every required control the mapping describes is one SDL now declares, so the generic
        // panel has something to show for the device the user just mapped by hand.
        Assert.Contains("a", device.DeclaredButtons);
        Assert.Contains("b", device.DeclaredButtons);
        Assert.Contains("x", device.DeclaredButtons);
        Assert.Contains("y", device.DeclaredButtons);
        Assert.Contains("leftx", device.DeclaredAxes);
        Assert.Contains("lefttrigger", device.DeclaredAxes);

        var controls = Sdl3ControlSet.Build(device);

        Assert.Contains(controls, control => control.Label == "A");
        Assert.Contains(controls, control => control.Label == "左摇杆");
        Assert.Contains(controls, control => control.Label == "左扳机");
        Assert.Contains(controls, control => control.Kind == GamepadControlKind.Direction);
    }

    [Fact]
    public void A_Hand_Mapped_DInput_Device_Keeps_The_Positional_Face_Semantic()
    {
        // The DInput mapping-supplement flow has to be unaffected by the glyph-driven face map.
        // Such a device reports no glyphs SDL recognizes, so every slot takes the positional
        // answer - which is exactly what the mapping the user wrote by hand assumed, because the
        // capture flow's own keys are SDL's positional ones (a = south, b = east, ...).
        if (!RuntimeAvailable())
        {
            return;
        }

        var instanceId = AttachPlainJoystick(VendorIdFor(8));
        var guid = GuidOf(instanceId);
        var path = System.IO.Path.Combine(_directory, Sdl3MappingFile.FileName);

        var entry = Sdl3MappingFile.TryBuild(
            guid,
            "Virtual Test Pad",
            CapturedMapping(guid, Sdl3JoystickCatalog.PlatformName));
        Assert.True(Sdl3MappingFile.Save(path, entry!).Saved);

        var device = Sdl3GamepadProvider.ProbeOnce(new Sdl3MappingFileSource(path))
            .Devices.Single(item => item.GuidString == guid);

        var map = Sdl3FaceButtonMap.From(device);
        Assert.Equal(Sdl3FaceButtonMap.Positional, map);

        // Pressing the button the mapping bound to SDL's north slot ("y:b3") is logical Y - the
        // same answer the positional table gave before the glyph-driven map existed.
        var tracker = new Sdl3GamepadStateTracker(map);
        tracker.ApplyButton(SDL.GamepadButton.North, true);

        Assert.True(tracker.Snapshot().PressedButtons.Has(GamepadButton.Y));
        Assert.False(tracker.Snapshot().PressedButtons.Has(GamepadButton.X));
    }

    [Fact]
    public void A_Mapping_Whose_Face_Keys_Sdl_Drops_Is_Refused_Rather_Than_Saved()
    {
        if (!RuntimeAvailable())
        {
            return;
        }

        var instanceId = AttachPlainJoystick(VendorIdFor(7));
        var guid = GuidOf(instanceId);
        var path = System.IO.Path.Combine(_directory, Sdl3MappingFile.FileName);

        // The bug this guards against: SDL's mapping keys for the face buttons are a/b/x/y, and
        // an element it does not recognize is dropped *without an error* - so a mapping written
        // with the enum's positional names loads, applies, and has no face buttons at all.
        var broken = $"{guid},Virtual Test Pad,platform:Linux,south:b0,east:b1,west:b2,north:b3,leftx:a0,lefty:a1";

        Assert.Null(Sdl3MappingFile.TryBuild(guid, "Virtual Test Pad", broken));

        var saved = Sdl3MappingFile.Save(path, new Sdl3MappingEntry
        {
            GuidString = guid,
            DeviceName = "Virtual Test Pad",
            Mapping = broken,
            UpdatedUtc = "2026-11-17T00:00:00.0000000Z",
        });

        Assert.False(saved.Saved);
        Assert.False(File.Exists(path));
    }
}
