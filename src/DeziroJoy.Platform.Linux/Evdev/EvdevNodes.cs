namespace DeziroJoy.Platform.Linux.Evdev;

/// <summary>
/// The result of listing an evdev directory. A listing that could not complete carries the
/// exception instead of an empty node list, because "I could not look" and "there is nothing
/// there" need opposite fixes and only one of them may retire the devices already being
/// served: a scan that reads a failed listing as "nothing is plugged in" stops every reader
/// and drops every connected pad on one transient failure.
/// </summary>
/// <param name="Nodes">The event nodes found, in device-number order. Empty on failure.</param>
/// <param name="Failure">Why the listing could not complete, or null when it did.</param>
internal readonly record struct EvdevEnumeration(IReadOnlyList<string> Nodes, Exception? Failure)
{
    public bool Failed => Failure is not null;
}

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
    /// (event2 before event10). A missing directory is an empty, successful listing - that is
    /// the "no input subsystem" state, not a failure. A directory that exists but cannot be
    /// listed is a failed listing carrying the exception, never an empty one.
    /// </summary>
    public static EvdevEnumeration Enumerate(string? directory = null)
    {
        var path = directory ?? DirectoryPath;

        try
        {
            if (!Directory.Exists(path))
            {
                return new EvdevEnumeration([], null);
            }

            return new EvdevEnumeration(Order(Directory.GetFiles(path, "event*")), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The failure this used to swallow into an empty list: a scan reading that as
            // "nothing is plugged in" retired every device it was serving. The one-shot probe
            // still reports such a directory as "no nodes" - it owns no devices to lose - but
            // the scan takes the failure-aware result and keeps what it has.
            return new EvdevEnumeration([], exception);
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
