namespace DeziroJoy.Core.Mapping;

/// <summary>Which special behavior a <see cref="SpecialKeyTarget"/> triggers.</summary>
public enum SpecialKeyKind
{
    /// <summary>Precision aiming: slows the mapped stick while engaged.</summary>
    Precision,

    /// <summary>Preset leader: switches the active preset slot.</summary>
    Leader,
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
/// A special key target: precision aiming or preset switching. Precision keys form the
/// button set the engine slows sticks for - sourced here, not from
/// <see cref="MouseStickSettings.PrecisionButton"/> - and a leader key emits
/// <see cref="Engine.SwitchPresetCommand"/> to flip between the two slots it names.
/// </summary>
public sealed record SpecialKeyTarget(
    SpecialKeyKind Kind,
    PrecisionMode PrecisionMode = PrecisionMode.Toggle,
    LeaderSwitchMode SwitchMode = LeaderSwitchMode.Press,
    int SlotA = 1,
    int SlotB = 2) : MappingTarget;
