using System.Reflection;
using System.Text.Json;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Services;

/// <summary>
/// One mapping as a readable pair - the physical source and what it does - the way the mapping
/// table names them. The bundle carries these so the file itself reads as behaviour, and the
/// import preview shows them without having to resolve targets again.
/// </summary>
public sealed record SchemeActionName(string Source, string Action);

/// <summary>
/// One preset of a scheme: the slot it occupies, the profile stored in it, and the readable
/// action names its mappings carry. The slot number travels with the profile because a leader
/// key names the two slots it flips between - a scheme whose live slots are 1, 3 and 4 has to
/// keep that shape when it is imported onto another device.
/// </summary>
public sealed record SchemePreset
{
    public required int Slot { get; init; }

    public required GamepadProfile Profile { get; init; }

    public IReadOnlyList<SchemeActionName> ActionNames { get; init; } = [];
}

/// <summary>
/// One temporary layer as the bundle names it: the layer id, the preset that carries it, and
/// what each of that layer's buttons does. The layer's mappings travel inside the preset's
/// profile - they are mappings with a <see cref="MappingEntry.Layer"/> tag - so this section is
/// the human-readable face of them, and an import treats the profiles as the authority.
/// </summary>
public sealed record SchemeLayer
{
    public required int LayerId { get; init; }

    public required int PresetSlot { get; init; }

    public IReadOnlyList<SchemeActionName> Actions { get; init; } = [];
}

/// <summary>
/// A complete scheme: a device's whole preset set in one file, not a single profile. It carries
/// the base config and every other preset, the device-global special keys the presets share,
/// the temporary layers their mappings populate, readable action names for every mapping, the
/// software version that wrote it and usage notes. Importing one replaces the device's preset
/// set as one operation; exporting one is how a user moves a complete, mutually-consistent
/// behavior between machines.
/// </summary>
public sealed record SchemeBundle
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>What the scheme is called, e.g. the built-in template's name.</summary>
    public required string Name { get; init; }

    /// <summary>The software version that wrote the bundle, or null when it could not be read.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Usage notes: what the scheme is for and how its pieces work together.</summary>
    public string? Notes { get; init; }

    /// <summary>Every preset the scheme carries, in slot order; the first is the base config.</summary>
    public IReadOnlyList<SchemePreset> Presets { get; init; } = [];

    /// <summary>
    /// The device-global special keys the scheme installs. Null - rather than empty - means the
    /// bundle does not name them, so the import takes the subset the presets themselves carry;
    /// an explicit list, empty or not, is what the device ends up with.
    /// </summary>
    public IReadOnlyList<PresetStore.DeviceSpecialKey>? SpecialKeys { get; init; }

    /// <summary>The temporary layers the scheme carries, one entry per layer per preset.</summary>
    public IReadOnlyList<SchemeLayer> Layers { get; init; } = [];
}

/// <summary>
/// Outcome of reading a scheme bundle. <see cref="Bundle"/> is set only by a successful read;
/// <see cref="Message"/> is a ready-to-show Chinese status line.
/// </summary>
public sealed record SchemeBundleLoadResult(bool Succeeded, SchemeBundle? Bundle, string Message)
{
    public static SchemeBundleLoadResult Loaded(SchemeBundle bundle)
        => new(true, bundle, $"已读取方案：{bundle.Name}");

    public static SchemeBundleLoadResult Failed(string message)
        => new(false, null, message);
}

/// <summary>
/// The one codec for scheme bundles. The embedded profiles go through
/// <see cref="ProfileSerializer"/>'s own options - the same codec the per-device store uses -
/// so a profile inside a bundle is byte-for-byte what a profile file holds. Reading never
/// throws for bad content: a malformed file or a bundle a newer build wrote comes back as a
/// failed result carrying the reason, and a plain single-profile file - what the old
/// single-profile export wrote - is accepted as a one-preset scheme rather than refused.
/// </summary>
public static class SchemeBundleCodec
{
    public static string Serialize(SchemeBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        return JsonSerializer.Serialize(bundle, ProfileSerializer.Options);
    }

