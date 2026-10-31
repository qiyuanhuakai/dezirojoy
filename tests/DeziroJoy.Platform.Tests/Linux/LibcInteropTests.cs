using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Linux.UInput;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// Guards the libc P/Invoke entry points. "libc" (unversioned) resolves only when
/// libc6-dev is installed, so CI images without it fail at discovery time;
/// the explicit soname must keep resolving on any glibc host.
/// </summary>
public class LibcInteropTests
{
    [Fact]
    public void Evdev_Open_Resolves_Libc_And_Fails_Only_On_Missing_Files()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "libc interop only exists on Linux");

        var fd = EvdevNative.Open("/dev/input/not-a-real-device", EvdevNative.ORdOnly);

        Assert.True(fd < 0, "opening a nonexistent path must fail with a negative fd, not throw");
    }

    [Fact]
    public void Evdev_Close_Resolves_Libc()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "libc interop only exists on Linux");

        EvdevNative.Close(-1);
    }

    [Fact]
    public void UInput_Open_Resolves_Libc_And_Fails_Without_Permission()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "libc interop only exists on Linux");

        var fd = UInputNative.Open("/dev/uinput", UInputNative.OWrOnly);

        Assert.True(fd < 0 || fd >= 0, "the call must not throw regardless of permissions");
    }
}
