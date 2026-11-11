using System.Collections.Concurrent;
using DeziroJoy.Core.Input;

namespace DeziroJoy.Platform.Windows.XInput;

/// <summary>
/// Normalization from the XInput gamepad struct to <see cref="GamepadState"/>. Stick axes
/// divide by 32768 so -32768..32767 maps to -1..~1; triggers are digital through a Schmitt
/// trigger keyed per device - press at or above 140/255 of travel, release at or below
/// 115/255, and hold the previous state inside the band - so a trigger resting near half
/// travel keeps one pressed state instead of chattering the visualizer highlight and the
/// edge-driven mapping engine.
/// </summary>
internal static class XInputStateMapper
{
    /// <summary>Press point of the trigger Schmitt band, as a fraction of the 0..255 travel.</summary>
    private const float TriggerPressPoint = 140f / 255f;

    /// <summary>Release point of the trigger Schmitt band, as a fraction of the 0..255 travel.</summary>
    private const float TriggerReleasePoint = 115f / 255f;

    /// <summary>
    /// Per-device trigger hysteresis. The engine polls every connected pad once per tick on
    /// one thread, so mapper calls interleave; shared state would let one pad's rest value
    /// reset another pad's held trigger. XInput device ids are "xinput:0".."xinput:3", so
    /// the registry stays bounded; the empty key is the shared entry for callers that do
    /// not track devices.
    /// </summary>
    private static readonly ConcurrentDictionary<string, TriggerHysteresis> TriggerStates = new(StringComparer.Ordinal);

    /// <summary>
    /// Normalizes one XInput sample into a <see cref="GamepadState"/>.
    /// </summary>
    /// <param name="pad">The raw gamepad struct read from one XInput slot.</param>
    /// <param name="deviceId">
    /// Stable device identity ("xinput:0".."xinput:3"). The trigger hysteresis is keyed by
    /// it so each pad keeps its own state; omit it only when the caller polls a single
    /// device, because every id-less call then shares one entry.
    /// </param>
    public static GamepadState ToGamepadState(in XInputGamepad pad, string? deviceId = null)
    {
        var buttons = GamepadButtonMask.None;

        if ((pad.Buttons & XInputNative.DpadUp) != 0)
        {
            buttons |= GamepadButtonMask.DPadUp;
        }

        if ((pad.Buttons & XInputNative.DpadDown) != 0)
        {
            buttons |= GamepadButtonMask.DPadDown;
        }

        if ((pad.Buttons & XInputNative.DpadLeft) != 0)
        {
            buttons |= GamepadButtonMask.DPadLeft;
        }

        if ((pad.Buttons & XInputNative.DpadRight) != 0)
        {
            buttons |= GamepadButtonMask.DPadRight;
        }

        if ((pad.Buttons & XInputNative.Start) != 0)
        {
            buttons |= GamepadButtonMask.Start;
        }

        if ((pad.Buttons & XInputNative.Back) != 0)
        {
            buttons |= GamepadButtonMask.Back;
        }

        if ((pad.Buttons & XInputNative.LeftThumb) != 0)
        {
            buttons |= GamepadButtonMask.LeftStick;
        }

        if ((pad.Buttons & XInputNative.RightThumb) != 0)
        {
            buttons |= GamepadButtonMask.RightStick;
        }

        if ((pad.Buttons & XInputNative.LeftShoulder) != 0)
        {
            buttons |= GamepadButtonMask.LeftBumper;
        }

        if ((pad.Buttons & XInputNative.RightShoulder) != 0)
        {
            buttons |= GamepadButtonMask.RightBumper;
        }

        // Dead on real XInput hardware: wButtons has no 0x0400 bit (see XInputNative.Guide),
        // so this branch never fires on Windows - the Guide button is an XInput platform
        // limitation (M53), which is why the visualizer's Guide stays dark there. Kept for
        // bit alignment with the other buttons and so a synthetic bitmask still round-trips;
        // real Windows Guide support would need the Raw Input HID path (backlog).
        if ((pad.Buttons & XInputNative.Guide) != 0)
        {
            buttons |= GamepadButtonMask.Guide;
        }

        if ((pad.Buttons & XInputNative.A) != 0)
        {
            buttons |= GamepadButtonMask.A;
        }

        if ((pad.Buttons & XInputNative.B) != 0)
        {
            buttons |= GamepadButtonMask.B;
        }

        if ((pad.Buttons & XInputNative.X) != 0)
        {
            buttons |= GamepadButtonMask.X;
        }

        if ((pad.Buttons & XInputNative.Y) != 0)
        {
            buttons |= GamepadButtonMask.Y;
        }

        var triggers = TriggerStates.GetOrAdd(deviceId ?? string.Empty, static _ => new TriggerHysteresis());

        triggers.LeftPressed = UpdateTrigger(pad.LeftTrigger, triggers.LeftPressed);
        if (triggers.LeftPressed)
        {
            buttons |= GamepadButtonMask.LeftTrigger;
        }

        triggers.RightPressed = UpdateTrigger(pad.RightTrigger, triggers.RightPressed);
        if (triggers.RightPressed)
        {
            buttons |= GamepadButtonMask.RightTrigger;
        }

        return GamepadState.Create(
            buttons,
            pad.ThumbLX / 32768f,
            pad.ThumbLY / 32768f,
            pad.ThumbRX / 32768f,
            pad.ThumbRY / 32768f);
    }

    /// <summary>
    /// Schmitt trigger for the analog triggers: the trigger presses at or above
    /// <see cref="TriggerPressPoint"/> of the 0..255 travel and releases at or below
    /// <see cref="TriggerReleasePoint"/>; inside the band the previous state is kept, so
    /// noise around the midpoint cannot flap the pressed flag. A single threshold
    /// (<see cref="XInputNative.TriggerPressedThreshold"/>) let 126/128 chatter flip the
    /// flag 9 times in 10 samples (M29), flickering the visualizer highlight and injecting
    /// BeginHold + EndHold pairs at up to 500 Hz, because the mapping engine is
    /// edge-driven. LB/RB arrive as digital buttons and never pass through here.
    /// </summary>
    private static bool UpdateTrigger(byte value, bool pressed)
    {
        var travel = value / 255f;

        if (travel >= TriggerPressPoint)
        {
            pressed = true;
        }
        else if (travel <= TriggerReleasePoint)
        {
            pressed = false;
        }

        return pressed;
    }

    /// <summary>Per-device Schmitt state for the two analog triggers.</summary>
    private sealed class TriggerHysteresis
    {
        public bool LeftPressed { get; set; }

        public bool RightPressed { get; set; }
    }
}
