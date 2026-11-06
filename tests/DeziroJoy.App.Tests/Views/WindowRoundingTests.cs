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
    public void A_Real_Window_Handle_Is_Accepted()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DWM window rounding is Windows-only.");
            return;
        }

        var window = new Avalonia.Controls.Window { Width = 320, Height = 240 };
        window.Show();

        try
        {
            var handle = window.TryGetPlatformHandle();

            Assert.NotNull(handle);

            WindowRounding.Apply(handle!.Handle);
        }
        finally
        {
            window.Close();
        }
    }
}
