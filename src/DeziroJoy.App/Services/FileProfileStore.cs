using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Services;

/// <summary>
/// JSON file store under %APPDATA%/DeziroJoy/profiles (Windows) or
/// ~/.config/DeziroJoy/profiles (Linux). One file per device id.
/// <para>
/// Every read migrates what it loads (<see cref="ProfileMigration.Migrate"/>), so a profile
/// written before the current schema version comes back upgraded - a deprecated Layer 键
/// rewritten as the leader that replaced it, the several temporary layers folded into the
/// one that remains - instead of relying on each consumer to normalize it. Writes always
/// stamp the current schema version, so a migrated profile is persisted in its new shape on
/// the next save.
/// </para>
/// </summary>
public sealed class FileProfileStore : IProfileStore
{
    public FileProfileStore(string? directory = null)
    {
        ProfilesDirectory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeziroJoy",
            "profiles");

        Directory.CreateDirectory(ProfilesDirectory);
    }

    public string ProfilesDirectory { get; }

    public GamepadProfile? Load(string deviceId)
    {
        var path = PathFor(deviceId);

        if (!File.Exists(path))
        {
            return null;
        }

        // A damaged file reads as what it can rather than as a failure. The caller is
        // building the device's runtime, so one unreadable mapping must not take the whole
        // profile - and with it the pad's mapping - away: whatever parses still runs, and
        // the next save writes the readable profile back over the damaged one. Whatever
        // came back is migrated, so a salvaged body lands in the current shape too.
        return ProfileMigration.Migrate(ProfileSerializer.Load(File.ReadAllText(path)).Profile);
    }

    public void Save(GamepadProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var normalized = profile with { SchemaVersion = GamepadProfile.CurrentSchemaVersion };
        var json = ProfileSerializer.Serialize(normalized);

        File.WriteAllText(PathFor(normalized.DeviceId ?? normalized.Name), json);
    }

    public bool Delete(string deviceId)
    {
        var path = PathFor(deviceId);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public IReadOnlyList<GamepadProfile> LoadAll()
    {
        var profiles = new List<GamepadProfile>();

        foreach (var path in Directory.EnumerateFiles(ProfilesDirectory, "*.json"))
        {
            try
            {
                profiles.Add(ProfileMigration.Migrate(ProfileSerializer.Deserialize(File.ReadAllText(path))));
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        return profiles;
    }

    private string PathFor(string key)
    {
        var invalid = Path.GetInvalidFileNameChars().Append(':').Append('/').Append('\\');
        var safe = string.Join("_", key.Split(invalid.ToArray(), StringSplitOptions.RemoveEmptyEntries));

        return Path.Combine(ProfilesDirectory, string.IsNullOrEmpty(safe) ? "unnamed" : safe) + ".json";
    }
}
