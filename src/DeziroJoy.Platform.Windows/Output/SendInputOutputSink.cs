using System.Diagnostics;
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
    /// <summary>
    /// KEYEVENTF_UNICODE. Declared here rather than beside the other event flags because
    /// text injection is this sink's alone: it is the one flag whose wScan is a UTF-16
    /// code unit instead of a scancode, so a key press never needs it.
    /// </summary>
    private const uint KeyEventFUnicode = 0x0004;

    private readonly Lock _failureSync = new();

    private int _lastErrorCode;

    private bool _lastInjectionFailed;

    private int _failedInjectionCount;

    /// <summary>Raised whenever SendInput injects fewer events than were requested.</summary>
    public event EventHandler<SendInputFailureEventArgs>? InjectionFailed;

    /// <summary>
    /// Raised after every injection - success or failure - carrying a lightweight record of what
    /// was sent. This is the always-on diagnostic feed the <c>InjectionLog</c> subscribes to; it
    /// fires <em>after</em> the native call, so a subscriber can never alter the injected stream.
    /// A custom struct delegate keeps the per-event cost allocation-free.
    /// </summary>
    public event InjectionEmittedHandler? InjectionEmitted;

    /// <summary>
    /// Test seam. When set, replaces the user32 SendInput call so tests can capture the exact
    /// INPUT bytes without the native library (which is absent on Linux). Null in production, so
    /// the real call runs unchanged.
    /// </summary>
    internal Func<INPUT, uint>? TransmitOverride;

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

    /// <summary>
    /// Types the string one UTF-16 code unit at a time with KEYEVENTF_UNICODE, so a
    /// character the local layout cannot name - a Greek letter, an emoji's surrogate pair -
    /// still reaches the foreground window. A surrogate pair travels as its two code units,
    /// exactly as a real keyboard's WM_CHAR stream delivers it. Each unit is its own
    /// injection so a blocked one is reported rather than silently dropping the tail.
    /// </summary>
    public void SendText(string text)
    {
        foreach (var unit in text)
        {
            var input = new INPUT
            {
                Type = SendInputNative.InputKeyboard,

                // WVk stays 0: the event names no virtual key, only the character wScan
                // carries, so nothing here can collide with a mapped key's press.
                Keyboard = new KEYBDINPUT { WVk = 0, WScan = unit, DwFlags = KeyEventFUnicode },
            };

            Send(input, InjectionAction.Text, unit, 0);
        }
    }

    public void MouseMove(int dx, int dy)
    {
        var input = new INPUT
        {
            Type = SendInputNative.InputMouse,
            Mouse = new MOUSEINPUT { Dx = dx, Dy = dy, DwFlags = SendInputNative.MouseEventMove },
        };

        Send(input, InjectionAction.MouseMove, dx, dy);
    }

    public void ScrollWheel(int horizontalNotches, int verticalNotches)
    {
        if (verticalNotches != 0)
        {
            SendWheel(SendInputNative.MouseEventWheel, verticalNotches * SendInputNative.WheelDelta, verticalNotches, 0);
        }

        if (horizontalNotches != 0)
        {
            SendWheel(SendInputNative.MouseEventHWheel, horizontalNotches * SendInputNative.WheelDelta, 0, horizontalNotches);
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

        Send(input, InjectionAction.MouseButtonDown, (int)button, 0);
    }

    public void MouseButtonUp(MouseButton button)
    {
        var (flags, data) = MouseButtonFlags(button, down: false);
        var input = new INPUT
        {
            Type = SendInputNative.InputMouse,
            Mouse = new MOUSEINPUT { DwFlags = flags, MouseData = data },
        };

        Send(input, InjectionAction.MouseButtonUp, (int)button, 0);
    }

    public void Dispose()
    {
    }

    private void SendWheel(uint flags, int amount, int verticalNotches, int horizontalNotches)
    {
        var input = new INPUT
        {
            Type = SendInputNative.InputMouse,
            Mouse = new MOUSEINPUT { DwFlags = flags, MouseData = (uint)amount },
        };

        Send(input, InjectionAction.MouseWheel, verticalNotches, horizontalNotches);
    }

    private void SendKey(GamepadKey key, bool keyUp)
    {
        var virtualKey = WindowsVirtualKeys.ToVirtualKey(key);
        if (virtualKey == WindowsVirtualKeys.Unmapped)
        {
            return;
        }

        // MapVirtualKeyW lives in user32.dll; guard it so the sink stays loadable (and testable)
        // off Windows, where this sink is never used in production anyway.
        var scanCode = OperatingSystem.IsWindows()
            ? (ushort)SendInputNative.MapVirtualKeyW(virtualKey, SendInputNative.MapVirtualKeyVkToScanCode)
            : (ushort)0;
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

        Send(input, keyUp ? InjectionAction.KeyUp : InjectionAction.KeyDown, (int)key, 0);
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

    private void Send(in INPUT input, InjectionAction action, int primary, int secondary)
    {
        var injected = Transmit(in input);

        // GetLastError is only meaningful on failure and must be read before any other
        // interop call can overwrite the thread's last-error slot.
        var error = injected == 1 ? 0 : Marshal.GetLastWin32Error();

        ReportInjectionResult(injected, requested: 1, error);
        RecordInjection(action, primary, secondary, injected >= 1);
    }

    private uint Transmit(in INPUT input)
    {
        if (TransmitOverride is not null)
        {
            return TransmitOverride(input);
        }

        Span<INPUT> buffer = [input];
        return SendInputNative.SendInput(1, buffer, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Fires <see cref="InjectionEmitted"/> with a monotonic timestamp. Runs strictly after the
   /// native call, so it is a pure observer: it can never change what was injected. The timestamp
    /// is captured here for the diagnostic only and is never fed back into the mapping path.
    /// </summary>
    private void RecordInjection(InjectionAction action, int primary, int secondary, bool success)
    {
        var handler = InjectionEmitted;
        if (handler is null)
        {
            return;
        }

        handler(new InjectionRecord(Stopwatch.GetTimestamp(), action, primary, secondary, success));
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

/// <summary>The kind of action a single injection performed.</summary>
public enum InjectionAction : byte
{
    KeyDown,
    KeyUp,
    Text,
    MouseMove,
    MouseWheel,
    MouseButtonDown,
    MouseButtonUp,
}

/// <summary>
/// One recorded injection. A value type so the always-on log can keep a preallocated ring buffer
/// of structs with no per-event heap allocation. <see cref="Primary"/> and <see cref="Secondary"/>
/// carry the action's operands: the <see cref="DeziroJoy.Core.Mapping.GamepadKey"/> or
/// <see cref="DeziroJoy.Core.Mapping.MouseButton"/> for keys/buttons, dx/dy for a move,
/// vertical/horizontal notches for the wheel, and the UTF-16 code unit for one unit of text.
/// </summary>
public readonly record struct InjectionRecord(
    long Timestamp,
    InjectionAction Action,
    int Primary,
    int Secondary,
    bool Success);

/// <summary>
/// Handler for <see cref="SendInputOutputSink.InjectionEmitted"/>. Takes the record by value so
/// raising the event allocates nothing on the hot path.
/// </summary>
public delegate void InjectionEmittedHandler(InjectionRecord record);
