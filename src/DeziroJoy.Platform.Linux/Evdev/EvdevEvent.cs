namespace DeziroJoy.Platform.Linux.Evdev;

/// <summary>One decoded evdev event (input_event without the timestamp).</summary>
public readonly record struct EvdevEvent(ushort Type, ushort Code, int Value);

/// <summary>Reported calibration range of one absolute axis.</summary>
public readonly record struct EvdevAxisRange(int Min, int Max)
{
    public int Midpoint => (Min + Max) / 2;
}
