using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Linux.Keys;
using DeziroJoy.Platform.Linux.UInput;

namespace DeziroJoy.Platform.Linux.Output;

/// <summary>
/// Experimental Linux output sink: creates a virtual keyboard/mouse through /dev/uinput.
/// Kernel-level, so it works under both X11 and Wayland. Needs write access to
/// /dev/uinput (member of the "input" group, or the udev rule quoted by
/// <see cref="LinuxDeviceAccess.UdevRulesText"/>). When the node will not open the failure
/// message and <see cref="ProbeUnavailableReason"/> both spell the fix out - the old
/// NullOutputSink degradation was silent, which is how "mapping does nothing" shipped.
/// </summary>
public sealed class UInputOutputSink : IOutputSink
{
    /// <summary>The kernel's virtual-input device node.</summary>
    internal const string DevicePath = "/dev/uinput";

    private readonly int _fd;
    private bool _disposed;

    public UInputOutputSink(string deviceName = "DeziroJoy Virtual Gamepad")
    {
        _fd = UInputNative.Open(DevicePath, UInputNative.OWrOnly);
        if (_fd < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(BuildOpenFailure(error));
        }

        try
        {
            Configure(deviceName);
        }
        catch
        {
            UInputNative.Close(_fd);
            throw;
        }
    }

    /// <summary>
    /// Test seam that adopts an already-open descriptor instead of opening /dev/uinput, so a
    /// suite on a machine without the node can still drive the configuration path: the ioctls
    /// fail on any non-uinput fd (ENOTTY), which is the shape of the failure that shipped -
    /// the open succeeding says nothing about the ioctls, and a silent one degraded every
    /// injection to nothing. The caller keeps ownership of the fd on every outcome.
    /// </summary>
    internal UInputOutputSink(int fd, string deviceName)
    {
        _fd = fd;
        Configure(deviceName);
    }

    private unsafe void Configure(string deviceName)
    {
        EnableEventType(1); // EV_KEY
        EnableEventType(2); // EV_REL

        foreach (var key in Enum.GetValues<GamepadKey>())
        {
            var code = LinuxKeyCodes.ToEvdevCode(key);
            if (code != LinuxKeyCodes.Unmapped)
            {
                SetBit(UInputNative.UiSetKeyBit, code);
            }
        }

        SetBit(UInputNative.UiSetRelBit, 0); // REL_X
        SetBit(UInputNative.UiSetRelBit, 1); // REL_Y
        SetBit(UInputNative.UiSetRelBit, 8); // REL_WHEEL
        SetBit(UInputNative.UiSetRelBit, 6); // REL_HWHEEL

        var nameBytes = Encoding.UTF8.GetBytes(deviceName);
        var nameLength = Math.Min(nameBytes.Length, 79);

        var setup = new UinputSetup
        {
            Bustype = UInputNative.BusVirtual,
            Vendor = 0x0DE2,
            Product = 0x0001,
            Version = 1,
            FfEffectsMax = 0,
        };

        var pointer = &setup;
        for (var i = 0; i < nameLength; i++)
        {
            pointer->Name[i] = nameBytes[i];
        }

        EnsureOk(UInputNative.Ioctl(_fd, UInputNative.UiDevSetup, pointer), "UI_DEV_SETUP");
        EnsureOk(UInputNative.Ioctl(_fd, UInputNative.UiDevCreate, null), "UI_DEV_CREATE");
    }

    /// <summary>
    /// Opens /dev/uinput and closes it again without creating a virtual device, answering
    /// whether injection could work at all. The GUI owns no sink - the daemon injects - but a
    /// failed sink used to degrade to <see cref="NullOutputSink"/> with nothing visible, so the
    /// status area asks this instead of showing silence. Null means writable.
    /// </summary>
    public static string? ProbeUnavailableReason()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var fd = UInputNative.Open(DevicePath, UInputNative.OWrOnly);
        if (fd >= 0)
        {
            UInputNative.Close(fd);
            return null;
        }

