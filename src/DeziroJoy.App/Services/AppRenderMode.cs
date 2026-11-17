using Avalonia;

namespace DeziroJoy.App.Services;

/// <summary>
/// Which rendering backend the GUI process asks Avalonia for. <see cref="Default"/> is the
/// platform's own detection - on Windows that is Avalonia's Win32 default of AngleEgl with
/// Software as its fallback - and is what every install runs. <see cref="Software"/> pins the
/// Win32 backend to CPU rendering only, as the single requested mode rather than a fallback
/// entry.
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
    /// <summary>The environment variable that overrides the persisted selection for one run.</summary>
    public const string EnvironmentVariable = "DEZIROJOY_RENDER";

    /// <summary>The command-line switch that overrides it, in <c>--render=&lt;name&gt;</c> form.</summary>
    public const string CommandLineSwitch = "--render";

    /// <summary>The name of <see cref="AppRenderMode.Default"/>.</summary>
    public const string DefaultName = "default";

    /// <summary>The name of <see cref="AppRenderMode.Software"/>.</summary>
    public const string SoftwareName = "software";

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
    /// The mode one process starts with. The <see cref="CommandLineSwitch"/> outranks the
    /// <see cref="EnvironmentVariable"/>, which outranks the persisted selection, which
    /// itself falls back to <see cref="AppRenderMode.Default"/> when damaged. An override
    /// whose value cannot be parsed is ignored with a note instead of being honoured
    /// wrongly, and the next source down decides. Off Windows the whole switch is inert -
    /// the software mode exists only on the Win32 backend - so the platform default runs
    /// and the ignored override is reported rather than swallowed.
    /// </summary>
    public static (AppRenderMode Mode, string? Ignored) Resolve(
        AppRenderMode persisted,
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

        return (isWindows ? persisted : AppRenderMode.Default, ignored);
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
