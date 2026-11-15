using DeziroJoy.Platform.Windows.Gaming;
using Xunit;

namespace DeziroJoy.Platform.Tests.Windows;

public sealed class GameBarPopupGuardTests
{
    [Fact]
    public void Unsupported_Platform_Neither_Reads_Nor_Writes()
    {
        var calls = 0;
        var guard = new GameBarPopupGuard(
            () => false,
            () => { calls++; return 1; },
            () => calls++);

        Assert.False(guard.IsSupported);
        Assert.False(guard.IsControllerLaunchDisabled);
        Assert.Throws<PlatformNotSupportedException>(guard.DisableControllerLaunch);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void Controller_Launch_State_Reads_The_Current_User_Setting(int? value, bool disabled)
    {
        var guard = new GameBarPopupGuard(() => true, () => value, () => { });

        Assert.True(guard.IsSupported);
        Assert.Equal(disabled, guard.IsControllerLaunchDisabled);
    }

    [Fact]
    public void Disable_Controller_Launch_Writes_The_Dedicated_Setting()
    {
        var calls = 0;
        var guard = new GameBarPopupGuard(() => true, () => 1, () => calls++);

        guard.DisableControllerLaunch();

        Assert.Equal(1, calls);
    }
}
