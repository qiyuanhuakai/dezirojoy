using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Sdl3;
using SDL3;
using Xunit;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// What a remap does to the face-button semantic. A remap changes what the mapping declares, and
/// with it which glyph sits in each slot - so the tracker and the description both have to be
/// rebuilt, or the device keeps executing the previous layout's buttons under the new names.
/// </summary>
public sealed class Sdl3FaceButtonRemapTests : IDisposable
{
    private readonly FakeSdl3Api _api = new();
    private readonly Sdl3GamepadProvider _provider;

    public Sdl3FaceButtonRemapTests()
    {
        // Zero interval: every GetConnected scans, so a test can observe the effect of each
        // scripted change without racing the rate limiter.
        _provider = new Sdl3GamepadProvider(_api, TimeSpan.Zero);
    }

    public void Dispose() => _provider.Dispose();

    private static FakeSdl3Api.FakeDevice Nintendo()
        => FakeSdl3Api.MakeDevice(
            instanceId: 1,
            name: "Nintendo Switch Pro Controller",
            serial: "unit-ns",
            faceButtonLabels: ["B", "A", "Y", "X"]);

    [Fact]
    public void A_Remap_Rebuilds_The_Tracker_From_The_New_Glyphs()
    {
        var device = Nintendo();
        _api.AddDevice(device);
        var deviceId = _provider.GetConnected().Single().DeviceId;

        // Before: the north slot is the physical X, so it is logical X.
        device.Pressed.Add(SDL.GamepadButton.North);
        Assert.True(State(deviceId).IsPressed(GamepadButton.X));

        // A mapping update puts the Xbox glyphs in the same slots.
        device.Info = device.Info with { FaceButtonLabels = ["A", "B", "X", "Y"] };
        _api.Queue(Sdl3GamepadEventType.Remapped, 1);

        var after = _provider.GetConnected().Single();

        device.Pressed.Clear();
        device.Pressed.Add(SDL.GamepadButton.North);
        Assert.True(State(deviceId).IsPressed(GamepadButton.Y), "north is now the Xbox Y");
        Assert.False(State(deviceId).IsPressed(GamepadButton.X));

        // And the description followed: the control labelled Y is the one bound to logical Y.
        Assert.Contains(after.Controls, control => control.Label == "Y");
        Assert.IsType<ButtonBinding>(after.Controls.Single(control => control.Label == "Y").Source);
        Assert.Equal(
            GamepadButton.Y,
            ((ButtonBinding)after.Controls.Single(control => control.Label == "Y").Source).Button);
    }

    [Fact]
    public void A_Remap_Replaces_The_Face_Map_So_The_Old_Layout_Cannot_Linger()
    {
        var device = Nintendo();
        _api.AddDevice(device);
        var deviceId = _provider.GetConnected().Single().DeviceId;

        device.Pressed.Add(SDL.GamepadButton.North);
        Assert.True(State(deviceId).IsPressed(GamepadButton.X));

        // The button is still physically held across the remap. Under the new glyphs the north
        // slot is the Xbox Y, so the logical name follows the physical button - and the old X
        // does not linger. Resetting the tracker without replacing its map would keep reporting
        // X here, which is the layout the device no longer has.
        device.Info = device.Info with { FaceButtonLabels = ["A", "B", "X", "Y"] };
        _api.Queue(Sdl3GamepadEventType.Remapped, 1);

        // The event queue is drained by the scan, which is the order the engine host uses:
        // GetConnected once per tick, then poll every pad.
        _provider.GetConnected();

        // The button is still physically held across the remap. Under the new glyphs the north
        // slot is the Xbox Y, so the logical name follows the physical button - and the old X
        // does not linger. Resetting the tracker without replacing its map would keep reporting
        // X here, which is the layout the device no longer has.
        var held = State(deviceId);

        Assert.False(held.IsPressed(GamepadButton.X));
        Assert.True(held.IsPressed(GamepadButton.Y));

        device.Pressed.Clear();

        var released = State(deviceId);

        Assert.False(released.IsPressed(GamepadButton.Y));
        Assert.False(released.IsPressed(GamepadButton.X));
    }

    [Fact]
    public void A_Remap_That_Only_Changes_The_Declaration_Keeps_The_Face_Semantic()
    {
        var device = Nintendo();
        _api.AddDevice(device);
        var deviceId = _provider.GetConnected().Single().DeviceId;

        device.Info = device.Info with { DeclaredButtons = [.. device.Info.DeclaredButtons, "misc1"] };
        _api.Queue(Sdl3GamepadEventType.Remapped, 1);

        device.Pressed.Add(SDL.GamepadButton.North);

        Assert.True(State(deviceId).IsPressed(GamepadButton.X));
    }

    [Fact]
    public void A_Device_That_Never_Remaps_Keeps_One_Tracker()
    {
        var device = Nintendo();
        _api.AddDevice(device);
        var deviceId = _provider.GetConnected().Single().DeviceId;

        _provider.GetConnected();
        _provider.GetConnected();

        device.Pressed.Add(SDL.GamepadButton.North);

        Assert.True(State(deviceId).IsPressed(GamepadButton.X));
    }

    private GamepadState State(string deviceId) => (GamepadState)_provider.Poll(deviceId)!;
}
