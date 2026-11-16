using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Platform.Windows.Capture;

/// <summary>Whether a captured event should reach the rest of the system or be swallowed.</summary>
public enum CaptureDecision
{
    /// <summary>Hand the event to the next hook / the focused window unchanged.</summary>
    PassThrough,

    /// <summary>
    /// Discard the event. This is what stops a combo another program registered with
    /// <c>RegisterHotKey</c> from firing: a swallowed key-down never completes that hotkey.
    /// </summary>
    Swallow,
}

/// <summary>
/// The outcome of feeding one <see cref="RawKeyEvent"/> to a <see cref="KeyboardCaptureSession"/>.
/// </summary>
/// <param name="Decision">Swallow or pass the event through.</param>
/// <param name="CapturedKey">
/// The key just pressed, with <paramref name="CapturedModifiers"/>, or null when this event did
/// not press a capturable key. Fired on every non-modifier key-down so the last key pressed wins,
/// exactly as the window path overwrites the target on each press.
/// </param>
/// <param name="Ended">
/// True when this event finished the capture - every held key released, or Escape cancelled it.
/// The caller unhooks and reports the end after this.
/// </param>
public readonly record struct CaptureStep(
    CaptureDecision Decision,
    GamepadKey? CapturedKey,
    KeyModifiers CapturedModifiers,
    bool Ended);

/// <summary>
/// The pure decision core of a global keyboard capture: given the stream of raw key events a
/// low-level hook delivers, it tracks the held modifiers and keys, decides which events to
/// swallow, reports the captured combo, and says when the capture is over. It holds no native
/// state and touches no real keyboard, so it is deterministic and testable anywhere.
/// <para>
/// The completion rule mirrors the window path the editor already used: a non-modifier key press
/// records the target (with whatever modifiers are held), and the capture ends the moment every
/// held key has been released. Modifiers are tracked separately and never become targets, and
/// they do not have to be released for the capture to end - so a modifier left held can never
/// wedge the hook open.
/// </para>
/// </summary>
public sealed class KeyboardCaptureSession
{
    private readonly HashSet<ushort> _heldModifiers = [];
    private readonly HashSet<ushort> _heldKeys = [];
    private bool _ended;

    /// <summary>
    /// A session optionally seeded with the modifiers already held when the hook was installed,
    /// so a combo begun before the capture was armed still reports its modifiers. The seed is
    /// read from the live key state by the orchestrator; a test can pass it directly.
    /// </summary>
    public KeyboardCaptureSession(IEnumerable<ushort>? initiallyHeldModifiers = null)
    {
        if (initiallyHeldModifiers is null)
        {
            return;
        }

        foreach (var vk in initiallyHeldModifiers)
        {
            if (WindowsKeyCapture.IsModifier(vk))
            {
                _heldModifiers.Add(vk);
            }
        }
    }

    public CaptureStep Process(in RawKeyEvent e)
    {
        // Once ended the session is inert: any trailing events (the key-up of the key that
        // completed the capture, say) pass straight through until the hook is gone.
        if (_ended || e.IsInjected)
        {
            return PassThrough;
        }

        // Escape cancels on the way down and is swallowed so it reaches neither the window nor
        // another program's hotkey. Its release falls through to the general path.
        if (!e.IsKeyUp && e.VirtualKeyCode == WindowsKeyCapture.VkEscape)
        {
            _ended = true;
            return new CaptureStep(CaptureDecision.Swallow, null, KeyModifiers.None, Ended: true);
        }

        if (e.IsKeyUp)
        {
            if (WindowsKeyCapture.IsModifier(e.VirtualKeyCode))
            {
                _heldModifiers.Remove(e.VirtualKeyCode);
            }

            _heldKeys.Remove(e.VirtualKeyCode);

            // Every key released with nothing still held ends the capture.
            if (_heldKeys.Count == 0)
            {
                _ended = true;
                return new CaptureStep(CaptureDecision.Swallow, null, KeyModifiers.None, Ended: true);
            }

            return Swallow();
        }

        if (WindowsKeyCapture.IsModifier(e.VirtualKeyCode))
        {
            _heldModifiers.Add(e.VirtualKeyCode);
            return Swallow();
        }

        var key = WindowsKeyCapture.ToGamepadKey(e.VirtualKeyCode);
        if (key is null)
        {
            // A physical key with no portable equivalent is swallowed so it cannot leak into the
            // focused window, but it is neither captured nor counted as held.
            return Swallow();
        }

        _heldKeys.Add(e.VirtualKeyCode);
        var modifiers = WindowsKeyCapture.ToModifiers(_heldModifiers);
        return new CaptureStep(CaptureDecision.Swallow, key, modifiers, Ended: false);
    }

    private static CaptureStep PassThrough => new(CaptureDecision.PassThrough, null, KeyModifiers.None, false);

    private static CaptureStep Swallow() => new(CaptureDecision.Swallow, null, KeyModifiers.None, false);
}
