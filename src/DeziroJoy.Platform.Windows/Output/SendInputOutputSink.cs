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
/// <remarks>
/// SendInput is subject to UIPI: it may only inject into processes at an equal or lower
/// integrity level, and when UIPI blocks it the call fails without the error code saying
/// so. The return value and the marshalled GetLastError are therefore captured and
/// surfaced through <see cref="InjectionFailed"/>, <see cref="LastInjectionFailed"/> and
/// <see cref="LastErrorCode"/> rather than discarded - a swallowed failure is
/// indistinguishable from a working mapping.
/// </remarks>
public sealed class SendInputOutputSink : IOutputSink
{
    private readonly Lock _failureSync = new();

    private int _lastErrorCode;

    private bool _lastInjectionFailed;

    private int _failedInjectionCount;

    /// <summary>Raised whenever SendInput injects fewer events than were requested.</summary>
    public event EventHandler<SendInputFailureEventArgs>? InjectionFailed;

    /// <summary>
    /// Win32 error code from the last failed call; 0 when the last call succeeded or when
    /// the API did not set one (UIPI blocking sets none).
    /// </summary>
    public int LastErrorCode
    {
        get
        {
            lock (_failureSync)
            {
                return _lastErrorCode;
            }
        }
    }

    /// <summary>True when the most recent SendInput call did not inject every requested event.</summary>
    public bool LastInjectionFailed
    {
        get
        {
            lock (_failureSync)
            {
                return _lastInjectionFailed;
            }
        }
    }

    /// <summary>How many SendInput calls have failed since this sink was created.</summary>
    public int FailedInjectionCount
    {
        get
        {
            lock (_failureSync)
            {
                return _failedInjectionCount;
            }
        }
    }

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

    private void SendWheel(uint flags, int amount)
    {
        var input = new INPUT
        {
            Type = SendInputNative.InputMouse,
            Mouse = new MOUSEINPUT { DwFlags = flags, MouseData = (uint)amount },
        };

        Send(input);
    }

    private void SendKey(GamepadKey key, bool keyUp)
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

    private void Send(in INPUT input)
    {
        Span<INPUT> buffer = [input];
        var injected = SendInputNative.SendInput(1, buffer, Marshal.SizeOf<INPUT>());

        // GetLastError is only meaningful on failure and must be read before any other
        // interop call can overwrite the thread's last-error slot.
        var error = injected == 1 ? 0 : Marshal.GetLastWin32Error();

        ReportInjectionResult(injected, requested: 1, error);
    }

    /// <summary>
    /// Records the outcome of a SendInput call and raises <see cref="InjectionFailed"/>
    /// when events were dropped. Internal so the surfacing contract is testable without
    /// user32.dll being present.
    /// </summary>
    internal void ReportInjectionResult(uint injected, uint requested, int errorCode)
    {
        if (injected >= requested)
        {
            lock (_failureSync)
            {
                _lastErrorCode = 0;
                _lastInjectionFailed = false;
            }

            return;
        }

        EventHandler<SendInputFailureEventArgs>? handlers;
        var args = new SendInputFailureEventArgs(errorCode, injected, requested);

        lock (_failureSync)
        {
            _lastErrorCode = errorCode;
            _lastInjectionFailed = true;
            _failedInjectionCount++;
            handlers = InjectionFailed;
        }

        // Raised outside the lock so a handler may safely read back into the sink.
        handlers?.Invoke(this, args);
    }
}

/// <summary>Describes a SendInput call that did not inject every requested event.</summary>
public sealed class SendInputFailureEventArgs : EventArgs
{
    public SendInputFailureEventArgs(int errorCode, uint injectedCount, uint requestedCount)
    {
        ErrorCode = errorCode;
        InjectedCount = injectedCount;
        RequestedCount = requestedCount;
    }

    /// <summary>
    /// Marshalled GetLastError value; 0 when the API set none. UIPI blocking is one of
    /// the cases that sets none, so a zero here does not mean the call succeeded.
    /// </summary>
    public int ErrorCode { get; }

    /// <summary>How many events SendInput actually injected.</summary>
    public uint InjectedCount { get; }

    /// <summary>How many events were submitted.</summary>
    public uint RequestedCount { get; }
}
