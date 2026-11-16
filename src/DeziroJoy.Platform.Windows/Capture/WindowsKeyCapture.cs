using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Platform.Windows.Capture;

/// <summary>
/// One raw low-level keyboard event, already lifted out of the native
/// <c>KBDLLHOOKSTRUCT</c>. This is the seam the whole capture path speaks: the native hook
/// turns a Windows message into one of these, and everything downstream - the pure session, the
/// orchestrator and its tests - works only with this value, so none of it needs a real keyboard.
/// </summary>
/// <param name="VirtualKeyCode">The Windows virtual-key code, layout-independent.</param>
/// <param name="IsKeyUp">True for a release, false for a press.</param>
/// <param name="IsInjected">
/// True when the event was synthesised (SendInput/keybd_event) rather than typed. Injected
/// events are passed through untouched so the engine's own output can never be swallowed by a
/// capture that happens to be armed at the same time.
/// </param>
public readonly record struct RawKeyEvent(ushort VirtualKeyCode, bool IsKeyUp, bool IsInjected);

/// <summary>
/// Translates Windows virtual-key codes onto the portable <see cref="GamepadKey"/> vocabulary and
/// reads the modifier state a low-level hook has to track by hand. Deliberately free of any
/// native call: it is a pure table over integers, which is what lets the capture be tested
/// wherever the suite runs. The output direction (<see cref="Keys.WindowsVirtualKeys"/>) maps the
/// other way; this is the input half, and the two round-trip on purpose.
/// </summary>
public static class WindowsKeyCapture
{
    // Modifier virtual keys, including both the left/right-specific codes the OS reports for a
    // physical press and the generic VK_SHIFT/VK_CONTROL/VK_MENU codes that can appear instead.
    public const ushort VkShift = 0x10;

    public const ushort VkLShift = 0xA0;

    public const ushort VkRShift = 0xA1;

    public const ushort VkControl = 0x11;

    public const ushort VkLControl = 0xA2;

    public const ushort VkRControl = 0xA3;

    public const ushort VkMenu = 0x12;

    public const ushort VkLMenu = 0xA4;

    public const ushort VkRMenu = 0xA5;

    public const ushort VkLWin = 0x5B;

    public const ushort VkRWin = 0x5C;

    public const ushort VkEscape = 0x1B;

    /// <summary>Every modifier virtual key, used to seed the held set from the live key state.</summary>
    public static readonly ushort[] ModifierKeys =
    [
        VkShift, VkLShift, VkRShift,
        VkControl, VkLControl, VkRControl,
        VkMenu, VkLMenu, VkRMenu,
        VkLWin, VkRWin,
    ];

    public static bool IsModifier(ushort vk) => vk switch
    {
        VkShift or VkLShift or VkRShift
            or VkControl or VkLControl or VkRControl
            or VkMenu or VkLMenu or VkRMenu
            or VkLWin or VkRWin => true,
        _ => false,
    };

    /// <summary>
    /// The portable key a virtual-key code stands for, or null when the code has no portable
    /// equivalent. Modifiers are mapped too - to their canonical left-hand member - so a
    /// round-trip through <see cref="Keys.WindowsVirtualKeys.ToVirtualKey"/> holds for every key,
    /// even though the session never turns a modifier into a capture target.
    /// </summary>
    public static GamepadKey? ToGamepadKey(ushort vk) => vk switch
    {
        >= 0x41 and <= 0x5A => GamepadKey.A + (vk - 0x41),
        >= 0x30 and <= 0x39 => GamepadKey.D0 + (vk - 0x30),
        >= 0x70 and <= 0x87 => GamepadKey.F1 + (vk - 0x70),
        >= 0x60 and <= 0x69 => GamepadKey.Numpad0 + (vk - 0x60),
        VkEscape => GamepadKey.Escape,
        0x0D => GamepadKey.Enter,
        0x20 => GamepadKey.Space,
        0x09 => GamepadKey.Tab,
        0x08 => GamepadKey.Backspace,
        0x2E => GamepadKey.Delete,
        0x2D => GamepadKey.Insert,
        0x24 => GamepadKey.Home,
        0x23 => GamepadKey.End,
        0x21 => GamepadKey.PageUp,
        0x22 => GamepadKey.PageDown,
        0x26 => GamepadKey.ArrowUp,
        0x28 => GamepadKey.ArrowDown,
        0x25 => GamepadKey.ArrowLeft,
        0x27 => GamepadKey.ArrowRight,
        VkShift or VkLShift or VkRShift => GamepadKey.LeftShift,
        VkControl or VkLControl or VkRControl => GamepadKey.LeftCtrl,
        VkMenu or VkLMenu or VkRMenu => GamepadKey.LeftAlt,
        VkLWin or VkRWin => GamepadKey.LeftWin,
        0xBD => GamepadKey.Minus,
        0xBB => GamepadKey.Equal,
        0xBC => GamepadKey.Comma,
        0xBE => GamepadKey.Period,
        0xBF => GamepadKey.Slash,
        0xBA => GamepadKey.Semicolon,
        0xDE => GamepadKey.Apostrophe,
        0xDB => GamepadKey.BracketLeft,
        0xDD => GamepadKey.BracketRight,
        0xDC => GamepadKey.Backslash,
        0xC0 => GamepadKey.Grave,

        // 媒体键: the VK_MEDIA_* / VK_VOLUME_* family.
        0xAF => GamepadKey.VolumeUp,
        0xAE => GamepadKey.VolumeDown,
        0xAD => GamepadKey.VolumeMute,
        0xB3 => GamepadKey.MediaPlayPause,
        0xB2 => GamepadKey.MediaStop,
        0xB1 => GamepadKey.MediaPrevTrack,
        0xB0 => GamepadKey.MediaNextTrack,

        // 系统与浏览器功能键: the VK_BROWSER_* / VK_LAUNCH_* family plus sleep and print-screen.
        0xA6 => GamepadKey.BrowserBack,
        0xA7 => GamepadKey.BrowserForward,
        0xA8 => GamepadKey.BrowserRefresh,
        0xA9 => GamepadKey.BrowserStop,
        0xAA => GamepadKey.BrowserSearch,
        0xAB => GamepadKey.BrowserFavorites,
        0xAC => GamepadKey.BrowserHome,
        0xB4 => GamepadKey.LaunchMail,
        0xB6 => GamepadKey.LaunchApp1,
        0xB7 => GamepadKey.LaunchApp2,
        0x5F => GamepadKey.Sleep,
        0x2C => GamepadKey.PrintScreen,
        _ => null,
    };

    /// <summary>
    /// The modifier flags implied by the set of modifier keys currently held. Either side of a
    /// modifier counts, and so does the generic code, because the OS may report any of them for a
    /// single physical key.
    /// </summary>
    public static KeyModifiers ToModifiers(IEnumerable<ushort> heldModifierKeys)
    {
        var result = KeyModifiers.None;

        foreach (var vk in heldModifierKeys)
        {
            result |= Classify(vk);
        }

        return result;
    }

    private static KeyModifiers Classify(ushort vk) => vk switch
    {
        VkShift or VkLShift or VkRShift => KeyModifiers.Shift,
        VkControl or VkLControl or VkRControl => KeyModifiers.Ctrl,
        VkMenu or VkLMenu or VkRMenu => KeyModifiers.Alt,
        VkLWin or VkRWin => KeyModifiers.Win,
        _ => KeyModifiers.None,
    };
}
