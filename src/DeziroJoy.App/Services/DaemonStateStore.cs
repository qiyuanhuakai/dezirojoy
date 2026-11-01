using System.Text.Json;
using DeziroJoy.App.Ipc;

namespace DeziroJoy.App.Services;

/// <summary>
/// Persists the daemon's desired state - mapping enabled, active profile and configuration
/// revision - as one JSON file, so a restarted daemon resumes the mapping the user asked for
/// instead of reverting to defaults. The profile travels as its serialized body, never a path.
/// </summary>
public sealed class DaemonStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public DaemonStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>
    /// Reads the persisted state, or null when there is none. A corrupt file reads as null:
    /// the daemon then starts from its first-run defaults rather than refusing to start.
    /// </summary>
    public DaemonState? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DaemonState>(File.ReadAllText(FilePath), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(DaemonState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Options));
    }
}
