namespace DeziroJoy.Platform.Windows.Capture;

/// <summary>
/// The native seam a global capture installs: a <c>WH_KEYBOARD_LL</c> hook plus the live modifier
/// read used to seed a session. Everything above it - the session, the orchestrator and every
/// test - depends on this interface, never on <c>SetWindowsHookEx</c>, so the capture logic is
/// exercised without a real hook and without any process-global keyboard state.
/// </summary>
public interface ILowLevelKeyboardHook
{
    bool IsInstalled { get; }

    /// <summary>
    /// The per-event handler. The hook calls it for each key event and returns its result: true
    /// swallows the event, false passes it on. Assigned by the orchestrator while armed and
    /// cleared when disarmed.
    /// </summary>
    Func<RawKeyEvent, bool>? Handler { get; set; }

    /// <summary>
    /// The modifier virtual keys physically held right now, used to seed a fresh session so a
    /// combo begun before arming still reports its modifiers.
    /// </summary>
    IReadOnlyCollection<ushort> ReadHeldModifiers();

    /// <summary>Installs the hook. Returns false when the OS refuses; installing twice is a no-op.</summary>
    bool Install();

    /// <summary>Uninstalls the hook. Uninstalling when not installed is a no-op.</summary>
    void Uninstall();
}
