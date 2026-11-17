namespace DeziroJoy.App.Services;

/// <summary>
/// Which input backend the mapping engine reads gamepads through. <see cref="Sdl3"/> is the
/// default since S3: a new install, an upgrade that never chose, and a missing or damaged
/// settings file all read through it. <see cref="Legacy"/> is the explicit fallback - the
/// XInput/evdev backend alpha.1–alpha.4 ran by default - kept for old configurations and
/// diagnosable rollback, and never selected silently: a user (or an older build's saved
/// selection) has to ask for it, and a failure to start the selected backend is reported
/// instead of quietly reading through the other one.
/// </summary>
public enum InputBackend
{
    /// <summary>
    /// The legacy platform backend: XInput on Windows, evdev on Linux. The explicit fallback
    /// for configurations that still live under the old device identities.
    /// </summary>
    Legacy,

    /// <summary>The SDL3 unified gamepad provider - the default backend.</summary>
    Sdl3,
}

/// <summary>Stable names for <see cref="InputBackend"/>, used on disk and on the wire.</summary>
public static class InputBackendText
{
    public const string Legacy = "legacy";

    public const string Sdl3 = "sdl3";

    /// <summary>
    /// The name alpha.1–alpha.4 wrote for the legacy backend, before S3 renamed it. Still
    /// read as <see cref="InputBackend.Legacy"/> so an explicit selection made under those
    /// builds survives the upgrade; never written any more.
    /// </summary>
    public const string Platform = "platform";

    /// <summary>
    /// The backend a missing, empty or unreadable selection reads as: SDL3 since S3. One
    /// default for the new install, the upgrade that never chose, and the damaged file - the
    /// store, the text parser and the composition all answer with this.
    /// </summary>
    public const InputBackend Default = InputBackend.Sdl3;

    /// <summary>The stored name of one backend.</summary>
    public static string ToName(InputBackend backend) => backend switch
    {
        InputBackend.Sdl3 => Sdl3,
        _ => Legacy,
    };

    /// <summary>
    /// Parses one stored name. An unknown name is the default with a reason, never a throw:
    /// a hand-edited or newer-build file must cost the selection and nothing else. The
    /// legacy backend answers to its current name and to the <see cref="Platform"/> name the
    /// earlier alphas wrote, so an explicit legacy choice made before S3 is honoured rather
    /// than silently switched to the new default.
    /// </summary>
    public static (InputBackend Backend, string? Problem) Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (Default, null);
        }

        if (string.Equals(name, Sdl3, StringComparison.OrdinalIgnoreCase))
        {
            return (InputBackend.Sdl3, null);
        }

        if (string.Equals(name, Legacy, StringComparison.OrdinalIgnoreCase))
        {
            return (InputBackend.Legacy, null);
        }

        if (string.Equals(name, Platform, StringComparison.OrdinalIgnoreCase))
        {
            return (InputBackend.Legacy, null);
        }

        return (Default, $"输入后端设置无法识别（“{name}”），已按默认 SDL3 读取。");
    }
}
