using System.Runtime.InteropServices;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;
using DeziroJoy.Platform.Windows.Keys;
using DeziroJoy.Platform.Windows.SendInput;

namespace DeziroJoy.Platform.Windows.Output;

/// <summary>
/// Windows output sink built on SendInput. Keys are sent as virtual keys with the
/// matching scancode filled in (games that read scancodes keep working); the mouse
/// uses relative movement and wheel deltas in 120-unit notches.
/// </summary>
public sealed class SendInputOutputSink : IOutputSink
{
    public void KeyDown(GamepadKey key) => SendKey(key, keyUp: false);

    public void KeyUp(GamepadKey key) => SendKey(key, keyUp: true);

    public void MouseMove(int dx, int dy)
    {
        var input = new INPUT
        {
            Type = SendInputNative.InputMouse,
            Mouse = new MOUSEINPUT { Dx = dx, Dy = dy, DwFlags = SendInputNative.MouseEventMove },
        };

        Send(input);
    }

    public void ScrollWheel(int horizontalNotches, int verticalNotches)
    {
        if (verticalNotches != 0)
        {
            SendWheel(SendInputNative.MouseEventWheel, verticalNotches * SendInputNative.WheelDelta);
        }

        if (horizontalNotches != 0)
        {
            SendWheel(SendInputNative.MouseEventHWheel, horizontalNotches * SendInputNative.WheelDelta);
        }
    }

    public void MouseButtonDown(MouseButton button)
    {
        var (flags, data) = MouseButtonFlags(button, down: true);
        var input = new INPUT
        {
            Type = SendInputNative.InputMouse,
            Mouse = new MOUSEINPUT { DwFlags = flags, MouseData = data },
        };

        Send(input);
    }

    public void MouseButtonUp(MouseButton button)
    {
        var (flags, data) = MouseButtonFlags(button, down: false);
        var input = new INPUT
        {
            Type = SendInputNative.InputMouse,
            Mouse = new MOUSEINPUT { DwFlags = flags, MouseData = data },
        };

        Send(input);
    }

    public void Dispose()
    {
    }

    private static void SendWheel(uint flags, int amount)
    {
        var input = new INPUT
        {
            Type = SendInputNative.InputMouse,
            Mouse = new MOUSEINPUT { DwFlags = flags, MouseData = (uint)amount },
        };

        Send(input);
    }

    private static void SendKey(GamepadKey key, bool keyUp)
    {
        var virtualKey = WindowsVirtualKeys.ToVirtualKey(key);
        if (virtualKey == WindowsVirtualKeys.Unmapped)
        {
            return;
        }

        var scanCode = (ushort)SendInputNative.MapVirtualKeyW(virtualKey, SendInputNative.MapVirtualKeyVkToScanCode);
        var flags = keyUp ? SendInputNative.KeyEventFKeyUp : 0;

        if (WindowsVirtualKeys.IsExtendedKey(key))
        {
            flags |= SendInputNative.KeyEventFExtendedKey;
        }

        var input = new INPUT
        {
            Type = SendInputNative.InputKeyboard,
            Keyboard = new KEYBDINPUT { WVk = virtualKey, WScan = scanCode, DwFlags = flags },
        };

        Send(input);
    }

    private static (uint Flags, uint Data) MouseButtonFlags(MouseButton button, bool down) => button switch
    {
        MouseButton.Left => (down ? SendInputNative.MouseEventLeftDown : SendInputNative.MouseEventLeftUp, 0u),
        MouseButton.Right => (down ? SendInputNative.MouseEventRightDown : SendInputNative.MouseEventRightUp, 0u),
        MouseButton.Middle => (down ? SendInputNative.MouseEventMiddleDown : SendInputNative.MouseEventMiddleUp, 0u),
        MouseButton.XButton1 => (down ? SendInputNative.MouseEventXDown : SendInputNative.MouseEventXUp, SendInputNative.XButton1),
        MouseButton.XButton2 => (down ? SendInputNative.MouseEventXDown : SendInputNative.MouseEventXUp, SendInputNative.XButton2),
        _ => (0u, 0u),
    };

    private static void Send(in INPUT input)
    {
        Span<INPUT> buffer = [input];
        SendInputNative.SendInput(1, buffer, Marshal.SizeOf<INPUT>());
    }
}
