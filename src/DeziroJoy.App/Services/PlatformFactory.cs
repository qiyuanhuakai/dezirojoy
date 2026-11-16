using DeziroJoy.Core.Platform;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Linux.Output;
using DeziroJoy.Platform.Windows.Output;
using DeziroJoy.Platform.Windows.XInput;

namespace DeziroJoy.App.Services;

/// <summary>
/// Picks the platform input/output implementation. The default backend is XInput on Windows
/// and evdev on Linux; SDL3 is an explicit opt-in (<see cref="InputBackend.Sdl3"/>) that a
/// failure to start reports instead of silently falling back to the platform backend.
/// </summary>
public static class PlatformFactory
{
    /// <summary>XInput has no Guide button bit; evdev can report BTN_MODE.</summary>
    public static bool GuideButtonSupported => !OperatingSystem.IsWindows();

    /// <summary>
    /// The provider-layer virtual pad probe, or null when the opt-in environment variable is
    /// not set. Production reads the variable; a test replaces the probe to pin the priority
    /// order without mutating process-wide environment variables every other test collection
    /// reads. Checked first whichever backend is selected.
    /// </summary>
    public static Func<IGamepadProvider?> VirtualProviderProbe { get; set; }
        = static () => VirtualGamepadProvider.TryCreate(out var provider) ? provider : null;

    public static string PlatformName => OperatingSystem.IsWindows()
        ? "Windows · XInput + SendInput"
        : OperatingSystem.IsLinux()
            ? "Linux · evdev + uinput（实验性）"
            : "不受支持的平台";

    /// <summary>
    /// The user-facing name of one backend, including the opt-in status of SDL3: the settings
    /// card and the daemon's startup line must never present the experimental backend as if
    /// it were the platform default.
    /// </summary>
    public static string DescribeBackend(InputBackend backend) => backend switch
    {
        InputBackend.Sdl3 => "SDL3 统一后端（实验性，需手动启用）",
        _ => OperatingSystem.IsWindows()
            ? "平台默认（Windows · XInput）"
            : OperatingSystem.IsLinux()
                ? "平台默认（Linux · evdev）"
                : "平台默认（当前平台不受支持）",
    };

    public static bool TryCreateProvider(out IGamepadProvider provider)
    {
        if (TryCreateProvider(InputBackend.Platform, out var created, out _))
        {
            provider = created;
            return true;
        }

        provider = null!;
        return false;
    }

    /// <summary>
    /// Creates the provider for one backend. The provider-layer virtual pad keeps priority
    /// over every backend: it is the performance baseline's own seam, and a backend selection
    /// must never quietly displace it.
    /// <para>
    /// A requested SDL3 backend that cannot start reports the failure and returns false. The
    /// caller - <see cref="EngineComposition"/> - then runs without a provider rather than
    /// silently reading through the platform backend, because a switch the user asked for
    /// that quietly did not happen is indistinguishable from one that did.
    /// </para>
    /// </summary>
    public static bool TryCreateProvider(InputBackend backend, out IGamepadProvider provider, out string? error)
    {
        // Test-only, provider-layer virtual pad. The production engine and output sink remain
        // unchanged; without DEZIROJOY_VIRTUAL_PAD this branch is just one environment read.
        if (VirtualProviderProbe() is { } virtualProvider)
        {
            provider = virtualProvider;
            error = null;
            return true;
        }

        if (backend == InputBackend.Sdl3)
        {
            return TryCreateSdl3Provider(out provider, out error);
        }

        if (OperatingSystem.IsWindows())
        {
            provider = new XInputGamepadProvider();
            error = null;
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            provider = new EvdevGamepadProvider();
            error = null;
            return true;
        }

        provider = null!;
        error = "当前平台没有可用的输入后端";
        return false;
    }

    /// <summary>
    /// Creates the SDL3 provider. A missing native runtime or an unsupported platform fails
    /// here with the reason, and the caller keeps the previous behaviour visible instead of
    /// pretending the switch happened.
    /// </summary>
    private static bool TryCreateSdl3Provider(out IGamepadProvider provider, out string? error)
    {
        try
        {
            provider = Sdl3Backend.CreateProvider();
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            provider = null!;
            error = $"SDL3 输入后端初始化失败：{exception.Message}";
            return false;
        }
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

    /// <summary>
    /// The resident-daemon service for this platform: a systemd user unit on Linux, a scheduled
    /// task on Windows. The settings card defaults to this so the Linux card is operable
    /// instead of "unsupported", and tests inject their own.
    /// </summary>
    public static IDaemonService CreateDaemonService()
        => OperatingSystem.IsLinux() ? new LinuxDaemonService() : new WindowsDaemonService();
}
