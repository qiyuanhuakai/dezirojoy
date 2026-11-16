namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// Opaque handle to an open SDL gamepad (<c>SDL_Gamepad*</c>). The provider only ever passes
/// it back to the <see cref="ISdl3Api"/> that produced it, which keeps every SDL call on the
/// interop side of the seam - the provider, the normalization logic and the tests never touch
/// the pointer. Reference equality distinguishes handles, so two devices can never alias even
/// when a test fake hands out synthetic pointers.
/// </summary>
public sealed class Sdl3GamepadHandle
{
    internal Sdl3GamepadHandle(nint pointer) => Pointer = pointer;

    internal nint Pointer { get; }
}
