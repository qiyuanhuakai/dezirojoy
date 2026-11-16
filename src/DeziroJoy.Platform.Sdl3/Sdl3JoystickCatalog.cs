using SDL3;

namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// What one plain-joystick scan saw. The joysticks are listed with the raw counts a mapping has
/// to be written against; <see cref="Unmapped"/> is the subset the mapping-supplement flow can
/// offer, and <see cref="MappedAsGamepadCount"/> says how many SDL already reads through the
/// standard path so a zero-unmapped result is distinguishable from "SDL sees nothing at all".
/// </summary>
public sealed record Sdl3JoystickScan
{
    /// <summary>Runtime version of the linked SDL library, or null when it could not be read.</summary>
    public required Sdl3RuntimeInfo? Runtime { get; init; }

    /// <summary>Whether the joystick subsystem came up at all.</summary>
    public required bool Initialized { get; init; }

    /// <summary>Why it would not initialize, when it would not; null otherwise.</summary>
    public required string? InitError { get; init; }

    /// <summary>Every joystick SDL currently sees, in SDL's order.</summary>
    public required IReadOnlyList<Sdl3JoystickInfo> Joysticks { get; init; }

    /// <summary>
    /// Joysticks with no standard gamepad mapping - the ones that need one written by hand.
    /// A device here is not claimed to be supported: it is listed so the user can map it.
    /// </summary>
    public IReadOnlyList<Sdl3JoystickInfo> Unmapped =>
        Joysticks.Where(joystick => !joystick.HasStandardMapping).ToList();

    /// <summary>How many joysticks SDL already reads as standard gamepads.</summary>
    public int MappedAsGamepadCount => Joysticks.Count(joystick => joystick.HasStandardMapping);

    /// <summary>SDL's platform token, the one a mapping's <c>platform:</c> field must carry.</summary>
    public required string Platform { get; init; }

    public string Hint => Initialized
        ? Unmapped.Count == 0
            ? "没有只能作普通 Joystick 枚举、缺少标准 Gamepad 映射的设备"
            : $"有 {Unmapped.Count} 个设备缺少标准 Gamepad 映射，可用 map 流程补充"
        : $"SDL3 运行时不可用，无法枚举普通 Joystick：{InitError ?? "未知原因"}";
}

/// <summary>
/// The public face of the mapping-supplement flow over the real SDL runtime. Everything here
/// goes through <see cref="ISdl3Api"/>'s real implementation; the provider and the tests drive
/// the same operations through the seam instead.
/// </summary>
public static class Sdl3JoystickCatalog
{
    /// <summary>
    /// SDL's platform token for this machine, or the OS name when SDL cannot be asked. This is
    /// what a generated mapping's <c>platform:</c> field carries, so a mapping recorded on
    /// Windows says so.
    /// </summary>
    public static string PlatformName => Sdl3MappingPlatform.Resolve();

    /// <summary>
    /// Enumerates the plain joysticks SDL currently sees, one-shot: SDL is initialized, the
    /// devices are read and the subsystem is released again - no handle is kept.
    /// </summary>
    public static Sdl3JoystickScan Scan() => Scan(new Sdl3Api());

    /// <summary>Test seam: the same scan over an injected api.</summary>
    internal static Sdl3JoystickScan Scan(ISdl3Api api)
    {
        Sdl3RuntimeInfo? runtime;
        string? initError = null;
        var initialized = api.TryEnsureInitialized(out initError);
        runtime = api.GetRuntimeInfo();

        IReadOnlyList<Sdl3JoystickInfo> joysticks = [];
        if (initialized)
        {
            joysticks = api.GetJoysticks();
        }

        try
        {
            return new Sdl3JoystickScan
            {
                Runtime = runtime,
                Initialized = initialized,
                InitError = initError,
                Joysticks = joysticks,
                Platform = PlatformName,
            };
        }
        finally
        {
            api.Shutdown();
        }
    }

    /// <summary>
    /// Opens one plain joystick for the interactive capture flow. The caller owns the reader and
    /// must dispose it - the handle is closed there, exactly once.
    /// </summary>
    public static Sdl3JoystickOpenResult OpenJoystick(uint instanceId)
    {
        var api = new Sdl3Api();
        if (!api.TryEnsureInitialized(out var initError))
        {
            return new Sdl3JoystickOpenResult(null, initError ?? "SDL3 游戏手柄子系统初始化失败");
        }

        var handle = api.OpenJoystick(instanceId, out var error);
        if (handle is null)
        {
            api.Shutdown();
            return new Sdl3JoystickOpenResult(null, error ?? "SDL_OpenJoystick 失败");
        }

        return new Sdl3JoystickOpenResult(new Sdl3JoystickReader(api, handle), null);
    }

