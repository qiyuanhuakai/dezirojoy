using DeziroJoy.App.Services;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Platform;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The Windows input path is two channels: XInput sees the 14 standard buttons and both sticks,
/// HID sees the non-standard buttons XInput cannot. The composite provider is what makes them
/// one gamepad instead of two.
/// </summary>
public sealed class CompositeGamepadProviderTests
{
    private const string XInputId = "xinput:0";

    private const string HidId = "hid:048d:ce00:001";

    private static readonly int[] ExtraButtons = [305, 306];

    private static GamepadDescriptor Descriptor(string id) => new(id, "Xbox Wireless Controller");

    [Fact]
    public void A_Hid_Device_That_Matches_An_XInput_Device_Merges_Its_Extra_Buttons()
    {
        var xinput = new StubProvider();
        xinput.Add(XInputId, Descriptor(XInputId));
        xinput.SetState(XInputId, GamepadState.Create(GamepadButtonMask.A, 0f, 0f, 0f, 0f));

        var hid = new StubProvider();
        hid.Add(HidId, Descriptor(HidId));
        hid.SetState(HidId, GamepadState.Create(default, 0f, 0f, 0f, 0f, [305, 306]));

        var composite = new CompositeGamepadProvider(xinput, hid, id => XInputId);

        var state = Assert.IsType<GamepadState>(composite.Poll(XInputId));

        Assert.True(state.IsPressed(GamepadButton.A), "the standard buttons stay with XInput");
        Assert.Equal(ExtraButtons, state.ExtraButtons);
    }

    [Fact]
    public void Without_A_Matching_Hid_Device_The_XInput_State_Passes_Through_Unchanged()
    {
        var xinput = new StubProvider();
        xinput.Add(XInputId, Descriptor(XInputId));
        xinput.SetState(XInputId, GamepadState.Create(GamepadButtonMask.B, 0.25f, 0f, 0f, 0f));

        var hid = new StubProvider();

        var composite = new CompositeGamepadProvider(xinput, hid, id => XInputId);

        var state = Assert.IsType<GamepadState>(composite.Poll(XInputId));

        Assert.True(state.IsPressed(GamepadButton.B));
        Assert.Equal(0.25f, state.LeftStickX);
        Assert.Empty(state.ExtraButtons);
    }

    [Fact]
    public void A_Hid_Only_Device_Is_Listed_Even_When_XInput_Does_Not_See_It()
    {
        var xinput = new StubProvider();

        var hid = new StubProvider();
        hid.Add(HidId, Descriptor(HidId));
        hid.SetState(HidId, GamepadState.Create(default, 0f, 0f, 0f, 0f, [305]));

        var composite = new CompositeGamepadProvider(xinput, hid, id => XInputId);

        var ids = composite.GetConnected().Select(d => d.DeviceId).ToList();

        Assert.Contains(HidId, ids);
    }

    [Fact]
    public void Disposing_The_Composite_Disposes_Both_Channels()
    {
        var xinput = new StubProvider();
        var hid = new StubProvider();

        var composite = new CompositeGamepadProvider(xinput, hid, id => XInputId);
        composite.Dispose();

        Assert.True(xinput.Disposed);
        Assert.True(hid.Disposed);
    }

    private sealed class StubProvider : IGamepadProvider
    {
        private readonly Dictionary<string, GamepadDescriptor> _devices = [];

        private readonly Dictionary<string, IGamepadState> _states = [];

        public bool Disposed { get; private set; }

        public void Add(string deviceId, GamepadDescriptor descriptor)
        {
            _devices[deviceId] = descriptor;
        }

        public void SetState(string deviceId, IGamepadState state) => _states[deviceId] = state;

        public IReadOnlyList<GamepadDescriptor> GetConnected() => _devices.Values.ToList();

        public IGamepadState? Poll(string deviceId)
            => _states.TryGetValue(deviceId, out var state) ? state : null;

        public void Dispose() => Disposed = true;
    }
}
