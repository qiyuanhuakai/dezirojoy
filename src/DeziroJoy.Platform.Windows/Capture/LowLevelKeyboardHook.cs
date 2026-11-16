using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeziroJoy.Platform.Windows.Capture;

/// <summary>
/// The real <c>WH_KEYBOARD_LL</c> global hook. It is the only piece that touches Win32, and it is
/// deliberately thin: it turns each keyboard message into a <see cref="RawKeyEvent"/>, hands it to
/// the assigned <see cref="ILowLevelKeyboardHook.Handler"/> and returns the swallow decision, and
/// it keeps the callback delegate and hook handle alive for exactly as long as it is installed.
/// <para>
/// A low-level hook only fires while the thread that installed it pumps messages, so this must be
/// installed on - and therefore runs its callback on - the UI thread. The orchestrator guarantees
/// the install happens there and defers the unhook so it never runs from inside the callback.
/// </para>
/// <para>
/// A handler that throws must not wedge the keyboard, so the callback swallows nothing and passes
/// the event on; the orchestrator's own error path then ends the capture.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LowLevelKeyboardHook : ILowLevelKeyboardHook
{
    private const int WhKeyboardLl = 13;
    private const int HcAction = 0;
    private const uint LlkhfInjected = 0x00000010;

    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    // Keep the delegate referenced for the hook's lifetime: the runtime will not collect a
    // delegate that native code still holds, so a field - not a local - is what keeps it alive.
    private LowLevelKeyboardProc? _proc;
    private nint _hookHandle;
    private bool _installed;

    public bool IsInstalled => _installed;

    public Func<RawKeyEvent, bool>? Handler { get; set; }

    public bool Install()
    {
        if (_installed)
        {
            return true;
        }

        _proc = HookCallback;
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);

        if (_hookHandle == nint.Zero)
        {
            _proc = null;
            return false;
        }

        _installed = true;
        return true;
    }

    public void Uninstall()
    {
        if (!_installed)
        {
            return;
        }

        if (_hookHandle != nint.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = nint.Zero;
        }

        _proc = null;
        _installed = false;
    }

    public IReadOnlyCollection<ushort> ReadHeldModifiers()
    {
        var held = new List<ushort>();

        foreach (var vk in WindowsKeyCapture.ModifierKeys)
        {
            // High bit set means the key is down right now.
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                held.Add(vk);
            }
        }

        return held;
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode == HcAction && Handler is { } handler)
        {
            var message = (int)wParam;
            var isKeyUp = message is WmKeyUp or WmSysKeyUp;
            var isKeyDown = message is WmKeyDown or WmSysKeyDown;

            if (isKeyDown || isKeyUp)
            {
                var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                var injected = (data.flags & LlkhfInjected) != 0;
                var raw = new RawKeyEvent((ushort)data.vkCode, isKeyUp, injected);

                try
                {
                    if (handler(raw))
                    {
                        return 1;
                    }
                }
                catch
                {
                    // A throwing handler must never wedge the keyboard: pass this event on and
                    // let the orchestrator tear the capture down from a safe point.
                    return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
                }
            }
        }

        return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
