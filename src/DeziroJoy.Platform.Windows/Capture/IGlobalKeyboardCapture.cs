using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Platform.Windows.Capture;

/// <summary>
/// A global low-level keyboard capture, armed only while the mapping editor is waiting for a key.
/// The App talks to this and never to the hook directly, so the editor wiring is identical whether
/// the real Windows hook or a test double sits behind it.
/// </summary>
public interface IGlobalKeyboardCapture : IDisposable
{
    /// <summary>
    /// Raised when a non-modifier key is pressed, carrying the key and the modifiers held with
    /// it. Fired on every such press, so the last key pressed before release wins.
    /// </summary>
    event Action<GamepadKey, KeyModifiers>? KeyCaptured;

    /// <summary>
    /// Raised once when the capture finishes - every key released, or Escape cancelled it - after
    /// which the hook is uninstalled. The subscriber treats it as "capture over".
    /// </summary>
    event Action? CaptureEnded;

    /// <summary>
    /// Arms the capture and installs the hook. Idempotent: arming an already-armed capture does
    /// nothing. When the hook cannot be installed the capture ends immediately rather than
    /// leaving the editor waiting for a key that will never arrive.
    /// </summary>
    void Arm();

    /// <summary>
    /// Disarms the capture and uninstalls the hook. Idempotent and safe to call from anywhere
    /// except synchronously from inside the hook's own callback - which is why the implementation
    /// defers its own unhook to the UI thread.
    /// </summary>
    void Disarm();
}
