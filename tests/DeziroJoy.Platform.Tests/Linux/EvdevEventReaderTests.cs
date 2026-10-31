using DeziroJoy.Platform.Linux.Evdev;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

public class EvdevEventReaderTests
{
    [Fact]
    public void Reads_Type_Code_And_Value_From_The_Fixed_24_Byte_Record()
    {
        Span<byte> buffer = new byte[EvdevEventReader.EventSize];

        buffer[16] = 0x01; // EV_KEY
        buffer[17] = 0x00;
        buffer[18] = 0x30; // BTN_SOUTH
        buffer[19] = 0x01;
        buffer[20] = 0x01; // pressed
        buffer[21] = 0x00;
        buffer[22] = 0x00;
        buffer[23] = 0x00;

        var evdevEvent = EvdevEventReader.Read(buffer, 0);

        Assert.Equal(EvdevCodes.EvKey, evdevEvent.Type);
        Assert.Equal(EvdevCodes.BtnSouth, evdevEvent.Code);
        Assert.Equal(1, evdevEvent.Value);
    }

    [Fact]
    public void Skips_The_16_Byte_Timestamp_Prefix()
    {
        Span<byte> buffer = new byte[EvdevEventReader.EventSize * 2];

        // garbage timestamp in the first 16 bytes must be ignored
        for (var i = 0; i < 16; i++)
        {
            buffer[i] = 0xFF;
        }

        buffer[16 + EvdevEventReader.EventSize] = 0x03; // EV_ABS in the second record
        buffer[20 + EvdevEventReader.EventSize] = 0x2A;

        var evdevEvent = EvdevEventReader.Read(buffer, 1);

        Assert.Equal(EvdevCodes.EvAbs, evdevEvent.Type);
        Assert.Equal(0x2A, evdevEvent.Value);
    }

    [Fact]
    public void Negative_Values_Roundtrip()
    {
        Span<byte> buffer = new byte[EvdevEventReader.EventSize];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer[20..], -12345);

        var evdevEvent = EvdevEventReader.Read(buffer, 0);

        Assert.Equal(-12345, evdevEvent.Value);
    }
}