    public static SchemeBundleLoadResult Load(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return SchemeBundleLoadResult.Failed("导入失败：未选择文件。");
        }

        if (!File.Exists(sourcePath))
        {
            return SchemeBundleLoadResult.Failed("导入失败：文件不存在。");
        }

        string json;
        try
        {
            json = File.ReadAllText(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SchemeBundleLoadResult.Failed($"导入失败：无法读取文件（{ex.Message}）");
        }

        return Parse(json);
    }

    /// <summary>
    /// Reads a bundle from JSON, refusing - rather than throwing on - everything that is not a
    /// scheme this build can install: a body that does not parse, a bundle whose schema or
    /// whose preset's schema is newer than this build, a preset list that names one slot
    /// twice, and a special-key entry whose target is not a special key. Each refusal names
    /// the reason in the message.
    /// <para>
    /// A body with no preset list is read a second time as a plain profile: the single-profile
    /// export this design replaced wrote exactly that shape, and a file a user kept from it
    /// still has to import. Such a file comes back as a one-preset scheme carrying the profile
    /// in slot 1, so the whole import - preview, atomic apply, undo - treats it like any
    /// bundle. A body that is neither is refused.
    /// </para>
    /// </summary>
    public static SchemeBundleLoadResult Parse(string json)
    {
        SchemeBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<SchemeBundle>(json, ProfileSerializer.Options);
        }
        catch (JsonException ex)
        {
            return SchemeBundleLoadResult.Failed($"导入失败：不是有效的 DeziroJoy 方案包（{ex.Message}）");
        }

        if (bundle is null)
        {
            return SchemeBundleLoadResult.Failed("导入失败：方案包内容为空。");
        }

        // "Is this a bundle at all?" comes before "is it new enough?": a plain profile file
        // parses into a bundle with an empty preset list - and its own top-level schema
        // version, which belongs to the profile format and says nothing about this bundle's.
        // Refusing it as a version mismatch would point the user at the wrong door. So an
        // empty preset list is read a second time as a profile and wrapped as a one-preset
        // scheme: the profile becomes slot 1's preset, and the whole import - preview, atomic
        // apply, undo - treats it like any bundle. The single-profile export this design
        // replaced wrote exactly this shape, and a file a user kept from it still has to
        // import. A body that is neither is refused.
        if (bundle.Presets.Count == 0)
        {
            GamepadProfile profile;
            try
            {
                profile = ProfileSerializer.Deserialize(json);
            }
            catch (JsonException)
            {
                return SchemeBundleLoadResult.Failed(
                    "导入失败：这不是方案包文件（没有预设列表），也不是可识别的 DeziroJoy 配置文件。");
            }

            if (profile.SchemaVersion > GamepadProfile.CurrentSchemaVersion)
            {
                return SchemeBundleLoadResult.Failed(
                    $"导入失败：配置版本过新（v{profile.SchemaVersion}），" +
                    $"本应用最高支持 v{GamepadProfile.CurrentSchemaVersion}");
            }

            bundle = new SchemeBundle
            {
                Name = string.IsNullOrWhiteSpace(profile.Name) ? "导入的方案" : profile.Name,
                Presets =
                [
                    new SchemePreset
                    {
                        Slot = 1,
                        Profile = profile,
                        ActionNames = SchemeBundleFactory.ActionNamesOf(profile),
                    },
                ],
            };
        }

        if (bundle.SchemaVersion > SchemeBundle.CurrentSchemaVersion)
        {
            return SchemeBundleLoadResult.Failed(
                $"导入失败：方案包版本过新（v{bundle.SchemaVersion}），本应用最高支持 v{SchemeBundle.CurrentSchemaVersion}");
        }

