using System.Text.Json.Serialization;

namespace DeziroJoy.Core.Mapping;

/// <summary>Which special behavior a <see cref="SpecialKeyTarget"/> triggers.</summary>
public enum SpecialKeyKind
{
    /// <summary>Precision aiming: slows the mapped stick while engaged.</summary>
    Precision,

    /// <summary>Preset leader: switches the active preset slot.</summary>
    Leader,

    /// <summary>
    /// Layer leader: enters a temporary layer - the mappings that carry the matching
    /// <see cref="MappingEntry.Layer"/> - for as long as the key is held. Unlike a preset
    /// leader it never reloads the engine: the layer inherits every mapping it does not
    /// override, so a drag or a held key survives the switch.
    /// </summary>
    Layer,
}

/// <summary>
/// How a leader key engages. Press toggles the preset on each new press edge; Hold
/// activates the alternate slot only while the key is physically held.
/// </summary>
public enum LeaderSwitchMode
{
    Press,
    Hold,
}

/// <summary>
/// A special key target: precision aiming, preset switching or a temporary layer. Precision
/// keys form the button set the engine slows sticks for - sourced here, not from
/// <see cref="MouseStickSettings.PrecisionButton"/> - a leader key emits
/// <see cref="Engine.SwitchPresetCommand"/> to flip between the two slots it names, and a
/// layer key enters the layer <see cref="LayerId"/> names for as long as it is held.
/// <see cref="LayerId"/> matches <see cref="MappingEntry.Layer"/> on the mappings that layer
/// carries; 0 - the default - is the base layer, which every mapping already runs, so a
/// layer key must name 1 or above. It is meaningless for the other kinds.
/// </summary>
public sealed record SpecialKeyTarget(
    SpecialKeyKind Kind,
    PrecisionMode PrecisionMode = PrecisionMode.Toggle,
    LeaderSwitchMode SwitchMode = LeaderSwitchMode.Press,
    int SlotA = 1,
    int SlotB = 2,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int LayerId = 0) : MappingTarget;
