using DeziroJoy.Core.Input;
using DeziroJoy.Core.Platform;

namespace DeziroJoy.App.Services;

/// <summary>
/// Two input channels presented as one gamepad. The primary channel (XInput on Windows) carries
/// the standard buttons and both sticks; the secondary channel (HID) carries the raw codes for
/// buttons the primary cannot see - macro keys, back paddles, pedals.
/// </summary>
/// <remarks>
/// The two channels describe the same physical device twice, so they must be correlated rather
/// than simply concatenated: a device known to both appears once, with the primary's buttons and
/// sticks and the secondary's extra codes. A device only the secondary sees is still listed, so a
/// pad XInput does not recognise at all keeps working through HID alone.
/// </remarks>
public sealed class CompositeGamepadProvider : IGamepadProvider
{
    private readonly IGamepadProvider _primary;

    private readonly IGamepadProvider _secondary;

    private readonly Func<string, string?> _correlate;

    public CompositeGamepadProvider(
        IGamepadProvider primary,
        IGamepadProvider secondary,
        Func<string, string?> correlate)
    {
        _primary = primary;
        _secondary = secondary;
        _correlate = correlate;
    }

    /// <summary>Descriptors of every connected gamepad, each physical device listed once.</summary>
    public IReadOnlyList<GamepadDescriptor> GetConnected()
    {
        var descriptors = new List<GamepadDescriptor>(_primary.GetConnected());

        foreach (var descriptor in _secondary.GetConnected())
        {
            if (_correlate(descriptor.DeviceId) is { } primaryId
                && descriptors.Any(d => d.DeviceId == primaryId))
            {
                continue;
            }

            descriptors.Add(descriptor);
        }

        return descriptors;
    }

    /// <summary>
    /// Latest snapshot for a device. A device the primary knows about keeps its buttons and
    /// sticks from the primary and gains its extra codes from the secondary; a device only the
    /// secondary knows is returned as the secondary reported it.
    /// </summary>
    public IGamepadState? Poll(string deviceId)
    {
        var state = _primary.Poll(deviceId);

        if (FindSecondary(deviceId) is not { } secondaryId)
        {
            return state;
        }

        var secondaryState = _secondary.Poll(secondaryId);

        if (state is null)
        {
            return secondaryState;
        }

        if (secondaryState is null || secondaryState.ExtraButtons.Count == 0)
        {
            return state;
        }

        return GamepadState.Create(
            state.PressedButtons,
            state.LeftStickX,
            state.LeftStickY,
            state.RightStickX,
            state.RightStickY,
            secondaryState.ExtraButtons);
    }

    /// <summary>
    /// The secondary device that describes the same physical pad as <paramref name="deviceId"/>,
    /// or null when there is none.
    /// </summary>
    private string? FindSecondary(string deviceId)
    {
        foreach (var descriptor in _secondary.GetConnected())
        {
            if (_correlate(descriptor.DeviceId) == deviceId)
            {
                return descriptor.DeviceId;
            }
        }

        return null;
    }

    public void Dispose()
    {
        _primary.Dispose();
        _secondary.Dispose();
    }
}
