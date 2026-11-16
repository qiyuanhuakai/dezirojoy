using DeziroJoy.Platform.Sdl3;
using SDL3;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// Scriptable stand-in for the SDL runtime. Devices, their live button/axis state and the
/// queued lifecycle events are plain test-owned data, so enumeration, hot-plug, remap,
/// failure and cleanup paths run deterministically - no native library, no gamepad. The
/// provider under test cannot tell the difference: every SDL call goes through
/// <see cref="ISdl3Api"/>.
/// </summary>
internal sealed class FakeSdl3Api : ISdl3Api
{
    /// <summary>One scripted gamepad: its reported info plus its live input state.</summary>
    public sealed class FakeDevice
    {
        public required uint InstanceId { get; init; }

        public required Sdl3GamepadInfo Info { get; set; }

        public required string Name { get; init; }

        public required string GuidString { get; init; }

        public required string Serial { get; init; }

        public required string Path { get; init; }

        /// <summary>Buttons currently held, by SDL button ordinal.</summary>
        public HashSet<SDL.GamepadButton> Pressed { get; } = [];

        /// <summary>Axis values by SDL axis ordinal; sticks default to centered.</summary>
        public short[] Axes { get; } = new short[(int)SDL.GamepadAxis.Count];

        /// <summary>True while SDL still enumerates this device.</summary>
        public bool Enumerated { get; set; } = true;
    }

    /// <summary>One scripted plain joystick: its reported info plus its live raw state.</summary>
    public sealed class FakeJoystick
    {
        public required uint InstanceId { get; init; }

        public required string Name { get; init; }

        public required string GuidString { get; init; }

        public ushort Vendor { get; set; } = 0x0079;

        public ushort Product { get; set; } = 0x0006;

        public ushort ProductVersion { get; set; } = 0x0100;

        public string Serial { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string TypeName { get; set; } = "Unknown";

        /// <summary>Live pressed state per raw button index.</summary>
        public List<bool> Buttons { get; } = [];

        /// <summary>Live value per raw axis index.</summary>
        public List<short> Axes { get; } = [];

        /// <summary>Live SDL hat mask per hat index.</summary>
        public List<byte> Hats { get; } = [];

        /// <summary>True while SDL still enumerates this device.</summary>
        public bool Enumerated { get; set; } = true;

        /// <summary>Builds a joystick with the given raw control counts, all at rest.</summary>
        public static FakeJoystick Make(
            uint instanceId,
            int buttons,
            int axes,
            int hats,
            string name = "Generic USB Joystick",
            string guidString = "03000000790000000006000000000000")
        {
            var joystick = new FakeJoystick
            {
                InstanceId = instanceId,
                Name = name,
                GuidString = guidString,
            };

            joystick.Buttons.AddRange(Enumerable.Repeat(false, buttons));
            joystick.Axes.AddRange(Enumerable.Repeat((short)0, axes));
            joystick.Hats.AddRange(Enumerable.Repeat((byte)0, hats));
            return joystick;
        }

        public void Press(int button) => Buttons[button] = true;

        public void Release(int button) => Buttons[button] = false;

        public void Move(int axis, short value) => Axes[axis] = value;

        public void Turn(int hat, byte mask) => Hats[hat] = mask;
    }

    private readonly List<Sdl3GamepadHandle> _openHandles = [];
    private readonly Dictionary<Sdl3GamepadHandle, FakeDevice> _handleDevices = [];
    private readonly Dictionary<Sdl3JoystickHandle, FakeJoystick> _joystickHandles = [];
    private readonly List<Sdl3GamepadEvent> _queuedEvents = [];
    private int _nextPointer = 0x1000;

    /// <summary>Devices SDL currently knows about, in enumeration order.</summary>
    public List<FakeDevice> Devices { get; } = [];

    /// <summary>
    /// Plain joysticks SDL currently knows about, in enumeration order. A joystick that is also
    /// in <see cref="Devices"/> is one SDL reads as a standard gamepad.
    /// </summary>
    public List<FakeJoystick> Joysticks { get; } = [];

    /// <summary>Lifecycle events that the next <see cref="PumpEvents"/> delivers.</summary>
    public List<Sdl3GamepadEvent> QueuedEvents => _queuedEvents;

    /// <summary>When false, initialization fails with <see cref="InitError"/>.</summary>
    public bool InitSucceeds { get; set; } = true;

    public string InitError { get; set; } = "SDL_Init 失败（测试）";

    public Sdl3RuntimeInfo Runtime { get; set; } = new(Version: 3004020, Revision: "fake-revision");

    /// <summary>When set, the next open of this instance id fails with this text.</summary>
    public Dictionary<uint, string> OpenFailures { get; } = [];

    public int UpdateGamepadsCalls { get; private set; }

    /// <summary>Times the event queue was drained - once per tick, like the real pump.</summary>
    public int PumpEventsCalls { get; private set; }

