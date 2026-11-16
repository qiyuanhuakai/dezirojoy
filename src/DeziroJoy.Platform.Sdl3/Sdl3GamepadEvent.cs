namespace DeziroJoy.Platform.Sdl3;

/// <summary>Gamepad lifecycle transitions the provider reacts to.</summary>
public enum Sdl3GamepadEventType
{
    /// <summary>A joystick appeared and SDL recognizes it as a standard gamepad.</summary>
    Added,

    /// <summary>A gamepad was disconnected; its handle is no longer valid.</summary>
    Removed,

    /// <summary>The mapping of an already-open gamepad changed at runtime (user mapping).</summary>
    Remapped,
}

/// <summary>
/// One gamepad lifecycle event drained from the SDL event queue.
/// <paramref name="InstanceId"/> is SDL's per-run joystick instance id - it is deliberately
/// not the device identity (see <see cref="Sdl3GamepadIdentity"/>).
/// </summary>
public readonly record struct Sdl3GamepadEvent(Sdl3GamepadEventType Type, uint InstanceId);
