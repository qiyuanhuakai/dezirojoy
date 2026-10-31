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
/// /dev/uinput (root, or a udev rule such as:
/// KERNEL=="uinput", MODE="0660", GROUP="input", OPTIONS+="static_node=uinput").
/// </summary>
public sealed class UInputOutputSink : IOutputSink
{
    private readonly int _fd;
    private bool _disposed;

    public unsafe UInputOutputSink(string deviceName = "DeziroJoy Virtual Gamepad")
    {
        _fd = UInputNative.Open("/dev/uinput", UInputNative.OWrOnly);
        if (_fd < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"无法打开 /dev/uinput (errno {error})，请确认 uinput 模块已加载且当前用户有写权限。");
        }

        try
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
        catch
        {
            UInputNative.Close(_fd);
            throw;
        }
    }

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
