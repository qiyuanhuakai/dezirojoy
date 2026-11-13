using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Linux.Keys;
using DeziroJoy.Platform.Linux.Output;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// The uinput text path. The kernel device only speaks key codes - there is no Unicode
/// event to send - so a character is typed the way a physical keyboard types it: the key
/// its US layout names, with Shift for the shifted symbol. These tests pin the stroke list
/// itself, which is pure and needs no /dev/uinput.
/// </summary>
public class UInputOutputSinkTextTests
{
    [Fact]
    public void A_Letter_Is_One_Down_Up_Pair()
    {
        Assert.Equal(
            [(GamepadKey.A, 1), (GamepadKey.A, 0)],
            EvdevTextKeys.StrokesFor("a"));
    }

    [Fact]
    public void A_Capital_Letter_Holds_Shift_Around_It()
    {
        Assert.Equal(
            [
                (GamepadKey.LeftShift, 1),
                (GamepadKey.B, 1),
                (GamepadKey.B, 0),
                (GamepadKey.LeftShift, 0),
            ],
            EvdevTextKeys.StrokesFor("B"));
    }

    [Fact]
    public void A_Shifted_Symbol_Comes_From_The_Digit_Row()
    {
        Assert.Equal(
            [
                (GamepadKey.LeftShift, 1),
                (GamepadKey.D1, 1),
                (GamepadKey.D1, 0),
                (GamepadKey.LeftShift, 0),
            ],
            EvdevTextKeys.StrokesFor("!"));
    }

    [Fact]
    public void Space_And_Enter_Are_Their_Own_Keys()
    {
        Assert.Equal(
            [(GamepadKey.Space, 1), (GamepadKey.Space, 0)],
            EvdevTextKeys.StrokesFor(" "));
        Assert.Equal(
            [(GamepadKey.Enter, 1), (GamepadKey.Enter, 0)],
            EvdevTextKeys.StrokesFor("\n"));
    }

    [Fact]
    public void A_Character_No_Key_Code_Names_Is_Skipped_Rather_Than_Garbled()
    {
        // The honest boundary of a text path built from key codes: 世 has no key, so it is
        // dropped and its neighbours still type.
        Assert.Equal(
            [
                (GamepadKey.A, 1),
                (GamepadKey.A, 0),
                (GamepadKey.B, 1),
                (GamepadKey.B, 0),
            ],
            EvdevTextKeys.StrokesFor("a世b"));
    }

    [Fact]
    public void Every_Stroke_Names_A_Key_The_Evdev_Map_Carries()
    {
        foreach (var text in new[] { "abcXYZ 0123456789", "!@#$%^&*()_+-=[]{}|;:'\",.<>/?" })
        {
            foreach (var (key, _) in EvdevTextKeys.StrokesFor(text))
            {
                Assert.NotEqual(LinuxKeyCodes.Unmapped, LinuxKeyCodes.ToEvdevCode(key));
            }
        }
    }
}
