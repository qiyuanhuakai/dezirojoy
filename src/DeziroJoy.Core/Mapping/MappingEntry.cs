using System.Text.Json.Serialization;
using DeziroJoy.Core.Input;

namespace DeziroJoy.Core.Mapping;

/// <summary>One row of the mapping table: source gesture to output action.</summary>
public sealed record MappingEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required InputBinding Source { get; init; }

    public required MappingTarget Target { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Which layer this mapping belongs to. 0 - the default - is the base layer, which
    /// always runs. 1 is the one temporary layer: it only runs while a leader key whose
    /// <see cref="SpecialKeyTarget.Action"/> is <see cref="LeaderAction.Layer"/> has it in
    /// effect, and it overrides the base mapping that listens on the same physical input;
    /// everything the layer does not override is inherited, so a temporary layer that
    /// rebinds a few buttons leaves the rest of the profile running exactly as it was. A
    /// value above 1 only reaches a profile written before the single-layer model;
    /// <see cref="ProfileMigration"/> folds it into 1 and the engine reads any non-zero
    /// value as the temporary layer.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Layer { get; init; }
}

/// <summary>A complete saved mapping set, optionally bound to one device.</summary>
public sealed record GamepadProfile
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string Name { get; init; }

    /// <summary>Device this profile was captured from; null for portable/global profiles.</summary>
    public string? DeviceId { get; init; }

    public GamepadLayout Layout { get; init; } = GamepadLayout.Generic;

    /// <summary>Active preset slot (1-4); leader keys switch between the slots they name.</summary>
    public int PresetSlot { get; init; } = 1;

    public IReadOnlyList<MappingEntry> Mappings { get; init; } = [];
}

/// <summary>
/// One mapping a device currently has in effect, and the layer it came from. Carried by the
/// daemon's snapshot so the GUI can show what a device is really running without parsing the
/// profile body: the ids resolve against the profile the snapshot already carries.
/// </summary>
public sealed record EffectiveMappingSnapshot(Guid MappingId, int Layer);

/// <summary>
/// The temporary layer one device is running - 0 for the base layer alone, 1 for the one
/// temporary layer - and every mapping that layer puts in effect, in engine order.
/// </summary>
public sealed record ActiveLayerSnapshot(
    string DeviceId,
    int LayerId,
    IReadOnlyList<EffectiveMappingSnapshot> Mappings);
