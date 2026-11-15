using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Linux.Output;
using DeziroJoy.Platform.Linux.UInput;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// The failure that shipped was never "uinput would not open" - the open succeeded and the
/// first ioctl was rejected, which no suite without a real node could reach. The fd-adopting
/// seam drives that branch with any non-uinput descriptor: the ioctls fail with ENOTTY, the
/// same throw-and-name-the-request shape as the EINVAL the wrong request numbers caused.
/// </summary>
public class UInputOutputSinkTests
{
    [Fact]
    public void An_Ioctl_Failure_After_A_Successful_Open_Throws_Naming_The_Request()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the uinput P/Invoke only exists on Linux");

        var fd = UInputNative.Open("/dev/null", UInputNative.OWrOnly);
        Assert.True(fd >= 0, "/dev/null must open for this seam to mean anything");

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => new UInputOutputSink(fd, "DeziroJoy seam"));

            // UI_SET_EVBIT is the first request the sink issues, so the failure must carry
            // its number - the daemon journal line that made the shipped bug findable.
            Assert.Contains("0x40045564", exception.Message);
            Assert.Contains("errno", exception.Message);
        }
        finally
        {
            UInputNative.Close(fd);
        }
    }

    [Fact]
    public void A_Dropped_Event_Write_Throws_Naming_The_Errno()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the uinput P/Invoke only exists on Linux");

        // The write() return value used to be discarded, so a destroyed or unwritable
        // virtual device turned every injection into silence. A read-only descriptor fails
        // the write with EBADF - the same shape a destroyed device produces - without a real
        // uinput node and without injecting a keystroke into the live session.
        var fd = UInputNative.Open("/dev/null", EvdevNative.ORdOnly);
        Assert.True(fd >= 0, "/dev/null must open for this seam to mean anything");

        try
        {
            var sink = new UInputOutputSink(fd, "DeziroJoy seam", configure: false);
            var exception = Assert.Throws<InvalidOperationException>(() => sink.KeyDown(GamepadKey.A));

            Assert.Contains("errno", exception.Message);
            Assert.Contains("0x001E", exception.Message); // KEY_A, the event that was dropped
        }
        finally
        {
            UInputNative.Close(fd);
        }
    }
}
