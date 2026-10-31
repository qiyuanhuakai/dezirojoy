using DeziroJoy.Core.Platform;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Linux.Output;
using DeziroJoy.Platform.Windows.Output;
using DeziroJoy.Platform.Windows.XInput;

namespace DeziroJoy.App.Services;

/// <summary>
/// Picks the platform input/output implementation. Windows uses XInput + SendInput;
/// Linux uses evdev + uinput (experimental, works under both X11 and Wayland).
/// </summary>
public static class PlatformFactory
{
    public static string PlatformName => OperatingSystem.IsWindows()
        ? "Windows · XInput + SendInput"
        : OperatingSystem.IsLinux()
            ? "Linux · evdev + uinput（实验性）"
            : "不受支持的平台";

    public static bool TryCreateProvider(out IGamepadProvider provider)
    {
        if (OperatingSystem.IsWindows())
        {
            provider = new XInputGamepadProvider();
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            provider = new EvdevGamepadProvider();
            return true;
        }

        provider = null!;
        return false;
    }

    public static bool TryCreateSink(out IOutputSink sink, out string? error)
    {
        if (OperatingSystem.IsWindows())
        {
            sink = new SendInputOutputSink();
            error = null;
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                sink = new UInputOutputSink();
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                sink = null!;
                error = exception.Message;
                return false;
            }
        }

        sink = null!;
        error = "当前平台不支持输出模拟";
        return false;
    }
}
