using SDL3;
using System.Globalization;
using System.Text;

namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// The real <see cref="ISdl3Api"/>: every SDL call the provider needs, over the SDL3-CS
/// bindings and the per-platform native packages (libSDL3.so on Linux, SDL3.dll on Windows).
/// All native entry points are resolved lazily on first call, so constructing this type - or
/// the provider that owns it - never loads the native library; a missing runtime surfaces as
/// a failed initialization with SDL's own error text, not as a crash at construction.
/// </summary>
internal sealed class Sdl3Api : ISdl3Api
{
    private bool _initialized;

    public Sdl3RuntimeInfo? GetRuntimeInfo()
    {
        try
        {
            return new Sdl3RuntimeInfo(SDL.GetVersion(), SDL.GetRevision() ?? string.Empty);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    public bool TryEnsureInitialized(out string? error)
    {
        if (_initialized)
        {
            error = null;
            return true;
        }

        try
        {
            if (!SDL.Init(SDL.InitFlags.Gamepad))
            {
                error = Describe(SDL.GetError(), "SDL_Init(SDL_INIT_GAMEPAD) 失败");
                return false;
            }

            _initialized = true;
            error = null;
            return true;
        }
        catch (DllNotFoundException)
        {
            error = "找不到原生 SDL3 运行时（Windows: SDL3.dll，Linux: libSDL3.so）；请确认应用包含当前平台的原生部署。";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            error = "原生 SDL3 运行时缺少所需入口点；版本可能过旧，请更新随应用分发的 SDL3。";
            return false;
        }
    }

    public void Shutdown()
    {
        if (!_initialized)
        {
            return;
        }

        _initialized = false;

        try
        {
            SDL.QuitSubSystem(SDL.InitFlags.Gamepad);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    public void UpdateGamepads()
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            SDL.UpdateGamepads();
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    public IReadOnlyList<Sdl3GamepadEvent> PumpEvents()
    {
        var events = new List<Sdl3GamepadEvent>();

        if (!_initialized)
        {
            return events;
        }

        try
        {
            while (SDL.PollEvent(out var sdlEvent))
            {
                var type = (SDL.EventType)sdlEvent.Type;
                var eventType = type switch
                {
                    SDL.EventType.GamepadAdded => Sdl3GamepadEventType.Added,
                    SDL.EventType.GamepadRemoved => Sdl3GamepadEventType.Removed,
                    SDL.EventType.GamepadRemapped => Sdl3GamepadEventType.Remapped,
                    _ => (Sdl3GamepadEventType?)null,
                };

                if (eventType is { } mapped)
                {
                    events.Add(new Sdl3GamepadEvent(mapped, sdlEvent.GDevice.Which));
                }
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return events;
    }

    public IReadOnlyList<uint> GetGamepadIds()
    {
        if (!_initialized)
        {
            return Array.Empty<uint>();
        }

        try
        {
            return SDL.GetGamepads(out _) ?? Array.Empty<uint>();
        }
        catch (DllNotFoundException)
        {
            return Array.Empty<uint>();
        }
        catch (EntryPointNotFoundException)
        {
            return Array.Empty<uint>();
        }
    }

    public Sdl3GamepadHandle? OpenGamepad(uint instanceId, out string? error)
    {
        error = null;

        try
        {
            var pointer = SDL.OpenGamepad(instanceId);
            if (pointer == IntPtr.Zero)
            {
                error = Describe(SDL.GetError(), "SDL_OpenGamepad 失败");
                return null;
            }

            return new Sdl3GamepadHandle(pointer);
        }
        catch (DllNotFoundException)
        {
            error = "找不到原生 SDL3 运行时。";
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            error = "原生 SDL3 运行时版本过旧，缺少 SDL_OpenGamepad。";
            return null;
        }
    }

    public void CloseGamepad(Sdl3GamepadHandle handle)
    {
        var pointer = handle.Pointer;
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        try
        {
            SDL.CloseGamepad(pointer);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    public Sdl3GamepadInfo ReadInfo(Sdl3GamepadHandle handle)
    {
        var pointer = handle.Pointer;
        var instanceId = Safe(() => SDL.GetGamepadID(pointer));
        var joystick = Safe(() => SDL.GetGamepadJoystick(pointer));

        var mapping = Coalesce(Safe(() => SDL.GetGamepadMapping(pointer)));
        var guid = Safe(() => SDL.GetJoystickGUID(joystick));
        var guidString = GuidToString(guid);
        var type = Safe(() => SDL.GetGamepadType(pointer));

        // SDL exposes no "which database produced this mapping" API. Comparing the effective
        // mapping with what the database answers for the GUID separates runtime overrides
        // (SDL_SetGamepadMapping) from the database result; built-in vs user-database entries
        // stay indistinguishable, and the probe says so instead of guessing.
        var databaseMapping = Coalesce(Safe(() => SDL.GetGamepadMappingForGUID(guid)));
        var mappingSource = string.Equals(mapping, databaseMapping, StringComparison.Ordinal)
            ? "sdl-database"
            : "runtime-override";

        return new Sdl3GamepadInfo
        {
            InstanceId = instanceId,
            Name = Coalesce(Safe(() => SDL.GetGamepadName(pointer))),
            GuidString = guidString,
            Vendor = Safe(() => SDL.GetGamepadVendor(pointer)),
            Product = Safe(() => SDL.GetGamepadProduct(pointer)),
            ProductVersion = Safe(() => SDL.GetGamepadProductVersion(pointer)),
            Serial = Coalesce(Safe(() => SDL.GetGamepadSerial(pointer))),
            Path = Coalesce(Safe(() => SDL.GetGamepadPath(pointer))),
            Type = (int)type,
            TypeName = type.ToString(),
            Mapping = mapping,
            MappingSource = mappingSource,
            PlayerIndex = Safe(() => SDL.GetGamepadPlayerIndex(pointer)),
            ConnectionState = Safe(() => SDL.GetGamepadConnectionState(pointer)).ToString(),
            TouchpadCount = Safe(() => SDL.GetNumGamepadTouchpads(pointer)),
            TouchpadFingerCounts = ReadTouchpadFingerCounts(pointer),
            Sensors = ReadSensors(pointer),
            DeclaredButtonCount = CountDeclaredButtons(pointer),
            FaceButtonLabels = ReadFaceButtonLabels(pointer),
        };
    }

    public bool GetButton(Sdl3GamepadHandle handle, SDL.GamepadButton button)
        => Safe(() => SDL.GetGamepadButton(handle.Pointer, button));

    public short GetAxis(Sdl3GamepadHandle handle, SDL.GamepadAxis axis)
        => Safe(() => SDL.GetGamepadAxis(handle.Pointer, axis));

    private static List<int> ReadTouchpadFingerCounts(nint pointer)
    {
        var counts = new List<int>();
        var touchpads = Safe(() => SDL.GetNumGamepadTouchpads(pointer));

        for (var i = 0; i < touchpads; i++)
        {
            counts.Add(Safe(() => SDL.GetNumGamepadTouchpadFingers(pointer, i)));
        }

        return counts;
    }

    /// <summary>
    /// Declared sensors with data rates. Capability probing only - reading sensor data is
    /// the S4 input-model work, and a sensor listed here is not a claim that the app uses it.
    /// </summary>
    private static List<string> ReadSensors(nint pointer)
    {
        var sensors = new List<string>();

        foreach (var sensor in Enum.GetValues<SDL.SensorType>())
        {
            if (sensor is SDL.SensorType.Unknown or SDL.SensorType.Invalid or SDL.SensorType.Count)
            {
                continue;
            }

            if (!Safe(() => SDL.GamepadHasSensor(pointer, sensor)))
            {
                continue;
            }

            var rate = Safe(() => SDL.GetGamepadSensorDataRate(pointer, sensor));
            var name = SensorName(sensor);
            sensors.Add(rate > 0f ? $"{name}（{rate.ToString("0.#", CultureInfo.InvariantCulture)} Hz）" : name);
        }

        return sensors;
    }

    private static string SensorName(SDL.SensorType sensor) => sensor switch
    {
        SDL.SensorType.Accel => "加速度计",
        SDL.SensorType.Gyro => "陀螺仪",
        SDL.SensorType.AccelL => "左加速度计",
        SDL.SensorType.GyroL => "左陀螺仪",
        SDL.SensorType.AccelR => "右加速度计",
        SDL.SensorType.GyroR => "右陀螺仪",
        _ => sensor.ToString(),
    };

    private static List<string> ReadFaceButtonLabels(nint pointer)
    {
        SDL.GamepadButton[] face =
        [
            SDL.GamepadButton.South,
            SDL.GamepadButton.East,
            SDL.GamepadButton.West,
            SDL.GamepadButton.North,
        ];

        var labels = new List<string>(face.Length);
        foreach (var button in face)
        {
            labels.Add(Safe(() => SDL.GetGamepadButtonLabel(pointer, button)).ToString());
        }

        return labels;
    }

    /// <summary>
    /// Buttons the SDL mapping declares, counted over the standard set. A declared button is
    /// what the mapping says exists - not proof of a physical control - which is exactly the
    /// distinction the UI's "declared but never detected" policy (U2) is built on.
    /// </summary>
    private static int CountDeclaredButtons(nint pointer)
    {
        var count = 0;
        foreach (var button in Enum.GetValues<SDL.GamepadButton>())
        {
            if (button is SDL.GamepadButton.Invalid or SDL.GamepadButton.Count)
            {
                continue;
            }

            if (Safe(() => SDL.GamepadHasButton(pointer, button)))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Renders an SDL GUID as its 32-character hex string. The buffer holds the 32 hex digits
    /// plus the terminator SDL writes; a short or unterminated answer degrades to the bytes
    /// actually present instead of throwing inside a diagnostic read.
    /// </summary>
    private static string GuidToString(SDL.GUID guid)
    {
        Span<byte> buffer = stackalloc byte[33];

        try
        {
            SDL.GUIDToString(guid, buffer, buffer.Length);
        }
        catch (DllNotFoundException)
        {
            return string.Empty;
        }
        catch (EntryPointNotFoundException)
        {
            return string.Empty;
        }

        var end = buffer.IndexOf((byte)0);
        if (end < 0)
        {
            end = 32;
        }

        return Encoding.UTF8.GetString(buffer[..end]);
    }

    private static string Coalesce(string? value) => value ?? string.Empty;

    private static string Describe(string? error, string fallback)
        => string.IsNullOrEmpty(error) ? fallback : error;

    /// <summary>
    /// Runs one SDL query, degrading to the type's default when the native call fails - a
    /// diagnostic read must never take the daemon down over one unreadable field.
    /// </summary>
    private static T Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (DllNotFoundException)
        {
            return default!;
        }
        catch (EntryPointNotFoundException)
        {
            return default!;
        }
    }
}
