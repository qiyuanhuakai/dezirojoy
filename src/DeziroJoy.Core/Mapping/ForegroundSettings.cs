using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeziroJoy.Core.Mapping;

/// <summary>
/// The persisted foreground table: the rules, the profile the desktop falls back to, and whether
/// the whole feature runs. A separate object from the profiles themselves, so a rule table a
/// newer build wrote still loads beside the profiles it points at - and an older build, which
/// knows nothing of this file, simply keeps mapping exactly as it did.
/// </summary>
public sealed record ForegroundSettings
{
    public IReadOnlyList<ForegroundRule> Rules { get; init; } = [];

    /// <summary>
    /// The profile that runs when no rule matches and on the desktop. Slot 1 is the device's own
    /// profile, which is what a user who never touches this setting gets.
    /// </summary>
    public int DefaultSlot { get; init; } = ForegroundRule.DefaultSlot;

    /// <summary>
    /// Whether foreground rules run at all. Off is the state a user returns to by turning the
    /// feature back off, and it is a different thing from having no rules: the table survives and
    /// the desktop default stays in charge.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Human-readable JSON for the foreground table. Enums are written as names, so a rule stays
/// editable by hand; a body that cannot be read at all is reported rather than thrown, because a
/// damaged rule file must cost the rules and nothing else.
/// </summary>
public static class ForegroundSettingsSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(ForegroundSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(settings, Options);
    }

    /// <summary>
    /// Reads a table back, or throws <see cref="JsonException"/> when the body is not one.
    /// Callers that must survive a damaged file use <see cref="Load"/> instead.
    /// </summary>
    public static ForegroundSettings Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<ForegroundSettings>(json, Options)
            ?? throw new JsonException("Foreground settings JSON deserialized to null.");
    }

    /// <summary>
    /// Reads a table without letting one damaged part take the whole thing down: whatever parses
    /// is kept, and what did not is named. A rule the build no longer understands is dropped
    /// rather than failing the read, so the rest of the table still runs.
    /// </summary>
    public static (ForegroundSettings Settings, IReadOnlyList<string> Problems) Load(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            return (Deserialize(json), []);
        }
        catch (JsonException exception)
        {
            return (new ForegroundSettings(), [$"前台规则文件无法解析，已按默认值读取：{exception.Message}"]);
        }
    }
}
