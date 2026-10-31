using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;

namespace DeziroJoy.App.Services;

/// <summary>
/// No-op sink used when the platform cannot create a real output device (e.g. Linux
/// without /dev/uinput access). The UI stays fully usable for editing and previewing.
/// </summary>
public sealed class NullOutputSink : IOutputSink
{
    public void KeyDown(GamepadKey key)
    {
    }

    public void KeyUp(GamepadKey key)
    {
    }

    public void MouseMove(int dx, int dy)
    {
    }

    public void ScrollWheel(int horizontalNotches, int verticalNotches)
    {
    }

    public void MouseButtonDown(MouseButton button)
    {
    }

    public void MouseButtonUp(MouseButton button)
    {
    }

    public void Dispose()
    {
    }
}
