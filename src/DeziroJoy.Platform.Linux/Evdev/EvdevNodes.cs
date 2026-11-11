namespace DeziroJoy.Platform.Linux.Evdev;

/// <summary>
/// Where the evdev nodes live and how they are listed. The provider used to probe
/// <c>event0..event31</c> with <see cref="File.Exists"/>, which silently missed everything past
/// event31 and could not tell "absent" from "unreadable"; enumerating the directory sees every
/// node the kernel actually created.
/// </summary>
internal static class EvdevNodes
{
    /// <summary>The kernel's evdev directory.</summary>
    public const string DirectoryPath = "/dev/input";

    public static bool DirectoryExists(string? directory = null)
        => Directory.Exists(directory ?? DirectoryPath);

    /// <summary>
    /// Every <c>event*</c> node under <paramref name="directory"/> in device-number order
    /// (event2 before event10), or empty when the directory is missing or unreadable.
    /// </summary>
    public static IReadOnlyList<string> Enumerate(string? directory = null)
    {
        var path = directory ?? DirectoryPath;

        try
        {
            if (!Directory.Exists(path))
            {
                return [];
            }

            return Order(Directory.GetFiles(path, "event*"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable /dev/input reports as "no nodes"; the permission guidance below
            // still points at the group fix, which is the usual cause.
            return [];
        }
    }

    /// <summary>
    /// Orders event nodes by their numeric suffix so logs and scans list them the way the
    /// kernel does; names without a trailing number sort last, stably.
    /// </summary>
    internal static IReadOnlyList<string> Order(IEnumerable<string> paths)
        => paths.OrderBy(DeviceNumber).ToList();

    private static int DeviceNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var digits = new string(name.SkipWhile(static character => !char.IsDigit(character)).ToArray());

        return int.TryParse(digits, out var number) ? number : int.MaxValue;
    }
}
