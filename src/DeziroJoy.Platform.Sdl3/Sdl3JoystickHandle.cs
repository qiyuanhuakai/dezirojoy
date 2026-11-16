namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// One handle to an open plain joystick, the same ownership shape
/// <see cref="Sdl3GamepadHandle"/> uses: a native pointer the caller closes exactly once,
/// with the provider-style rule that closing an already-closed handle is a no-op.
/// </summary>
public sealed class Sdl3JoystickHandle
{
    internal Sdl3JoystickHandle(nint pointer) => Pointer = pointer;

    internal nint Pointer { get; private set; }

    /// <summary>True once this handle has been closed; closing twice does not call SDL again.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Marks the handle closed. The real close is the api's job, not the handle's.</summary>
    internal void MarkClosed()
    {
        IsClosed = true;
        Pointer = IntPtr.Zero;
    }
}
