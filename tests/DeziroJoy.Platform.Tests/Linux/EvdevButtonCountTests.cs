using DeziroJoy.Core.Input;
using DeziroJoy.Platform.Linux.Evdev;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// The app never asked the kernel how many buttons a pad has. EVIOCGBIT(EV_KEY) answers it:
/// a bitmask where bit N of byte N/8 is set when key code N exists on the device.
/// </summary>
public class EvdevButtonCountTests
{
    [Fact]
    public void EvIocGBitKey_Matches_The_Kernel_IOC_Formula()
    {
        // _IOC(dir, type, nr, size) = dir<<30 | type<<8 | nr | size<<16, with _IOC_READ = 2
        // and EVIOCGBIT(ev, len) = _IOC(_IOC_READ, 'E', 0x20 + ev, len). EV_KEY is 1 and the
        // buffer is 96 bytes (KEY_CNT 0x300 bits).
        const uint expected = (2u << 30) | ((uint)'E' << 8) | (0x20u + 1u) | (96u << 16);

        Assert.Equal(expected, EvdevNative.EvIocGBitKey);
    }

    [Fact]
    public void The_IOC_Formula_Reproduces_The_Already_Shipped_Abs_Constant()
    {
        // Guards the derivation itself: the same formula must regenerate the EVIOCGBIT(EV_ABS, 8)
        // constant that is already in production, so the EV_KEY one is derived, not guessed.
        const uint expected = (2u << 30) | ((uint)'E' << 8) | (0x20u + 3u) | (8u << 16);

        Assert.Equal(expected, EvdevNative.EvIocGBitAbs);
    }

    [Fact]
    public void KeyBitBufferSize_Covers_The_Whole_EvKey_Range()
    {
        // KEY_CNT is 0x300 (768) bits, so 96 bytes is the smallest whole-byte buffer that
        // covers every code the kernel can report, including BTN_TRIGGER_HAPPY40 at 0x2e7.
        Assert.Equal(96, EvdevNative.KeyBitBufferSize);
        Assert.True(EvdevNative.KeyBitBufferSize * 8 > EvdevCodes.BtnTriggerHappy40);
    }

    [Fact]
    public void CountSetBits_Counts_The_Set_Bits_In_An_EvKey_Bitmask()
    {
        var bits = new byte[EvdevNative.KeyBitBufferSize];

        SetBit(bits, EvdevCodes.BtnSouth);
        SetBit(bits, EvdevCodes.BtnEast);
        SetBit(bits, EvdevCodes.BtnNorth);
        SetBit(bits, EvdevCodes.BtnWest);
        SetBit(bits, EvdevCodes.BtnTl);
        SetBit(bits, EvdevCodes.BtnTr);
        SetBit(bits, EvdevCodes.BtnTl2);
        SetBit(bits, EvdevCodes.BtnTr2);
        SetBit(bits, EvdevCodes.BtnSelect);
        SetBit(bits, EvdevCodes.BtnStart);
        SetBit(bits, EvdevCodes.BtnMode);
        SetBit(bits, EvdevCodes.BtnThumbL);
        SetBit(bits, EvdevCodes.BtnThumbR);
        SetBit(bits, EvdevCodes.BtnDpadUp);
        SetBit(bits, EvdevCodes.BtnDpadDown);
        SetBit(bits, EvdevCodes.BtnDpadLeft);
        SetBit(bits, EvdevCodes.BtnDpadRight);
        SetBit(bits, EvdevCodes.BtnTriggerHappy40);

        Assert.Equal(18, EvdevGamepadProvider.CountSetBits(bits));
    }

    [Fact]
    public void CountSetBits_Returns_Zero_For_An_Empty_Bitmask()
    {
        Assert.Equal(0, EvdevGamepadProvider.CountSetBits(new byte[EvdevNative.KeyBitBufferSize]));
    }

    [Fact]
    public void CountSetBits_Counts_Bits_In_The_Last_Byte_Of_The_Buffer()
    {
        var bits = new byte[EvdevNative.KeyBitBufferSize];

        SetBit(bits, EvdevCodes.BtnTriggerHappy40);

        Assert.Equal(1, EvdevGamepadProvider.CountSetBits(bits));
    }

    [Fact]
    public void Reading_The_Button_Count_From_A_Non_Evdev_Fd_Reports_Unknown()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "evdev ioctls only exist on Linux");

        var fd = EvdevNative.Open("/dev/null", EvdevNative.ORdOnly);
        Assert.True(fd >= 0, "/dev/null must open for reading");

        try
        {
            Assert.Null(EvdevGamepadProvider.ReadButtonCount(fd));
        }
        finally
        {
            EvdevNative.Close(fd);
        }
    }

    [Fact]
    public void ButtonCount_Defaults_To_Null_Meaning_Unknown()
    {
        // XInput's 14-button set is fixed and closed with no raw-code channel, so the Windows
        // provider leaves ButtonCount at its default: null == "unknown", never a guess.
        var descriptor = new GamepadDescriptor("xinput:0", "Xbox 兼容手柄 #1");

        Assert.Null(descriptor.ButtonCount);
    }

    [Fact]
    public void ButtonCount_Can_Be_Set_On_A_Descriptor()
    {
        var descriptor = new GamepadDescriptor("/dev/input/event3", "Generic X-Box pad") { ButtonCount = 18 };

        Assert.Equal(18, descriptor.ButtonCount);
    }

    private static void SetBit(byte[] bits, ushort code)
    {
        bits[code / 8] |= (byte)(1 << (code % 8));
    }
}
