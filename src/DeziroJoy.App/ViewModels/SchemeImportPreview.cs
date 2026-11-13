using DeziroJoy.App.Services;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>What one row of an import preview describes happening to one preset slot.</summary>
public enum SchemePreviewRowKind
{
    /// <summary>The slot holds a profile the scheme will overwrite.</summary>
    Replaced,

    /// <summary>The slot holds nothing today; the scheme will create it.</summary>
    Added,

    /// <summary>The slot holds a profile the scheme does not carry; it will be removed.</summary>
    Removed,
}

/// <summary>
/// One slot of the import preview: what it holds today, what the scheme will put there, and
/// whether that is a replace, an addition or a removal. This is the "what will be replaced"
/// the import has to show before anything is touched.
/// </summary>
public sealed record SchemePreviewRow(
    SchemePreviewRowKind Kind,
    int Slot,
    string? CurrentName,
    int CurrentMappings,
    string? IncomingName,
    int IncomingMappings)
{
    /// <summary>What the slot holds today, as one line.</summary>
    public string Summary => Kind switch
    {
        SchemePreviewRowKind.Added => $"槽位 {Slot} · 当前为空",
        _ => $"槽位 {Slot} · 当前「{CurrentName}」（{CurrentMappings} 条映射）",
    };

    /// <summary>What the import will do to the slot, as one line.</summary>
    public string Detail => Kind switch
    {
        SchemePreviewRowKind.Added =>
            $"将新增「{IncomingName}」（{IncomingMappings} 条映射）",
        SchemePreviewRowKind.Removed =>
            "方案不包含此槽位，导入后将被移除",
        _ =>
            $"将替换为「{IncomingName}」（{IncomingMappings} 条映射）",
    };
}

/// <summary>
/// Everything the import dialog shows before the user commits: the scheme's name, the version
/// that wrote it and its usage notes; one row per slot saying what happens to it; the incoming
/// presets with their readable action names; the device-global special keys the device will
/// carry; and the temporary layers the scheme populates. Built from the bundle and the
/// device's current slots, and never mutated afterwards - the dialog is a view over it.
/// </summary>
public sealed record SchemeImportPreview
{
    public required string SchemeName { get; init; }

    public string? AppVersion { get; init; }

    public string? Notes { get; init; }

    public IReadOnlyList<SchemePreviewRow> Rows { get; init; } = [];

    /// <summary>The incoming presets, in slot order, each with what its buttons will do.</summary>
    public IReadOnlyList<SchemePreset> Presets { get; init; } = [];

    /// <summary>The device-global special keys the device will carry, as readable lines.</summary>
    public IReadOnlyList<string> SpecialKeyLines { get; init; } = [];

    /// <summary>The temporary layers the scheme populates, as readable lines.</summary>
    public IReadOnlyList<string> LayerLines { get; init; } = [];

    /// <summary>True when the import overwrites or removes anything the device holds today.</summary>
    public bool ReplacesAnything => Rows.Any(row => row.Kind != SchemePreviewRowKind.Added);

    /// <summary>True when the bundle names the software version that wrote it.</summary>
    public bool HasAppVersion => !string.IsNullOrWhiteSpace(AppVersion);

    /// <summary>True when the bundle carries usage notes.</summary>
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    /// <summary>True when the scheme installs at least one device-global special key.</summary>
    public bool HasSpecialKeys => SpecialKeyLines.Count > 0;

    /// <summary>True when the scheme populates at least one temporary layer.</summary>
    public bool HasLayers => LayerLines.Count > 0;

    /// <summary>
    /// Builds the preview for importing <paramref name="bundle"/> onto the device. The
    /// special-key lines describe the subset the device will end up with - the bundle's own
    /// section when it names one, else the subset its presets carry - because that is the
    /// thing the user is agreeing to.
    /// </summary>
    public static SchemeImportPreview Build(
        SchemeBundle bundle, PresetStore presets, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(presets);

        var current = new Dictionary<int, GamepadProfile>();
        foreach (var slot in presets.SlotsInUse(deviceId))
        {
            if (presets.LoadSlot(deviceId, slot) is { } profile)
            {
                current[slot] = profile;
            }
        }

        var incomingSlots = bundle.Presets.Select(preset => preset.Slot).ToHashSet();
        var rows = new List<SchemePreviewRow>();

        foreach (var preset in bundle.Presets.OrderBy(preset => preset.Slot))
        {
            if (current.TryGetValue(preset.Slot, out var existing))
            {
                rows.Add(new SchemePreviewRow(
                    SchemePreviewRowKind.Replaced,
                    preset.Slot,
                    existing.Name,
                    existing.Mappings.Count,
                    preset.Profile.Name,
                    preset.Profile.Mappings.Count));
            }
            else
            {
                rows.Add(new SchemePreviewRow(
                    SchemePreviewRowKind.Added,
                    preset.Slot,
                    CurrentName: null,
                    CurrentMappings: 0,
                    preset.Profile.Name,
                    preset.Profile.Mappings.Count));
            }
        }

        foreach (var slot in current.Keys
            .Where(slot => !incomingSlots.Contains(slot))
            .OrderBy(slot => slot))
        {
            rows.Add(new SchemePreviewRow(
                SchemePreviewRowKind.Removed,
                slot,
                current[slot].Name,
                current[slot].Mappings.Count,
                IncomingName: null,
                IncomingMappings: 0));
        }

        var keys = bundle.SpecialKeys
            ?? PresetStore.SpecialKeysInEffect(
                bundle.Presets.OrderBy(preset => preset.Slot).Select(preset => preset.Profile));

        return new SchemeImportPreview
        {
            SchemeName = bundle.Name,
            AppVersion = bundle.AppVersion,
            Notes = bundle.Notes,
            Rows = rows,
            Presets = [.. bundle.Presets.OrderBy(preset => preset.Slot)],
            SpecialKeyLines = [.. keys.Select(SpecialKeyLine)],
            LayerLines = [.. bundle.Layers.Select(LayerLine)],
        };
    }

    /// <summary>One device-global special key as a readable line, naming its trigger and mode.</summary>
    private static string SpecialKeyLine(PresetStore.DeviceSpecialKey key)
    {
        var trigger = MappingDescriptions.Button(key.Button, key.Code);

        if (key.Target is not SpecialKeyTarget target)
        {
            return $"{trigger}：{MappingDescriptions.Target(key.Target)}";
        }

        return target.Kind switch
        {
            SpecialKeyKind.Leader =>
                $"{trigger}：Leader 键（槽位 {target.SlotA} ↔ {target.SlotB}，{MappingDescriptions.LeaderSwitchModeLabel(target.SwitchMode)}）",
            SpecialKeyKind.Layer =>
                $"{trigger}：{MappingDescriptions.SpecialKey(target)}（按住进入）",
            _ =>
                $"{trigger}：精准键（{MappingDescriptions.PrecisionModeLabel(target.PrecisionMode)}）",
        };
    }

    /// <summary>One temporary layer as a readable line: where it sits and what its buttons do.</summary>
    private static string LayerLine(SchemeLayer layer)
    {
        var actions = string.Join("；", layer.Actions.Select(action => $"{action.Source} → {action.Action}"));

        return $"槽位 {layer.PresetSlot} · 临时层 {layer.LayerId}：{actions}";
    }
}
