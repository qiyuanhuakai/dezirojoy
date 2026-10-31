using DeziroJoy.Core.Input;

namespace DeziroJoy.Core.Platform;

/// <summary>
/// Source of normalized gamepad snapshots. Implementations poll the OS
/// (XInput, evdev, SDL, ...); the engine host drives them on a fixed timestep.
/// </summary>
public interface IGamepadProvider : IDisposable
{
    /// <summary>Descriptors of currently connected gamepads (cheap; poll each tick).</summary>
    IReadOnlyList<GamepadDescriptor> GetConnected();

    /// <summary>Latest snapshot for a device, or null when it is gone.</summary>
    IGamepadState? Poll(string deviceId);
}
