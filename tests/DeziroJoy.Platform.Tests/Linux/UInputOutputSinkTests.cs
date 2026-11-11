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
}
