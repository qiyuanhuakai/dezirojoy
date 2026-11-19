using System.Text.RegularExpressions;

namespace DeziroJoy.Platform.Sdl3;

/// <summary>Chooses one useful gamepad interface from a physical composite HID device.</summary>
internal static class Sdl3CompositeDevice
{
    private static readonly Regex CollectionPath = new(
        @"&COL[0-9]+#(?<instance>[^#]+)#[^#]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<uint> SelectGamepadIds(
        IReadOnlyList<uint> gamepadIds,
        IReadOnlyList<Sdl3JoystickInfo> joysticks)
    {
        var selected = new HashSet<uint>(gamepadIds);
        var groups = joysticks
            .Select(joystick => (Joystick: joystick, Group: PhysicalGroup(joystick.Path)))
            .Where(item => item.Group is not null)
            .GroupBy(item => item.Group!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var candidates = group
                .Select(item => item.Joystick)
                .Where(joystick => selected.Contains(joystick.InstanceId))
                .ToList();
            if (candidates.Count < 2)
            {
                continue;
            }

            var order = gamepadIds
                .Select((instanceId, index) => (instanceId, index))
                .ToDictionary(item => item.instanceId, item => item.index);
            var winner = candidates
                .OrderByDescending(joystick => joystick.AxisCount + joystick.HatCount)
                .ThenByDescending(joystick => joystick.ButtonCount)
                .ThenBy(joystick => order[joystick.InstanceId])
                .First()
                .InstanceId;
            foreach (var joystick in candidates)
            {
                if (joystick.InstanceId != winner)
                {
                    selected.Remove(joystick.InstanceId);
                }
            }
        }

        return gamepadIds.Where(selected.Contains).ToList();
    }

    private static string? PhysicalGroup(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var match = CollectionPath.Match(path);
        if (!match.Success)
        {
            return null;
        }

        var instance = match.Groups["instance"].Value;
        var separator = instance.LastIndexOf('&');
        if (separator <= 0)
        {
            return null;
        }

        // The collection-independent HID instance identifies the physical unit. Two units of
        // the same model have different instances; COL01/COL02 on one unit share this value.
        return $"{path[..match.Index]}#{instance[..separator]}";
    }
}
