using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Platform;

/// <summary>
/// Output sink for mapped actions. Implementations translate to SendInput (Windows)
/// or uinput (Linux). Every method is a single, immediately executed action.
/// </summary>
public interface IOutputSink : IDisposable
{
    void KeyDown(GamepadKey key);

    void KeyUp(GamepadKey key);

    void MouseMove(int dx, int dy);

    /// <summary>Wheel notches (1 notch = 120 units on Windows).</summary>
    void ScrollWheel(int horizontalNotches, int verticalNotches);

    void MouseButtonDown(MouseButton button);

    void MouseButtonUp(MouseButton button);
}
