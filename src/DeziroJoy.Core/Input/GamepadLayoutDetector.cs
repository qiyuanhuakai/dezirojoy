namespace DeziroJoy.Core.Input;

/// <summary>
/// Best-effort layout detection from the raw device name reported by the platform layer.
/// </summary>
public static class GamepadLayoutDetector
{
    private static readonly string[] XboxMarkers =
    [
        "xbox",
        "xinput",
        "x-box",
        "360",
        "microsoft",
    ];

    private static readonly string[] DualShockMarkers =
    [
        "playstation",
        "dualshock",
        "dualsense",
        "sony",
        "ps3",
        "ps4",
        "ps5",
    ];

    public static GamepadLayout Detect(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return GamepadLayout.Generic;
        }

        if (XboxMarkers.Any(marker => deviceName.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return GamepadLayout.Xbox;
        }

        if (DualShockMarkers.Any(marker => deviceName.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return GamepadLayout.DualShock;
        }

        return GamepadLayout.Generic;
    }
}
