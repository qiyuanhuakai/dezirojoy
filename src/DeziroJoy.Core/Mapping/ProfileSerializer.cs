using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DeziroJoy.Core.Input;

namespace DeziroJoy.Core.Mapping;

/// <summary>
/// Human-readable, hand-editable JSON (de)serialization for profiles. Polymorphic
/// bindings/targets carry a "$type" discriminator; enums are written as names. A target
/// discriminator no type in this build claims is preserved as an <see cref="UnknownTarget"/>
/// rather than rejected, so a profile a newer build wrote still loads, and
/// <see cref="Load"/> goes further still and rebuilds whatever a damaged body still has.
/// </summary>
public static class ProfileSerializer
{
    /// <summary>What a profile whose name could not be read is called, so the UI never shows a blank.</summary>
    private const string UnreadableProfileName = "无法解析的配置";

    /// <summary>
    /// The resolver every profile read and write goes through. It does one thing: it hands
    /// <see cref="MappingTarget"/>'s polymorphism to <see cref="MappingTargetConverter"/>,
    /// which owns the "$type" discriminator and the unknown-target fallback. A converter
    /// registered for a base type that the serializer also configures for metadata from its
    /// [JsonDerivedType] attributes is refused outright, so the attributes are cleared for
    /// that one type and left alone everywhere else - <see cref="InputBinding"/> still
    /// serializes through its own.
    /// </summary>
    private static readonly IJsonTypeInfoResolver TargetResolver =
        new DefaultJsonTypeInfoResolver().WithAddedModifier(typeInfo =>
        {
            if (typeInfo.Type == typeof(MappingTarget))
            {
                typeInfo.PolymorphismOptions = null;
            }
        });

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(), new MappingTargetConverter() },
        TypeInfoResolver = TargetResolver,
    };

    public static string Serialize(GamepadProfile profile)
        => JsonSerializer.Serialize(profile, Options);

    public static GamepadProfile Deserialize(string json)
        => JsonSerializer.Deserialize<GamepadProfile>(json, Options)
           ?? throw new JsonException("Profile JSON deserialized to null.");

    /// <summary>
    /// Loads a profile for display without letting one damaged part take the whole table
    /// down: everything that parses is kept, everything that does not is reported. A profile
    /// a newer build wrote, or one a hand edit broke, still has to show its mappings and let
    /// the user delete them - a body that failed outright left the mapping list empty and the
    /// daemon's copy undeletable. Anything that must refuse a damaged profile - an import, an
    /// apply - uses <see cref="Deserialize"/> instead.
    /// </summary>
    public static ProfileLoadResult Load(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            return new ProfileLoadResult(Deserialize(json), []);
        }
        catch (JsonException)
        {
            return Salvage(json);
        }
    }

    /// <summary>
    /// Rebuilds as much of a damaged body as can be read: the header field by field, each
    /// one falling back to its own default, and the mappings one by one, each broken one
    /// dropped and named. The result is what the UI shows, never what gets applied.
    /// </summary>
    private static ProfileLoadResult Salvage(string json)
    {
        var problems = new List<string>();
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            // Not JSON at all: nothing in the body can be read, so the profile comes back
            // empty and the reason travels with it.
            problems.Add($"配置内容不是有效的 JSON：{exception.Message}");
            return new ProfileLoadResult(new GamepadProfile { Name = UnreadableProfileName }, problems);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                problems.Add("配置内容不是 JSON 对象。");
                return new ProfileLoadResult(new GamepadProfile { Name = UnreadableProfileName }, problems);
            }

            var entries = new List<MappingEntry>();

            if (root.TryGetProperty(nameof(GamepadProfile.Mappings), out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var entry in list.EnumerateArray())
                {
                    index++;
                    try
                    {
                        if (JsonSerializer.Deserialize<MappingEntry>(entry, Options) is { } mapping)
                        {
                            entries.Add(mapping);
                        }
                    }
                    catch (JsonException exception)
                    {
                        problems.Add($"第 {index} 条映射无法解析，已跳过：{exception.Message}");
                    }
                }
            }
            else
            {
                problems.Add("配置中没有可读取的映射列表。");
            }

            // The record's own defaults, so a header field that cannot be read costs that
            // field alone and nothing else about the profile moves.
            var defaults = new GamepadProfile { Name = UnreadableProfileName };
            var profile = new GamepadProfile
            {
                SchemaVersion = ReadHeader(root, nameof(GamepadProfile.SchemaVersion), defaults.SchemaVersion, problems),
                Name = ReadHeader(root, nameof(GamepadProfile.Name), defaults.Name, problems),
                DeviceId = ReadHeader<string?>(root, nameof(GamepadProfile.DeviceId), defaults.DeviceId, problems),
                Layout = ReadHeader(root, nameof(GamepadProfile.Layout), defaults.Layout, problems),
                PresetSlot = ReadHeader(root, nameof(GamepadProfile.PresetSlot), defaults.PresetSlot, problems),
                Mappings = entries,
            };

            if (problems.Count == 0)
            {
                // The strict parse failed for a reason the field-by-field read did not name -
                // a required header field that is simply absent, say - and the caller still
                // has to know the body it is showing is not the one that was written.
                problems.Add("配置的头部字段缺失或无法解析，已按默认值读取。");
            }

            return new ProfileLoadResult(profile, problems);
        }
    }

    /// <summary>
    /// One header field, read through the same deserializer the whole profile uses so its
    /// enums and numbers keep the semantics the editor wrote them with. A field that cannot
    /// be read costs itself: the default takes its place and the reason is named.
    /// </summary>
    private static T ReadHeader<T>(JsonElement root, string property, T fallback, List<string> problems)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return fallback;
        }

        try
        {
            return value.Deserialize<T>(Options) ?? fallback;
        }
        catch (JsonException)
        {
            problems.Add($"配置的 {property} 无法解析，已使用默认值。");
            return fallback;
        }
    }
}

/// <summary>
/// What <see cref="ProfileSerializer.Load"/> produced: the profile it could rebuild, and
/// every part of the body it had to drop or default. An empty list means the body parsed
/// whole.
/// </summary>
public sealed record ProfileLoadResult(GamepadProfile Profile, IReadOnlyList<string> Problems)
{
    /// <summary>True when any part of the body could not be read.</summary>
    public bool HasProblems => Problems.Count > 0;
}
