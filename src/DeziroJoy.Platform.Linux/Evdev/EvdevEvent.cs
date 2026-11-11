namespace DeziroJoy.Platform.Linux.Evdev;

/// <summary>One decoded evdev event (input_event without the timestamp).</summary>
public readonly record struct EvdevEvent(ushort Type, ushort Code, int Value);

/// <summary>
/// Reported calibration of one absolute axis, as EVIOCGABS (input_absinfo) answered it.
/// <paramref name="Rest"/> is the device's current value at open time: the tracker seeds the
/// axis's initial state from it instead of waiting for the first event, and a trigger whose
/// rest value sits near <paramref name="Max"/> is an inverted axis. Callers with no device
/// data leave it at 0, which is the center of the canonical stick range and the rest end of
/// a 0..255 trigger.
/// </summary>
public readonly record struct EvdevAxisRange(int Min, int Max, int Rest = 0)
{
    public int Midpoint => (Min + Max) / 2;
}