        return BuildOpenFailure(Marshal.GetLastPInvokeError());
    }

    /// <summary>
    /// The one wording for "uinput will not open". It quotes the same group command and udev
    /// rule the evdev diagnostic uses, so a user who reads either surface gets the same fix.
    /// </summary>
    internal static string BuildOpenFailure(int errno)
        => $"无法打开 /dev/uinput（errno {errno}）：请确认 uinput 模块已加载，且当前用户有写权限；"
            + $"可执行 {LinuxDeviceAccess.InputGroupCommand} 后重新登录，"
            + $"或添加 udev 规则 {LinuxDeviceAccess.UdevRulesPath}：{LinuxDeviceAccess.UdevRulesText}";

    public void KeyDown(GamepadKey key) => EmitKey(LinuxKeyCodes.ToEvdevCode(key), 1);

    public void KeyUp(GamepadKey key) => EmitKey(LinuxKeyCodes.ToEvdevCode(key), 0);

    public void MouseMove(int dx, int dy)
    {
        if (dx != 0)
        {
            Emit(EvdevCodes.EvRel, 0, dx);
        }

        if (dy != 0)
        {
            Emit(EvdevCodes.EvRel, 1, dy);
        }

        Syn();
    }

    public void ScrollWheel(int horizontalNotches, int verticalNotches)
    {
        if (verticalNotches != 0)
        {
            Emit(EvdevCodes.EvRel, 8, verticalNotches);
        }

        if (horizontalNotches != 0)
        {
            Emit(EvdevCodes.EvRel, 6, horizontalNotches);
        }

        Syn();
    }

    public void MouseButtonDown(MouseButton button)
    {
        if (EvdevMouseButtons.TryGetButtonCode(button, out var code))
        {
            Emit(EvdevCodes.EvKey, code, 1);
            Syn();
        }
    }

    public void MouseButtonUp(MouseButton button)
    {
        if (EvdevMouseButtons.TryGetButtonCode(button, out var code))
        {
            Emit(EvdevCodes.EvKey, code, 0);
            Syn();
        }
    }

    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UInputNative.Ioctl(_fd, UInputNative.UiDevDestroy, null);
        UInputNative.Close(_fd);
    }

    private void EnableEventType(int eventType) => SetBit(UInputNative.UiSetEvBit, (ushort)eventType);

    private unsafe void SetBit(uint request, ushort code)
        => EnsureOk(UInputNative.Ioctl(_fd, request, (void*)code), $"ioctl 0x{request:X8}");

    private void EmitKey(ushort code, int value)
    {
        if (code == LinuxKeyCodes.Unmapped)
        {
            return;
        }

        Emit(EvdevCodes.EvKey, code, value);
        Syn();
    }

    private unsafe void Emit(ushort type, ushort code, int value)
    {
        Span<byte> buffer = stackalloc byte[EvdevNative.EventSize];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[16..], type);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[18..], code);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[20..], value);

        fixed (byte* pointer = buffer)
        {
            UInputNative.Write(_fd, pointer, (nuint)buffer.Length);
        }
    }

    private void Syn() => Emit(EvdevCodes.EvSyn, 0, 0);

    private static void EnsureOk(int result, string operation)
    {
        if (result < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"{operation} 失败 (errno {error})。");
        }
    }
}

internal static class EvdevMouseButtons
{
    public static bool TryGetButtonCode(MouseButton button, out ushort code)
    {
        code = button switch
        {
            MouseButton.Left => EvdevCodes.BtnLeft,
            MouseButton.Right => EvdevCodes.BtnRight,
            MouseButton.Middle => EvdevCodes.BtnMiddle,
            MouseButton.XButton1 => EvdevCodes.BtnSide,
            MouseButton.XButton2 => EvdevCodes.BtnExtra,
            _ => (ushort)0,
        };

        return code != 0;
    }
}