    /// <summary>
    /// Times SDL was asked for its gamepad list. Only the throttled scan enumerates, so
    /// this is the observable that distinguishes "state refreshed" from "devices
    /// re-enumerated".
    /// </summary>
    public int GetGamepadIdsCalls { get; private set; }

    public int ShutdownCalls { get; private set; }

    /// <summary>Handles opened so far, in order.</summary>
    public IReadOnlyList<Sdl3GamepadHandle> OpenedHandles => _openHandles;

    /// <summary>Handles closed so far, in order.</summary>
    public List<Sdl3GamepadHandle> ClosedHandles { get; } = [];

    /// <summary>Builds a device with a complete, valid info record; tests override what matters.</summary>
    public static FakeDevice MakeDevice(
        uint instanceId,
        string name = "测试手柄",
        string guidString = "03000000000000000000000000000000",
        string serial = "",
        string path = "",
        int type = 3,
        int declaredButtonCount = 15,
        IReadOnlyList<string>? declaredButtons = null,
        IReadOnlyList<string>? declaredAxes = null,
        IReadOnlyList<string>? faceButtonLabels = null)
    {
        // A standard pad by default: the fifteen standard buttons and all six axes declared.
        // A test that wants a narrower - or richer - declaration says so, and the control
        // description follows the declaration rather than the device name.
        var buttons = declaredButtons ?? DefaultButtonKeys;
        var axes = declaredAxes ?? StandardAxisKeys;

        var info = new Sdl3GamepadInfo
        {
            InstanceId = instanceId,
            Name = name,
            GuidString = guidString,
            Vendor = 0x045e,
            Product = 0x0b20,
            ProductVersion = 0x0500,
            Serial = serial,
            Path = path,
            Type = type,
            TypeName = "XboxOne",
            Mapping = "test-mapping",
            MappingSource = "sdl-database",
            PlayerIndex = -1,
            ConnectionState = "Wireless",
            TouchpadCount = 0,
            TouchpadFingerCounts = [],
            Sensors = [],
            DeclaredButtons = buttons,
            DeclaredButtonCount = declaredButtonCount,
            DeclaredAxes = axes,
            FaceButtonLabels = faceButtonLabels ?? ["A", "B", "X", "Y"],
        };

        return new FakeDevice
        {
            InstanceId = instanceId,
            Info = info,
            Name = name,
            GuidString = guidString,
            Serial = serial,
            Path = path,
        };
    }

    /// <summary>The fifteen standard buttons a plain Xbox-layout pad declares.</summary>
    public static readonly string[] DefaultButtonKeys =
    [
        "a", "b", "x", "y", "back", "guide", "start",
        "leftstick", "rightstick", "leftshoulder", "rightshoulder",
        "dpup", "dpdown", "dpleft", "dpright",
    ];

    /// <summary>Every standard SDL button key, including the positionless cluster.</summary>
    public static readonly string[] AllStandardButtonKeys =
    [
        .. DefaultButtonKeys,
        "misc1", "misc2", "misc3", "misc4", "misc5", "misc6",
        "paddle1", "paddle2", "paddle3", "paddle4", "touchpad",
    ];

    /// <summary>Every standard SDL axis key, in SDL's own order.</summary>
    public static readonly string[] StandardAxisKeys =
        ["leftx", "lefty", "rightx", "righty", "lefttrigger", "righttrigger"];

    public FakeDevice AddDevice(FakeDevice device)
    {
        Devices.Add(device);
        return device;
    }

    public FakeJoystick AddJoystick(FakeJoystick joystick)
    {
        Joysticks.Add(joystick);
        return joystick;
    }

    public void Queue(Sdl3GamepadEventType type, uint instanceId) => _queuedEvents.Add(new Sdl3GamepadEvent(type, instanceId));

    /// <summary>Mappings handed to SDL so far, in order - the observable for "loaded before enumerating".</summary>
    public List<string> AddedMappings { get; } = [];

    /// <summary>When false, <see cref="TryAddGamepadMapping"/> rejects with <see cref="MappingRejection"/>.</summary>
    public bool MappingAccepted { get; set; } = true;

    public string MappingRejection { get; set; } = "映射格式非法（测试）";

