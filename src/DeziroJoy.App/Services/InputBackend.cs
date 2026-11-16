namespace DeziroJoy.App.Services;

/// <summary>
/// Which input backend the mapping engine reads gamepads through. <see cref="Platform"/> is
/// the status quo and the default: XInput on Windows, evdev on Linux. <see cref="Sdl3"/> is
/// the opt-in unified provider - the S2 initial wiring - and is never selected silently: a
/// user (or a future default switch) has to ask for it, and a failure to start it is
/// reported instead of quietly falling back to the platform backend.
/// </summary>
public enum InputBackend
{
    /// <summary>The platform backend: XInput on Windows, evdev on Linux.</summary>
    Platform,

    /// <summary>The SDL3 unified gamepad provider.</summary>
    Sdl3,
}

/// <summary>Stable names for <see cref="InputBackend"/>, used on disk and on the wire.</summary>
public static class InputBackendText
{
    public const string Platform = "platform";

    public const string Sdl3 = "sdl3";

    /// <summary>The stored name of one backend.</summary>
    public static string ToName(InputBackend backend) => backend switch
    {
        InputBackend.Sdl3 => Sdl3,
        _ => Platform,
    };

    /// <summary>
    /// Parses one stored name. An unknown name is the platform default with a reason, never
    /// a throw: a hand-edited or newer-build file must cost the selection and nothing else.
    /// </summary>
    public static (InputBackend Backend, string? Problem) Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (InputBackend.Platform, null);
        }

        if (string.Equals(name, Sdl3, StringComparison.OrdinalIgnoreCase))
        {
            return (InputBackend.Sdl3, null);
        }

        if (string.Equals(name, Platform, StringComparison.OrdinalIgnoreCase))
        {
            return (InputBackend.Platform, null);
        }

        return (InputBackend.Platform, $"输入后端设置无法识别（“{name}”），已按平台默认读取。");
    }
}
