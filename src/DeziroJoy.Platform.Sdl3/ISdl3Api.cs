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

    /// <summary>
    /// Hands one mapping string to <c>SDL_AddGamepadMapping</c>. Returns false - with SDL's own
    /// error text - when SDL rejects it, so the caller can refuse to persist a mapping that
    /// would not take effect. SDL answering "already mapped" counts as success: the mapping is
    /// in force either way.
    /// </summary>
    bool TryAddGamepadMapping(string mapping, out string? error);

    /// <summary>
    /// Every joystick SDL currently sees, each carrying whether SDL already recognizes it as a
    /// standard gamepad. The CLI mapping flow filters to unmapped devices; the provider also
    /// uses the raw paths and capabilities to choose one interface of a composite HID unit.
    /// </summary>
    IReadOnlyList<Sdl3JoystickInfo> GetJoysticks();

    /// <summary>
    /// Opens one plain joystick for raw reading. Returns null - with SDL's error text - when
    /// the open fails; the caller records that instead of throwing.
    /// </summary>
    Sdl3JoystickHandle? OpenJoystick(uint instanceId, out string? error);

    /// <summary>Closes one open joystick; closing twice is a no-op.</summary>
    void CloseJoystick(Sdl3JoystickHandle handle);

    /// <summary>Reads the raw button, axis and hat state of one open joystick.</summary>
    Sdl3JoystickSnapshot ReadJoystick(Sdl3JoystickHandle handle);

    /// <summary>
    /// What SDL's mapping database answers for one GUID, or null when it answers nothing. This
    /// is the confirmation that a mapping is registered: unlike the gamepad list, it is not
    /// cached per instance id, so it stays truthful after a mapping is added mid-process.
    /// </summary>
    string? GetMappingForGuid(string guidString);
}
