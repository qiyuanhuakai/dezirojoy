using DeziroJoy.Platform.Linux.UInput;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// Pins the uinput ioctl request numbers to the kernel uapi encoding. These are plain
/// constants, so nothing here opens /dev/uinput or talks to a kernel - which is exactly
/// how the wrong SET_*BIT numbers shipped: the wrong request only fails once a real
/// device answers the ioctl, and no suite without one ever reached that branch.
/// </summary>
public class UInputNativeTests
{
    [Fact]
    public void SetBit_Ioctls_Match_The_Kernel_Uapi_Numbers()
    {
        // linux/uinput.h: UI_SET_EVBIT/KEYBIT/RELBIT are _IOW('U', 100..102, int), i.e.
        // direction _IOC_WRITE | size sizeof(int) | type 'U' (0x55) | nr 100..102.
        Assert.Equal(0x40045564u, UInputNative.UiSetEvBit);
        Assert.Equal(0x40045565u, UInputNative.UiSetKeyBit);
        Assert.Equal(0x40045566u, UInputNative.UiSetRelBit);
    }

    [Fact]
    public void Device_Ioctls_Stay_Verified_Correct()
    {
        // UI_DEV_SETUP is _IOW('U', 3, struct uinput_setup); CREATE/DESTROY are _IO('U', 1..2).
        Assert.Equal(0x405C5503u, UInputNative.UiDevSetup);
        Assert.Equal(0x5501u, UInputNative.UiDevCreate);
        Assert.Equal(0x5502u, UInputNative.UiDevDestroy);
    }
}
