using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Linux.Keys;
using DeziroJoy.Platform.Linux.Output;
using DeziroJoy.Platform.Linux.UInput;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// The virtual device's capability declarations, pinned as data. The declaration gap found
/// here was on the mouse half: the BTN_LEFT..BTN_EXTRA codes were emitted without ever being
/// declared, so udev never tagged the device ID_INPUT_MOUSE and X11's pointer catchall never
/// matched it. The keyboard half was verified live (real Xorg, real uinput device): the key
/// bits alone already attach a KEYBOARD device and deliver key events under both the libinput
/// and the evdev driver, so a missing bit is only invisible until a real device answers the
/// ioctls - which is why the plan is pure and pinned here.
/// </summary>
public class UInputDevicePlanTests
{
    private static (uint Request, ushort Code)[] Plan() => [.. UInputDevicePlan.Build()];

    private static ushort[] DeclaredKeyCodes()
        => [.. Plan().Where(d => d.Request == UInputNative.UiSetKeyBit).Select(d => d.Code)];

    [Fact]
    public void The_Plan_Enables_Both_Event_Types_The_Sink_Emits()
    {
        var evBits = Plan().Where(d => d.Request == UInputNative.UiSetEvBit).Select(d => d.Code);

        Assert.Contains(EvdevCodes.EvKey, evBits);
        Assert.Contains(EvdevCodes.EvRel, evBits);
    }

    [Fact]
    public void Every_Key_Code_The_Sink_Emits_Is_Declared()
    {
        var declared = DeclaredKeyCodes();

        foreach (var key in Enum.GetValues<GamepadKey>())
        {
            var code = LinuxKeyCodes.ToEvdevCode(key);
            if (code == LinuxKeyCodes.Unmapped)
            {
                continue;
            }

            Assert.Contains(code, declared);
        }
    }

    [Fact]
    public void Every_Mouse_Button_Code_The_Sink_Emits_Is_Declared()
    {
        // MouseButtonDown emits EV_KEY/BTN_LEFT & friends. Emitting a code the device never
        // declared is what left the mouse half of the virtual device undeclared: the kernel
        // does not validate event codes, so nothing failed - the codes simply reached no X
        // device, because udev and the X server classify from the declared bits alone.
        var declared = DeclaredKeyCodes();

        foreach (var button in Enum.GetValues<MouseButton>())
        {
            Assert.True(EvdevMouseButtons.TryGetButtonCode(button, out var code));
            Assert.Contains(code, declared);
        }
    }

    [Fact]
    public void Btn_Left_Is_Declared_So_Udev_Tags_The_Device_A_Mouse()
    {
        // udev's input_id sets ID_INPUT_KEYBOARD from the key bits but only sets
        // ID_INPUT_MOUSE when a BTN_MOUSE..BTN_TASK code is declared, and the X server's
        // stock pointer catchall (MatchIsPointer in 40-libinput.conf / 10-evdev.conf)
        // matches on that property alone. Verified live against a real uinput device and a
        // real Xorg: without this bit only the keyboard catchalls match, the device reaches
        // X11 as a keyboard (its wheel rides along only where libinput promotes a keyboard
        // that declares REL_WHEEL), and the evdev driver logs "Forcing buttons for scroll
        // wheel(s)" instead of a real button class. With it, ID_INPUT_MOUSE is set and all
        // four catchalls match. This is the mouse half of the declaration gap - the
        // keyboard half attaches and receives key events either way.
        Assert.Contains(EvdevCodes.BtnLeft, DeclaredKeyCodes());
    }

    [Fact]
    public void The_Motion_And_Wheel_Axes_Are_Declared()
    {
        var relBits = Plan().Where(d => d.Request == UInputNative.UiSetRelBit).Select(d => d.Code);

        Assert.Contains((ushort)0x00, relBits); // REL_X
        Assert.Contains((ushort)0x01, relBits); // REL_Y
        Assert.Contains((ushort)0x06, relBits); // REL_HWHEEL
        Assert.Contains((ushort)0x08, relBits); // REL_WHEEL
    }

    [Fact]
    public void The_Plan_Sets_Bits_Only_So_Every_Bit_Lands_Before_Create()
    {
        // UI_DEV_SETUP needs the device name and UI_DEV_CREATE ends the setup phase - the
        // kernel rejects bit-setting after CREATE with EINVAL - so the plan is bit-only and
        // the sink applies it before the two device ioctls.
        var bitRequests = new[]
        {
            UInputNative.UiSetEvBit,
            UInputNative.UiSetKeyBit,
            UInputNative.UiSetRelBit,
        };

        Assert.All(Plan(), declaration => Assert.Contains(declaration.Request, bitRequests));
    }
}
