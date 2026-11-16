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
            // SDL_INIT_GAMEPAD implies the joystick subsystem; the flag is requested explicitly
            // anyway so the plain-joystick enumeration the mapping flow needs is covered by the
            // same initialization rather than depending on that implication.
            if (!SDL.Init(SDL.InitFlags.Gamepad | SDL.InitFlags.Joystick))
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
            DeclaredButtons = ReadDeclaredButtons(pointer),
            DeclaredButtonCount = CountDeclaredButtons(pointer),
            DeclaredAxes = ReadDeclaredAxes(pointer),
            FaceButtonLabels = ReadFaceButtonLabels(pointer),
        };
    }

    public bool GetButton(Sdl3GamepadHandle handle, SDL.GamepadButton button)
        => Safe(() => SDL.GetGamepadButton(handle.Pointer, button));

    public short GetAxis(Sdl3GamepadHandle handle, SDL.GamepadAxis axis)
        => Safe(() => SDL.GetGamepadAxis(handle.Pointer, axis));

    public bool TryAddGamepadMapping(string mapping, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(mapping))
        {
            error = "映射字符串为空。";
            return false;
        }

        try
        {
            var result = SDL.AddGamepadMapping(mapping);
            if (result < 0)
            {
                error = Describe(SDL.GetError(), "SDL_AddGamepadMapping 拒绝了该映射");
                return false;
            }

            return true;
        }
        catch (DllNotFoundException)
        {
            error = "找不到原生 SDL3 运行时。";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            error = "原生 SDL3 运行时版本过旧，缺少 SDL_AddGamepadMapping。";
            return false;
        }
    }

    public IReadOnlyList<Sdl3JoystickInfo> GetJoysticks()
    {
        if (!_initialized)
        {
            return Array.Empty<Sdl3JoystickInfo>();
        }

        try
        {
            var ids = SDL.GetJoysticks(out _);
            if (ids is null || ids.Length == 0)
            {
                return Array.Empty<Sdl3JoystickInfo>();
            }

            // SDL shares one id space between joysticks and gamepads, so a joystick that already
            // has a standard mapping appears in both lists. That is the honest test for "this
            // device does not need a hand-written mapping", and it costs one enumeration.
            var gamepadIds = new HashSet<uint>(SDL.GetGamepads(out _) ?? Array.Empty<uint>());
            var joysticks = new List<Sdl3JoystickInfo>(ids.Length);

            foreach (var instanceId in ids)
            {
                if (gamepadIds.Contains(instanceId))
                {
                    // Already a standard gamepad: the per-ID queries are enough, and opening it
                    // here would collide with the provider's own open of the same device.
                    joysticks.Add(DescribeFromId(instanceId, hasStandardMapping: true));
                    continue;
                }

                var pointer = SDL.OpenJoystick(instanceId);
                if (pointer == nint.Zero)
                {
                    continue;
                }

                try
                {
                    joysticks.Add(ReadJoystickInfo(instanceId, pointer, hasStandardMapping: true));
                }
                finally
                {
                    SDL.CloseJoystick(pointer);
                }
            }

            return joysticks;
        }
        catch (DllNotFoundException)
        {
            return Array.Empty<Sdl3JoystickInfo>();
        }
        catch (EntryPointNotFoundException)
        {
            return Array.Empty<Sdl3JoystickInfo>();
        }
    }

    public Sdl3JoystickHandle? OpenJoystick(uint instanceId, out string? error)
    {
        error = null;

        try
        {
            var pointer = SDL.OpenJoystick(instanceId);
            if (pointer == nint.Zero)
            {
                error = Describe(SDL.GetError(), "SDL_OpenJoystick 失败");
                return null;
            }

            return new Sdl3JoystickHandle(pointer);
        }
        catch (DllNotFoundException)
        {
            error = "找不到原生 SDL3 运行时。";
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            error = "原生 SDL3 运行时版本过旧，缺少 SDL_OpenJoystick。";
            return null;
        }
    }

    public void CloseJoystick(Sdl3JoystickHandle handle)
    {
        var pointer = handle.Pointer;
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        handle.MarkClosed();

        try
        {
            SDL.CloseJoystick(pointer);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    public Sdl3JoystickSnapshot ReadJoystick(Sdl3JoystickHandle handle)
    {
        var pointer = handle.Pointer;
        if (pointer == IntPtr.Zero)
        {
            return Sdl3JoystickSnapshot.Empty;
        }

        try
        {
            var buttons = Math.Max(SDL.GetNumJoystickButtons(pointer), 0);
            var axes = Math.Max(SDL.GetNumJoystickAxes(pointer), 0);
            var hats = Math.Max(SDL.GetNumJoystickHats(pointer), 0);

            var buttonStates = new bool[buttons];
            for (var i = 0; i < buttons; i++)
            {
                buttonStates[i] = SDL.GetJoystickButton(pointer, i);
            }

            var axisStates = new short[axes];
            for (var i = 0; i < axes; i++)
            {
                axisStates[i] = SDL.GetJoystickAxis(pointer, i);
            }

            var hatStates = new byte[hats];
            for (var i = 0; i < hats; i++)
            {
                hatStates[i] = (byte)SDL.GetJoystickHat(pointer, i);
            }

            return new Sdl3JoystickSnapshot(buttonStates, axisStates, hatStates);
        }
        catch (DllNotFoundException)
        {
            return Sdl3JoystickSnapshot.Empty;
        }
        catch (EntryPointNotFoundException)
        {
            return Sdl3JoystickSnapshot.Empty;
        }
    }

    /// <summary>
    /// The joystick record of one device SDL already maps as a gamepad, read through the
    /// per-instance-id queries so nothing has to be opened. Counts stay zero: the mapping flow
    /// never offers these devices, and a gamepad's raw control counts are not its standard
    /// layout anyway.
    /// </summary>
    private static Sdl3JoystickInfo DescribeFromId(uint instanceId, bool hasStandardMapping)
    {
        var guid = Safe(() => SDL.GetJoystickGUIDForID(instanceId));
        var type = Safe(() => SDL.GetJoystickTypeForID(instanceId));

        return new Sdl3JoystickInfo
        {
            InstanceId = instanceId,
            Name = Coalesce(Safe(() => SDL.GetJoystickNameForID(instanceId))),
            GuidString = GuidToString(guid),
            Vendor = Safe(() => SDL.GetJoystickVendorForID(instanceId)),
            Product = Safe(() => SDL.GetJoystickProductForID(instanceId)),
            ProductVersion = Safe(() => SDL.GetJoystickProductVersionForID(instanceId)),
            Serial = string.Empty,
            Path = Coalesce(Safe(() => SDL.GetJoystickPathForID(instanceId))),
            ButtonCount = 0,
            AxisCount = 0,
            HatCount = 0,
            TypeName = type.ToString(),
            HasStandardMapping = hasStandardMapping,
        };
    }

    public string? GetMappingForGuid(string guidString)
    {
        if (!_initialized || string.IsNullOrWhiteSpace(guidString))
        {
            return null;
        }

        try
        {
            return SDL.GetGamepadMappingForGUID(SDL.StringToGUID(guidString));
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

    /// <summary>
    /// The joystick record of one open plain joystick, including the serial and the raw
    /// button/axis/hat counts a mapping has to be written against.
    /// </summary>
    private static Sdl3JoystickInfo ReadJoystickInfo(uint instanceId, nint pointer, bool hasStandardMapping)
    {
        var type = Safe(() => SDL.GetJoystickType(pointer));

        return new Sdl3JoystickInfo
        {
            InstanceId = instanceId,
            Name = Coalesce(Safe(() => SDL.GetJoystickName(pointer))),
            GuidString = GuidToString(Safe(() => SDL.GetJoystickGUID(pointer))),
            Vendor = Safe(() => SDL.GetJoystickVendor(pointer)),
            Product = Safe(() => SDL.GetJoystickProduct(pointer)),
            ProductVersion = Safe(() => SDL.GetJoystickProductVersion(pointer)),
            Serial = Coalesce(Safe(() => SDL.GetJoystickSerial(pointer))),
            Path = Coalesce(Safe(() => SDL.GetJoystickPath(pointer))),
            ButtonCount = Math.Max(Safe(() => SDL.GetNumJoystickButtons(pointer)), 0),
            AxisCount = Math.Max(Safe(() => SDL.GetNumJoystickAxes(pointer)), 0),
            HatCount = Math.Max(Safe(() => SDL.GetNumJoystickHats(pointer)), 0),
            TypeName = type.ToString(),
            HasStandardMapping = hasStandardMapping,
        };
    }

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
    /// The declared standard buttons, by mapping key. The raw evidence the generic control
    /// description is built from, so a surface can say "SDL declares these" without re-querying
    /// the device and without guessing which ones matter.
    /// </summary>
    private static List<string> ReadDeclaredButtons(nint pointer)
    {
        var declared = new List<string>();
        foreach (var button in Enum.GetValues<SDL.GamepadButton>())
        {
            if (button is SDL.GamepadButton.Invalid or SDL.GamepadButton.Count)
            {
                continue;
            }

            if (Safe(() => SDL.GamepadHasButton(pointer, button)))
            {
                declared.Add(Sdl3ControlNames.Key(button));
            }
        }

        return declared;
    }

    /// <summary>The declared standard axes, by mapping key - the same role as the buttons.</summary>
    private static List<string> ReadDeclaredAxes(nint pointer)
    {
        var declared = new List<string>();
        foreach (var axis in Enum.GetValues<SDL.GamepadAxis>())
        {
            if (axis is SDL.GamepadAxis.Invalid or SDL.GamepadAxis.Count)
            {
                continue;
            }

            if (Safe(() => SDL.GamepadHasAxis(pointer, axis)))
            {
                declared.Add(Sdl3ControlNames.Key(axis));
            }
        }

        return declared;
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
