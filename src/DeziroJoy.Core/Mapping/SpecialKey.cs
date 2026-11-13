using System.Text.Json.Serialization;

namespace DeziroJoy.Core.Mapping;

/// <summary>Which special behavior a <see cref="SpecialKeyTarget"/> triggers.</summary>
public enum SpecialKeyKind
{
    /// <summary>Precision aiming: slows the mapped stick while engaged.</summary>
    Precision,

    /// <summary>
    /// Leader: switches what the device is running - the active preset slot, or the one
    /// temporary layer. Which of the two is <see cref="LeaderAction"/>.
    /// </summary>
    Leader,

    /// <summary>
    /// Deprecated: a layer leader, kept so a profile written before the layer key was
    /// merged into the leader still deserializes. <see cref="ProfileMigration"/> rewrites
    /// every one into <see cref="Leader"/> with <see cref="LeaderAction.Layer"/> on load,
    /// and <see cref="Engine.MappingEngine"/> normalizes the same shape, so a profile that
    /// skipped migration still behaves exactly as it did. New configurations are written
    /// as <see cref="Leader"/>.
    /// </summary>
    Layer,
}

/// <summary>
/// How a leader key engages. Press toggles on each new press edge; Hold
/// activates the alternate only while the key is physically held.
/// </summary>
public enum LeaderSwitchMode
{
    Press,
    Hold,
}

/// <summary>
/// What a leader key switches: the active preset slot, or the one temporary layer. There is
/// exactly one temporary layer, so a layer leader has nothing to name - it switches the
/// layer the profile already carries.
/// </summary>
public enum LeaderAction
{
    /// <summary>
    /// Switches the active preset slot between <see cref="SpecialKeyTarget.SlotA"/> and
    /// <see cref="SpecialKeyTarget.SlotB"/>, emitting
    /// <see cref="Engine.SwitchPresetCommand"/> for the host to act on.
    /// </summary>
    Preset,

    /// <summary>
    /// Switches the one temporary layer - the mappings that carry
    /// <see cref="MappingEntry.Layer"/> 1 - instead of any preset slot. The layer inherits
    /// every base mapping it does not override, so a drag or a held key survives the switch,
    /// and the engine never reloads for it. Meaningless for the other kinds.
    /// </summary>
    Layer,
}

/// <summary>
/// A special key target: precision aiming, or a leader key. Precision keys form the button
/// set the engine slows sticks for - sourced here, not from
/// <see cref="MouseStickSettings.PrecisionButton"/>. A leader key switches what the device
/// runs: <see cref="LeaderAction.Preset"/> flips between the two slots
/// <see cref="SlotA"/> and <see cref="SlotB"/> name, and <see cref="LeaderAction.Layer"/>
/// switches the one temporary layer the profile carries, entering it while the key is held
/// under <see cref="LeaderSwitchMode.Hold"/> or toggling it on each press edge under
/// <see cref="LeaderSwitchMode.Press"/>. <see cref="Action"/> is meaningless for the other
/// kinds, and so are the slot numbers for a layer leader.
/// </summary>
public sealed record SpecialKeyTarget(
    SpecialKeyKind Kind,
    PrecisionMode PrecisionMode = PrecisionMode.Toggle,
    LeaderSwitchMode SwitchMode = LeaderSwitchMode.Press,
    int SlotA = 1,
    int SlotB = 2,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] LeaderAction Action = LeaderAction.Preset) : MappingTarget;
