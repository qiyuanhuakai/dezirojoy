using DeziroJoy.Platform.Linux;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Linux.Output;

namespace DeziroJoy.App.Services;

public sealed record DeviceAccessSnapshot(
    int GamepadCount,
    int DeniedNodeCount,
    string? UInputUnavailableReason,
    bool UdevRulesInstalled,
    bool UserInInputGroup,
    bool SessionInInputGroup,
    bool CanFixAutomatically);

public interface IDeviceAccessProbe
{
    DeviceAccessSnapshot Probe();
}

public sealed class LinuxDeviceAccessProbe : IDeviceAccessProbe
{
    private readonly Func<EvdevScanDiagnostic> _evdevProbe;
    private readonly Func<string?> _uinputProbe;
    private readonly Func<string> _helperPath;
    private readonly Func<string, string?> _readText;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string?> _path;
    private readonly string _userName;

    public LinuxDeviceAccessProbe()
        : this(
            static () => EvdevGamepadProvider.ProbeOnce(),
            static () => UInputOutputSink.ProbeUnavailableReason(),
            static () => LinuxDeviceAccess.FixHelperPath,
            static path => File.Exists(path) ? File.ReadAllText(path) : null,
            static path => File.Exists(path),
            static () => Environment.GetEnvironmentVariable("PATH"),
            Environment.UserName)
    {
    }

    internal LinuxDeviceAccessProbe(
        Func<EvdevScanDiagnostic> evdevProbe,
        Func<string?> uinputProbe,
        Func<string> helperPath,
        Func<string, string?> readText,
        Func<string, bool> fileExists,
        Func<string?> path,
        string userName)
    {
        _evdevProbe = evdevProbe;
        _uinputProbe = uinputProbe;
        _helperPath = helperPath;
        _readText = readText;
        _fileExists = fileExists;
        _path = path;
        _userName = userName;
    }

    public DeviceAccessSnapshot Probe()
    {
        var evdev = _evdevProbe();
        var group = ReadInputGroup();
        return new DeviceAccessSnapshot(
            evdev.GamepadCount,
            evdev.DeniedNodeCount,
            _uinputProbe(),
            RulesInstalled(),
            group.Persistent,
            group.Session,
            _fileExists(_helperPath()) && FindExecutable("pkexec") is not null);
    }

    private bool RulesInstalled()
        => string.Equals(
            _readText(LinuxDeviceAccess.UdevRulesPath)?.Trim(),
            LinuxDeviceAccess.UdevRulesText.Trim(),
            StringComparison.Ordinal);

    private (bool Persistent, bool Session) ReadInputGroup()
    {
        var entry = _readText("/etc/group")?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(':'))
            .FirstOrDefault(parts => parts.Length >= 4 && parts[0] == "input");
        if (entry is null || !int.TryParse(entry[2], out var groupId))
        {
            return (false, false);
        }

        var persistent = entry[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(_userName, StringComparer.Ordinal);
        var status = _readText("/proc/self/status") ?? string.Empty;
        var effectiveGroup = ReadIdList(status, "Gid:").Skip(1).Take(1);
        var groups = ReadIdList(status, "Groups:");
        return (persistent, effectiveGroup.Concat(groups).Contains(groupId));
    }

    private static IEnumerable<int> ReadIdList(string status, string name)
    {
        var line = status.Split('\n').FirstOrDefault(line => line.StartsWith(name, StringComparison.Ordinal));
        if (line is null)
        {
            return [];
        }

        return line[name.Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var id) ? id : -1)
            .Where(id => id >= 0);
    }

    private string? FindExecutable(string fileName)
    {
        foreach (var directory in _path()?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [])
        {
            var candidate = Path.Combine(directory, fileName);
            if (_fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
