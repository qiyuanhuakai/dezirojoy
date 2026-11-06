using DeziroJoy.Platform.Windows.Windowing;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The shell draws its own chrome, so the window corners have to be rounded by the platform.
/// The rounding itself is a DWM attribute, which only exists on Windows; on every other
/// platform the app keeps the square corners its own chrome already has.
/// </summary>
public sealed class WindowRoundingTests
{
    [Fact]
    public void A_Zero_Handle_Is_A_Safe_No_Op()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DWM window rounding is Windows-only.");
            return;
        }

        WindowRounding.Apply(IntPtr.Zero);
    }

    [Fact]
    public void An_Arbitrary_Handle_Is_Accepted_Without_Throwing()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DWM window rounding is Windows-only.");
            return;
        }

        // A handle that belongs to no window: DWM answers with a failed HRESULT rather than
        // raising, so rounding must tolerate it. The production caller passes exactly this
        // when the platform cannot hand back a handle.
        WindowRounding.Apply(new IntPtr(0x00001234));
    }
}
