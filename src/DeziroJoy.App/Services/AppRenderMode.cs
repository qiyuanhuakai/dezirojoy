using Avalonia;

namespace DeziroJoy.App.Services;

/// <summary>
/// Which rendering backend the GUI process asks Avalonia for. <see cref="Default"/> is the
/// platform's own detection - on Windows that is Avalonia's Win32 default of AngleEgl with
/// Software as its fallback - and is what the render window runs unless the user opts into
/// something else. <see cref="Software"/> pins the Win32 backend to CPU rendering only, as
/// the single requested mode rather than a fallback entry.
/// <para>
/// This is an experiment switch, not a repair: nothing here claims it is faster, smoother or
/// more compatible than the default, and a machine where Software cannot initialize fails to
/// start instead of falling back to a mode nobody asked for. The daemon and the headless
/// paths never read it - they build no UI, so they have no renderer to choose.
/// </para>
/// </summary>
public enum AppRenderMode
{
    /// <summary>The platform's own detection; the documented default everywhere.</summary>
    Default,

    /// <summary>Win32 CPU (software) rendering only. Inert off Windows.</summary>
    Software,
}

/// <summary>
/// The stable names and the resolution order for <see cref="AppRenderMode"/>. One vocabulary
/// is shared by the settings file, the <see cref="EnvironmentVariable"/> variable and the
/// <see cref="CommandLineSwitch"/> switch, so the three never disagree about what "software"
/// means, and an unknown value costs the override and nothing else.
/// </summary>
public static class AppRenderModes
{
    /// <summary>The environment variable that overrides the resolved mode for one run.</summary>
    public const string EnvironmentVariable = "DEZIROJOY_RENDER";

    /// <summary>The command-line switch that overrides it, in <c>--render=&lt;name&gt;</c> form.</summary>
    public const string CommandLineSwitch = "--render";

    /// <summary>The name of <see cref="AppRenderMode.Default"/>.</summary>
    public const string DefaultName = "default";

    /// <summary>The name of <see cref="AppRenderMode.Software"/>.</summary>
    public const string SoftwareName = "software";

    /// <summary>
    /// The resident presentation host's baseline - the <c>--tray</c> process that owns the
    /// tray icon and the floating layer-hint and screen-keyboard surfaces for the whole
    /// session. It is pinned to <see cref="AppRenderMode.Software"/> by policy: a host that
    /// never closes would otherwise keep a GPU stack (ANGLE/D3D11) alive for the entire
    /// logon session on behalf of an icon and two small overlays, and the user's rendering
    /// preference is about the window they work in, not about the resident host.
    /// <para>
    /// The pin is a baseline, not a lockout: an explicit <see cref="EnvironmentVariable"/>
    /// override still moves this process, because comparing the two renderers on the tray
    /// surfaces is exactly the diagnostic the variable exists for. The persisted selection is
    /// deliberately never read here, so a user who picks GPU rendering for the main window
    /// does not silently hand the GPU stack back to the resident host.
    /// </para>
    /// </summary>
    public static AppRenderMode ResidentHostBaseline => AppRenderMode.Software;

    /// <summary>The stored name of one mode.</summary>
    public static string ToName(AppRenderMode mode)
        => mode == AppRenderMode.Software ? SoftwareName : DefaultName;

    /// <summary>
    /// Parses one stored or overridden name. An unknown name is the default with the reason
    /// attached, never a throw: a hand-edited settings file or a mistyped A/B variable must
    /// cost the override and nothing else.
    /// </summary>
    public static (AppRenderMode Mode, string? Problem) Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (AppRenderMode.Default, null);
        }

        if (string.Equals(name, SoftwareName, StringComparison.OrdinalIgnoreCase))
        {
            return (AppRenderMode.Software, null);
        }

        if (string.Equals(name, DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            return (AppRenderMode.Default, null);
        }

        return (AppRenderMode.Default, $"“{name}”不是可用的渲染模式（可用：{DefaultName}、{SoftwareName}）。");
    }

    /// <summary>
    /// The mode one process starts with, given the baseline its own entry point stands for:
    /// the render window's persisted selection, or the resident host's fixed
    /// <see cref="ResidentHostBaseline"/>. The <see cref="CommandLineSwitch"/> outranks the
    /// <see cref="EnvironmentVariable"/>, which outranks that baseline, which itself falls
    /// back to <see cref="AppRenderMode.Default"/> when damaged. An override whose value
    /// cannot be parsed is ignored with a note instead of being honoured wrongly, and the
    /// next source down decides. Off Windows the whole switch is inert - the software mode
    /// exists only on the Win32 backend - so the platform default runs and the ignored
    /// override is reported rather than swallowed.
    /// </summary>
    public static (AppRenderMode Mode, string? Ignored) Resolve(
        AppRenderMode baseline,
        string? commandLine,
        string? environment,
        bool isWindows)
    {
        string? ignored = null;

        (string Source, string? Value)[] overrides =
        [
            (CommandLineSwitch, commandLine),
            (EnvironmentVariable, environment),
        ];

        foreach (var (source, value) in overrides)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var (mode, problem) = Parse(value);
            if (problem is null)
            {
                if (isWindows)
                {
                    // The override is honoured; anything ignored before it is still reported,
                    // so a run that fixed one of its two sources hears about the other.
                    return (mode, ignored);
                }

                var inert = $"{source}={value} 仅 Windows 有效，已按平台默认渲染启动。";
                return (AppRenderMode.Default, ignored is null ? inert : $"{ignored}；{inert}");
            }

            ignored ??= $"{source}={value} 已忽略：{problem}";
        }

        return (isWindows ? baseline : AppRenderMode.Default, ignored);
    }

    /// <summary>
    /// The Win32 platform options one mode asks for, or null when the mode leaves Avalonia's
    /// own defaults untouched. Software is requested as the only mode: keeping AngleEgl
    /// ahead of it would be exactly what <see cref="AppRenderMode.Default"/> already means,
    /// and an empty fallback chain is what makes the switch observable in the first place.
    /// </summary>
    public static Win32PlatformOptions? Win32Options(AppRenderMode mode)
        => mode == AppRenderMode.Software
            ? new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } }
            : null;
}
