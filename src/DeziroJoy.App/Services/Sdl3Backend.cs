using DeziroJoy.Core.Platform;
using DeziroJoy.Platform.Sdl3;

namespace DeziroJoy.App.Services;

/// <summary>
/// The SDL3 backend seam: the one place the App knows the SDL3 provider's construction.
/// Construction alone proves nothing - the provider initializes lazily on its first scan -
/// so the factory runs that minimal scan here and only accepts a provider whose runtime
/// actually came up. A missing native library or an unusable gamepad subsystem fails here
/// with the reason, and <see cref="PlatformFactory"/> reports the failure instead of falling
/// back to the platform backend. A runtime that came up with no gamepads is not a failure:
/// that is a working backend with nothing plugged in yet, and its guidance reaches the user
/// through the daemon's device note instead.
/// <para>
/// The provider is built with the config-root mapping file as its source, so the mappings the
/// <c>map</c> flow writes are handed to SDL before the first enumeration - the daemon and the
/// probe then agree on which devices are standard gamepads. There is no hot reload: the file is
/// read once per daemon start, which is why the mapping flow tells the user to restart the
/// resident service.
/// </para>
/// </summary>
internal static class Sdl3Backend
{
    /// <summary>
    /// Where the provider reads persisted SDL mappings from. The production value is the config
    /// root's <c>sdl-mappings.json</c>; a test replaces it so the App never touches the
    /// signed-in user's configuration.
    /// </summary>
    public static Func<ISdl3MappingSource> MappingSource { get; set; }
        = static () => new Sdl3MappingFileSource();

    /// <summary>
    /// The scan the factory runs to prove the runtime loads: the provider's own first
    /// enumeration, read back as its diagnostic. Seam so a test can drive the unavailable
    /// and healthy paths deterministically without depending on the native library being
    /// present or absent.
    /// </summary>
    public static Func<Sdl3GamepadProvider, Sdl3ScanDiagnostic?> DiagnosticScan { get; set; }
        = static provider =>
        {
            provider.GetConnected();
            return provider.LastScanDiagnostic;
        };

    public static IGamepadProvider CreateProvider()
    {
        var provider = new Sdl3GamepadProvider(mappingSource: MappingSource());

        if (DiagnosticScan(provider) is { Outcome: Sdl3ScanOutcome.SdlUnavailable } unavailable)
        {
            provider.Dispose();
            throw new InvalidOperationException(
                string.IsNullOrEmpty(unavailable.InitError)
                    ? "SDL3 运行时不可用：未知原因。请确认应用分发了当前平台的原生 SDL3 运行时"
                      + "（Windows 为 SDL3.dll，Linux 为 libSDL3.so）。"
                    : $"SDL3 运行时不可用：{unavailable.InitError}");
        }

        return provider;
    }
}
