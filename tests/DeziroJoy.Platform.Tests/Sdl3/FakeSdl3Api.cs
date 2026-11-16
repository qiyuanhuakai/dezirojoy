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

    private readonly List<Sdl3GamepadHandle> _openHandles = [];
    private readonly Dictionary<Sdl3GamepadHandle, FakeDevice> _handleDevices = [];
    private readonly List<Sdl3GamepadEvent> _queuedEvents = [];
    private int _nextPointer = 0x1000;

    /// <summary>Devices SDL currently knows about, in enumeration order.</summary>
    public List<FakeDevice> Devices { get; } = [];

    /// <summary>Lifecycle events that the next <see cref="PumpEvents"/> delivers.</summary>
    public List<Sdl3GamepadEvent> QueuedEvents => _queuedEvents;

    /// <summary>When false, initialization fails with <see cref="InitError"/>.</summary>
    public bool InitSucceeds { get; set; } = true;

    public string InitError { get; set; } = "SDL_Init 失败（测试）";

    public Sdl3RuntimeInfo Runtime { get; set; } = new(Version: 3004020, Revision: "fake-revision");

    /// <summary>When set, the next open of this instance id fails with this text.</summary>
    public Dictionary<uint, string> OpenFailures { get; } = [];

    public int UpdateGamepadsCalls { get; private set; }

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
        int declaredButtonCount = 15)
    {
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
            DeclaredButtonCount = declaredButtonCount,
            FaceButtonLabels = ["A", "B", "X", "Y"],
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

    public FakeDevice AddDevice(FakeDevice device)
    {
        Devices.Add(device);
        return device;
    }

    public void Queue(Sdl3GamepadEventType type, uint instanceId) => _queuedEvents.Add(new Sdl3GamepadEvent(type, instanceId));

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
        var drained = _queuedEvents.ToArray();
        _queuedEvents.Clear();
        return drained;
    }

    public IReadOnlyList<uint> GetGamepadIds()
        => Devices.Where(device => device.Enumerated).Select(device => device.InstanceId).ToArray();

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

    /// <summary>Counts how many times one handle was closed - idempotency assertions.</summary>
    public int CloseCount(Sdl3GamepadHandle handle) => ClosedHandles.Count(h => ReferenceEquals(h, handle));
}