    /// <summary>
    /// Applies one mapping to the running SDL instance and reports whether SDL took it. A
    /// mapping SDL rejects is a diagnostic the capture flow prints instead of persisting.
    /// <para>
    /// The confirmation is SDL's own GUID lookup rather than the gamepad list: SDL caches the
    /// per-instance "is this a gamepad" answer for the life of the process, so a mapping added
    /// after enumeration does not make the device appear as a gamepad until the next start.
    /// That is precisely the restart the flow tells the user about - the GUID lookup proves the
    /// mapping is registered, which is what survives the restart.
    /// </para>
    /// </summary>
    public static Sdl3MappingApplyResult ApplyMapping(string mapping, string guidString)
    {
        var api = new Sdl3Api();
        try
        {
            if (!api.TryEnsureInitialized(out var initError))
            {
                return new Sdl3MappingApplyResult(false, initError ?? "SDL3 游戏手柄子系统初始化失败", null);
            }

            var applied = api.TryAddGamepadMapping(mapping, out var error);
            var registered = applied ? api.GetMappingForGuid(guidString) : null;
            return new Sdl3MappingApplyResult(applied, error, registered);
        }
        finally
        {
            api.Shutdown();
        }
    }
}

/// <summary>Opening one plain joystick: either a reader or the reason it did not open.</summary>
public sealed record Sdl3JoystickOpenResult(Sdl3JoystickReader? Reader, string? Error)
{
    public bool Opened => Reader is not null;
}

/// <summary>Applying one mapping string to the running SDL instance.</summary>
public sealed record Sdl3MappingApplyResult(bool Applied, string? Error, string? RegisteredMapping)
{
    /// <summary>
    /// What SDL's mapping database now answers for this GUID, or null when it answers nothing.
    /// A non-null value is the proof that the mapping is registered and will survive a restart.
    /// </summary>
    public string? RegisteredMapping { get; } = RegisteredMapping;
}

/// <summary>
/// A live plain joystick, read through the real SDL joystick API. The capture flow samples this
/// in a loop and compares consecutive samples; disposing it closes the native handle and
/// releases the joystick subsystem this reader initialized.
/// </summary>
public sealed class Sdl3JoystickReader : ISdl3JoystickReader
{
    private readonly ISdl3Api _api;
    private readonly Sdl3JoystickHandle _handle;
    private bool _disposed;

    internal Sdl3JoystickReader(ISdl3Api api, Sdl3JoystickHandle handle)
    {
        _api = api;
        _handle = handle;
    }

    /// <summary>
    /// One raw sample of every button, axis and hat, as of now.
    /// <para>
    /// The pump comes first, and is not optional: <c>SDL_GetJoystickButton</c>/<c>Axis</c>/
    /// <c>Hat</c> read the cached state the joystick driver last wrote, so without a pump every
    /// sample would return the values from when the device was opened - the capture flow would
    /// wait forever for a button that was already pressed. <c>SDL_UpdateGamepads</c> is the
    /// documented manual pump and delegates to <c>SDL_UpdateJoysticks</c>, so one call refreshes
    /// every open joystick.
    /// </para>
    /// </summary>
    public Sdl3JoystickSnapshot Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _api.UpdateGamepads();
        return _api.ReadJoystick(_handle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _api.CloseJoystick(_handle);
        _api.Shutdown();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The seam the interactive capture loop is written against, so the flow's polling, its skip and
/// cancel commands and its end-of-flow reporting are tested against scripted input instead of a
/// controller. A sample is "the device's state as of now": the real implementation pumps SDL
/// before reading, because the raw joystick getters serve cached state.
/// </summary>
public interface ISdl3JoystickReader : IDisposable
{
    Sdl3JoystickSnapshot Sample();
}

/// <summary>SDL's platform token, resolved once SDL is reachable.</summary>
public static class Sdl3MappingPlatform
{
    /// <summary>
    /// SDL's own answer, or the OS name when SDL cannot be asked. SDL reports "Windows" on
    /// Windows and "Linux" on Linux, so the fallback agrees with it on both supported platforms.
    /// </summary>
    public static string Resolve()
    {
        try
        {
            var platform = SDL.GetPlatform();
            if (!string.IsNullOrEmpty(platform))
            {
                return platform;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return OperatingSystem.IsWindows()
            ? "Windows"
            : OperatingSystem.IsLinux()
                ? "Linux"
                : "Unknown";
    }
}
