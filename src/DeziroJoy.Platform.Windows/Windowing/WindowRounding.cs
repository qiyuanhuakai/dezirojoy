using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeziroJoy.Platform.Windows.Windowing;

/// <summary>
/// Rounds the window's corners the way Windows 11 rounds a stock window. The app draws its
/// own chrome, so without this the shell keeps the square corners of a decoration-less
/// window on a platform whose own windows are rounded.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DwmSetWindowAttribute"/> is a documented, always-present export of
/// dwmapi.dll; the rounding attribute itself only exists on Windows 11 build 22000 and
/// later. On an older build the call fails and the window simply keeps square corners, so
/// there is nothing to version-gate and no reason to fall back.
/// </para>
/// <para>
/// This is presentation only. It changes no behaviour and it is the reason the layer lives
/// in the Windows platform project rather than in the shared UI.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowRounding
{
    private const int DwmwaWindowCornerPreference = 33;

    private const int DwmcpRound = 2;

    public static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var preference = DwmcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);
}
