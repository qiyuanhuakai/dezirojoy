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
    public void Disable_Controller_Launch_Writes_And_Verifies_The_Dedicated_Setting()
    {
        var value = 1;
        var calls = 0;
        var guard = new GameBarPopupGuard(() => true, () => value, () => { calls++; value = 0; });

        guard.DisableControllerLaunch();

        Assert.Equal(1, calls);
        Assert.True(guard.IsControllerLaunchDisabled);
    }

    [Fact]
    public void Disable_Controller_Launch_Rejects_A_Write_That_Does_Not_Stick()
    {
        var guard = new GameBarPopupGuard(() => true, () => 1, () => { });

        Assert.Throws<InvalidOperationException>(guard.DisableControllerLaunch);
    }
}
