using SDL3;
namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// Every SDL call the provider makes, behind one seam. The real implementation
/// (<see cref="Sdl3Api"/>) talks to the SDL3-CS bindings; tests substitute a fake, which is
/// what makes enumeration, hot-plug, remap, failure paths and cleanup deterministic without
/// a device or a native library. The provider itself contains no SDL calls, so the logic it
/// owns - identity, normalization, lifecycle, diagnostics - is fully covered off hardware.
/// </summary>
internal interface ISdl3Api
{
    /// <summary>Version and revision of the linked SDL runtime, or null when unreachable.</summary>
    Sdl3RuntimeInfo? GetRuntimeInfo();

    /// <summary>
    /// Initializes the SDL gamepad subsystem once. Idempotent: a second call on an already
    /// initialized api succeeds without touching SDL again.
    /// </summary>
    bool TryEnsureInitialized(out string? error);

    /// <summary>Releases the gamepad subsystem this api initialized; a no-op when it did not.</summary>
    void Shutdown();

    /// <summary>Pumps the event queue and refreshes gamepad state (no video subsystem here).</summary>
    void UpdateGamepads();

    /// <summary>Drains the queued gamepad lifecycle events since the last call.</summary>
    IReadOnlyList<Sdl3GamepadEvent> PumpEvents();

    /// <summary>Instance ids of the gamepads SDL currently recognizes, in SDL's order.</summary>
    IReadOnlyList<uint> GetGamepadIds();

    /// <summary>
    /// Opens one gamepad. Returns null - with SDL's error text - when the open fails, which
    /// is recorded in the scan diagnostic rather than thrown.
    /// </summary>
    Sdl3GamepadHandle? OpenGamepad(uint instanceId, out string? error);

    /// <summary>Closes one open gamepad; closing twice is a no-op.</summary>
    void CloseGamepad(Sdl3GamepadHandle handle);

    /// <summary>Reads the identity and capability snapshot of one open gamepad.</summary>
    Sdl3GamepadInfo ReadInfo(Sdl3GamepadHandle handle);

    bool GetButton(Sdl3GamepadHandle handle, SDL.GamepadButton button);

    short GetAxis(Sdl3GamepadHandle handle, SDL.GamepadAxis axis);
}
