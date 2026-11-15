using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Linux.Keys;
using DeziroJoy.Platform.Linux.UInput;

namespace DeziroJoy.Platform.Linux.Output;

/// <summary>
/// The capability declarations the virtual device is created with, as pure data: one
/// <c>UI_SET_*BIT</c> ioctl per entry, in the order the kernel requires (every bit before
/// <c>UI_DEV_SETUP</c>/<c>UI_DEV_CREATE</c> - after CREATE the kernel rejects bit-setting
/// with EINVAL). Extracted from the sink so a suite with no /dev/uinput node can still pin
/// what the device claims it can emit: a missing bit stays invisible until a real device
/// answers the ioctls.
/// <para>
/// The gap this extraction found is on the mouse half: the BTN_LEFT..BTN_EXTRA codes were
/// emitted but never declared, so udev never tagged the device ID_INPUT_MOUSE and X11's
/// pointer catchall never matched it. The keyboard half was checked against a live uinput
/// device and a live Xorg and attaches and receives key events on the key bits alone, so
/// nothing here should be read as a diagnosed keyboard failure - see the plan tests for
/// what each declaration is pinned to.
/// </para>
/// </summary>
internal static class UInputDevicePlan
{
    // linux/input.h REL_* codes, named here rather than in EvdevCodes because the relative
    // range is the output side's own vocabulary - what the virtual mouse emits. Internal so
    // the sink's emit sites name the same constants the declarations below use: a wheel axis
    // emitted under one number and declared under another is the same class of bug as the
    // undeclared mouse buttons, just quieter.
    internal const ushort RelX = 0x00;

    internal const ushort RelY = 0x01;

    internal const ushort RelHWheel = 0x06;

    internal const ushort RelWheel = 0x08;

    /// <summary>
    /// Every declaration in application order: the two event types, the key codes, the
    /// mouse-button codes, the relative axes.
    /// </summary>
    public static IReadOnlyList<(uint Request, ushort Code)> Build()
    {
        var declarations = new List<(uint Request, ushort Code)>
        {
            (UInputNative.UiSetEvBit, EvdevCodes.EvKey),
            (UInputNative.UiSetEvBit, EvdevCodes.EvRel),
        };

        foreach (var key in Enum.GetValues<GamepadKey>())
        {
            var code = LinuxKeyCodes.ToEvdevCode(key);
            if (code != LinuxKeyCodes.Unmapped)
            {
                declarations.Add((UInputNative.UiSetKeyBit, code));
            }
        }

        // BTN_LEFT and friends share the EV_KEY bitmask with the letter keys, and declaring
        // them is what makes the rest of the stack treat this device as a mouse as well as
        // a keyboard: udev's input_id sets ID_INPUT_KEYBOARD from the key bits but only
        // sets ID_INPUT_MOUSE when a BTN_MOUSE..BTN_TASK code is declared, and the X
        // server's stock pointer catchall (40-libinput.conf / 10-evdev.conf) matches on
        // that property alone. A device that declares only keys and relative axes reaches
        // X11 as a keyboard - its wheel rides along only where libinput promotes a keyboard
        // declaring REL_WHEEL, and the classic evdev driver logs "Forcing buttons for
        // scroll wheel(s)" because no BTN_* bit gave it a real button class. Driven off
        // EvdevMouseButtons so the declaration can never drift from the codes
        // MouseButtonDown actually emits.
        foreach (var button in Enum.GetValues<MouseButton>())
        {
            if (EvdevMouseButtons.TryGetButtonCode(button, out var code))
            {
                declarations.Add((UInputNative.UiSetKeyBit, code));
            }
        }

        declarations.Add((UInputNative.UiSetRelBit, RelX));
        declarations.Add((UInputNative.UiSetRelBit, RelY));
        declarations.Add((UInputNative.UiSetRelBit, RelHWheel));
        declarations.Add((UInputNative.UiSetRelBit, RelWheel));

        return declarations;
    }
}