    /// <summary>What <see cref="GetMappingForGuid"/> answers, keyed by GUID.</summary>
    public Dictionary<string, string> RegisteredMappings { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Joystick opens that failed, by instance id, with SDL's own error text.</summary>
    public Dictionary<uint, string> JoystickOpenFailures { get; } = [];

    public Sdl3RuntimeInfo? GetRuntimeInfo() => Runtime;

    public bool TryEnsureInitialized(out string? error)
    {
        error = InitSucceeds ? null : InitError;
        return InitSucceeds;
    }

    public void Shutdown() => ShutdownCalls++;

    public void UpdateGamepads() => UpdateGamepadsCalls++;

    public IReadOnlyList<Sdl3GamepadEvent> PumpEvents()
    {
        PumpEventsCalls++;
        var drained = _queuedEvents.ToArray();
        _queuedEvents.Clear();
        return drained;
    }

    public IReadOnlyList<uint> GetGamepadIds()
    {
        GetGamepadIdsCalls++;
        return Devices.Where(device => device.Enumerated).Select(device => device.InstanceId).ToArray();
    }

    public Sdl3GamepadHandle? OpenGamepad(uint instanceId, out string? error)
    {
        error = null;

        if (OpenFailures.TryGetValue(instanceId, out var failure))
        {
            error = failure;
            return null;
        }

        var device = Devices.FirstOrDefault(d => d.InstanceId == instanceId && d.Enumerated);
        if (device is null)
        {
            error = $"设备 #{instanceId} 不存在";
            return null;
        }

        var handle = new Sdl3GamepadHandle(_nextPointer++);
        _openHandles.Add(handle);
        _handleDevices[handle] = device;
        return handle;
    }

    public void CloseGamepad(Sdl3GamepadHandle handle)
    {
        ClosedHandles.Add(handle);
        _handleDevices.Remove(handle);
    }

    public Sdl3GamepadInfo ReadInfo(Sdl3GamepadHandle handle)
    {
        if (!_handleDevices.TryGetValue(handle, out var device))
        {
            throw new InvalidOperationException("读取了未打开的手柄");
        }

        // Re-derive the info from the device's current fields so a test that mutates the
        // scripted device after open sees the change on the next ReadInfo (remap).
        return device.Info with
        {
            InstanceId = device.InstanceId,
            Name = device.Name,
            GuidString = device.GuidString,
            Serial = device.Serial,
            Path = device.Path,
        };
    }

    public bool GetButton(Sdl3GamepadHandle handle, SDL.GamepadButton button)
        => _handleDevices.TryGetValue(handle, out var device) && device.Pressed.Contains(button);

    public short GetAxis(Sdl3GamepadHandle handle, SDL.GamepadAxis axis)
        => _handleDevices.TryGetValue(handle, out var device) ? device.Axes[(int)axis] : (short)0;

    public bool TryAddGamepadMapping(string mapping, out string? error)
    {
        AddedMappings.Add(mapping);
        error = MappingAccepted ? null : MappingRejection;
        return MappingAccepted;
    }

    public string? GetMappingForGuid(string guidString)
        => RegisteredMappings.TryGetValue(guidString, out var mapping) ? mapping : null;

    public IReadOnlyList<Sdl3JoystickInfo> GetJoysticks()
    {
        var gamepadIds = Devices.Where(device => device.Enumerated).Select(device => device.InstanceId).ToHashSet();
        return Joysticks
            .Where(joystick => joystick.Enumerated)
            .Select(joystick => new Sdl3JoystickInfo
            {
                InstanceId = joystick.InstanceId,
                Name = joystick.Name,
                GuidString = joystick.GuidString,
                Vendor = joystick.Vendor,
                Product = joystick.Product,
                ProductVersion = joystick.ProductVersion,
                Serial = joystick.Serial,
                Path = joystick.Path,
                ButtonCount = joystick.Buttons.Count,
                AxisCount = joystick.Axes.Count,
                HatCount = joystick.Hats.Count,
                TypeName = joystick.TypeName,
                HasStandardMapping = gamepadIds.Contains(joystick.InstanceId),
            })
            .ToList();
    }

    public Sdl3JoystickHandle? OpenJoystick(uint instanceId, out string? error)
    {
        error = null;

        if (JoystickOpenFailures.TryGetValue(instanceId, out var failure))
        {
            error = failure;
            return null;
        }

        var joystick = Joysticks.FirstOrDefault(item => item.InstanceId == instanceId && item.Enumerated);
        if (joystick is null)
        {
            error = $"Joystick #{instanceId} 不存在";
            return null;
        }

        var handle = new Sdl3JoystickHandle(_nextPointer++);
        _joystickHandles[handle] = joystick;
        return handle;
    }

    public void CloseJoystick(Sdl3JoystickHandle handle)
    {
        // The same guard the real api has: closing an already-closed handle is a no-op.
        if (handle.IsClosed)
        {
            return;
        }

        ClosedJoystickHandles.Add(handle);
        _joystickHandles.Remove(handle);
        handle.MarkClosed();
    }

    public Sdl3JoystickSnapshot ReadJoystick(Sdl3JoystickHandle handle)
        => _joystickHandles.TryGetValue(handle, out var joystick)
            ? new Sdl3JoystickSnapshot(joystick.Buttons, joystick.Axes, joystick.Hats)
            : Sdl3JoystickSnapshot.Empty;

    /// <summary>Joystick handles closed so far, in order.</summary>
    public List<Sdl3JoystickHandle> ClosedJoystickHandles { get; } = [];

    /// <summary>Counts how many times one joystick handle was closed - idempotency assertions.</summary>
    public int JoystickCloseCount(Sdl3JoystickHandle handle)
        => ClosedJoystickHandles.Count(item => ReferenceEquals(item, handle));

    /// <summary>Counts how many times one handle was closed - idempotency assertions.</summary>
    public int CloseCount(Sdl3GamepadHandle handle) => ClosedHandles.Count(h => ReferenceEquals(h, handle));
}