        var slots = new HashSet<int>();
        foreach (var preset in bundle.Presets)
        {
            if (preset.Slot < 1)
            {
                return SchemeBundleLoadResult.Failed("导入失败：方案包的预设槽位编号无效。");
            }

            if (!slots.Add(preset.Slot))
            {
                return SchemeBundleLoadResult.Failed($"导入失败：方案包多次使用了槽位 {preset.Slot}。");
            }

            if (preset.Profile.SchemaVersion > GamepadProfile.CurrentSchemaVersion)
            {
                return SchemeBundleLoadResult.Failed(
                    $"导入失败：槽位 {preset.Slot} 的配置版本过新（v{preset.Profile.SchemaVersion}），" +
                    $"本应用最高支持 v{GamepadProfile.CurrentSchemaVersion}");
            }
        }

        foreach (var key in bundle.SpecialKeys ?? [])
        {
            if (key.Target is not SpecialKeyTarget)
            {
                return SchemeBundleLoadResult.Failed("导入失败：方案包的特殊键包含无法识别的目标。");
            }
        }

        return SchemeBundleLoadResult.Loaded(bundle);
    }
}

/// <summary>
/// Builds a scheme bundle from the device a user is exporting: every preset slot the device
/// holds, the device-global special keys in effect, the temporary layers its mappings
/// populate, and the readable action names for all of it.
/// </summary>
public static class SchemeBundleFactory
{
    public static SchemeBundle FromDevice(
        PresetStore presets,
        string deviceId,
        string name,
        string? notes,
        string? appVersion)
    {
        ArgumentNullException.ThrowIfNull(presets);

        var presetList = new List<SchemePreset>();
        var layerList = new List<SchemeLayer>();

        foreach (var slot in presets.SlotsInUse(deviceId))
        {
            if (presets.LoadSlot(deviceId, slot) is not { } profile)
            {
                continue;
            }

            presetList.Add(new SchemePreset
            {
                Slot = slot,
                Profile = profile,
                ActionNames = ActionNamesOf(profile),
            });

            foreach (var layer in LayersOf(profile, slot))
            {
                layerList.Add(layer);
            }
        }

        return new SchemeBundle
        {
            Name = name,
            AppVersion = appVersion,
            Notes = notes,
            Presets = presetList,
            SpecialKeys = presets.SpecialKeysInEffect(deviceId),
            Layers = layerList,
        };
    }

    /// <summary>Every mapping of a profile as a readable pair, in profile order.</summary>
    public static IReadOnlyList<SchemeActionName> ActionNamesOf(GamepadProfile profile)
        => ActionNamesOf(profile.Mappings);

    /// <summary>The given mappings as readable pairs, in the order they are given.</summary>
    public static IReadOnlyList<SchemeActionName> ActionNamesOf(IEnumerable<MappingEntry> mappings)
        => [.. mappings.Select(mapping => new SchemeActionName(
            MappingDescriptions.Source(mapping.Source),
            MappingDescriptions.ActionName(mapping.Target)))];

    /// <summary>The temporary layers one profile populates, in ascending layer order.</summary>
    private static IEnumerable<SchemeLayer> LayersOf(GamepadProfile profile, int slot)
    {
        foreach (var group in profile.Mappings
            .Where(mapping => mapping.Layer > 0)
            .GroupBy(mapping => mapping.Layer)
            .OrderBy(group => group.Key))
        {
            yield return new SchemeLayer
            {
                LayerId = group.Key,
                PresetSlot = slot,
                Actions =
                [
                    .. group.Select(mapping => new SchemeActionName(
                        MappingDescriptions.Source(mapping.Source),
                        MappingDescriptions.ActionName(mapping.Target))),
                ],
            };
        }
    }

    /// <summary>
    /// The running software version, read from the assembly's informational version the same
    /// way the update service reads it, so a bundle records the build that wrote it.
    /// </summary>
    public static string CurrentAppVersion()
    {
        var informational = typeof(SchemeBundleFactory).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // SourceLink-style builds append "+<commit>" to the informational version; the
        // precedence-relevant part ends at the plus.
        var cut = informational?.Split('+', 2)[0];

        return string.IsNullOrWhiteSpace(cut) ? "未知版本" : cut!;
    }
}
