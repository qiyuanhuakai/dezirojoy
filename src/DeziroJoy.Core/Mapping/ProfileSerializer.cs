using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeziroJoy.Core.Mapping;

/// <summary>
/// Human-readable, hand-editable JSON (de)serialization for profiles. Polymorphic
/// bindings/targets carry a "$type" discriminator; enums are written as names.
/// </summary>
public static class ProfileSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(GamepadProfile profile)
        => JsonSerializer.Serialize(profile, Options);

    public static GamepadProfile Deserialize(string json)
        => JsonSerializer.Deserialize<GamepadProfile>(json, Options)
           ?? throw new JsonException("Profile JSON deserialized to null.");
}
