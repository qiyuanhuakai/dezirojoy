using DeziroJoy.Core.Input;

namespace DeziroJoy.Core.Mapping;

/// <summary>One row of the mapping table: source gesture to output action.</summary>
public sealed record MappingEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required InputBinding Source { get; init; }

    public required MappingTarget Target { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>A complete saved mapping set, optionally bound to one device.</summary>
public sealed record GamepadProfile
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string Name { get; init; }

    /// <summary>Device this profile was captured from; null for portable/global profiles.</summary>
    public string? DeviceId { get; init; }

    public GamepadLayout Layout { get; init; } = GamepadLayout.Generic;

    /// <summary>Active preset slot (1-4); leader keys switch between the slots they name.</summary>
    public int PresetSlot { get; init; } = 1;

    public IReadOnlyList<MappingEntry> Mappings { get; init; } = [];
}
