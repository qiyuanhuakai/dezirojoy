using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Services;

/// <summary>
/// JSON file store under %APPDATA%/DeziroJoy/profiles (Windows) or
/// ~/.config/DeziroJoy/profiles (Linux). One file per device id.
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

        return File.Exists(path) ? ProfileSerializer.Deserialize(File.ReadAllText(path)) : null;
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
                profiles.Add(ProfileSerializer.Deserialize(File.ReadAllText(path)));
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
