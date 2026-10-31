using System.Buffers.Binary;

namespace DeziroJoy.Platform.Linux.Evdev;

/// <summary>
/// Decodes the fixed 24-byte evdev input_event record:
/// timeval (16 bytes) + type (u16) + code (u16) + value (i32).
/// </summary>
public static class EvdevEventReader
{
    public const int EventSize = 24;

    public static EvdevEvent Read(ReadOnlySpan<byte> buffer, int index)
    {
        var offset = index * EventSize;

        return new EvdevEvent(
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 16)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 18)..]),
            BinaryPrimitives.ReadInt32LittleEndian(buffer[(offset + 20)..]));
    }
}
