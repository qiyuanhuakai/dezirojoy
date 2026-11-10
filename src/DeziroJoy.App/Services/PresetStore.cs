using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Profiles;

namespace DeziroJoy.App.Services;

/// <summary>
/// Why <see cref="PresetStore.DeleteSlot"/> did or did not remove a slot. Every refusal is a
/// state the caller can show the user; only <see cref="Deleted"/> changes the store.
/// </summary>
public enum PresetDeleteOutcome
{
    /// <summary>The slot's profile was removed and any Leader references to it were repaired.</summary>
    Deleted,

    /// <summary>Slot 1 is the device's own profile and the anchor every other slot reads from; it is never deletable.</summary>
    PrimarySlot,

    /// <summary>The device would be left with no preset at all; the last remaining slot is kept.</summary>
    LastRemaining,

    /// <summary>The slot already held nothing, so there was nothing to remove.</summary>
    NothingStored,
}

/// <summary>
/// The preset-slot service layer over <see cref="IProfileStore"/>: the slots of one
/// device are stored beside each other under derived keys, so a leader key can flip
/// between them and a reconnect restores whichever slot was saved. Slot 1 keeps the
/// device's own key, which is what lets an unmigrated profile read as the primary slot.
/// Slots are open-ended - any positive slot number is valid - and the four a device is
/// first materialized with are only the initial set, not a ceiling.
/// </summary>
public sealed class PresetStore
{
    /// <summary>Slots are numbered 1 and up; slot 1 is the device's own profile.</summary>
    public const int MinSlot = 1;

    /// <summary>
    /// How many slots <see cref="EnsureDefaults"/> materializes on first use. Slots added
    /// later persist and behave exactly like these; this is only the initial set a fresh
    /// device starts with, not the maximum a device may hold.
    /// </summary>
    public const int DefaultSlotCount = 4;

    private readonly IProfileStore _store;

    public PresetStore(IProfileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>The store key a slot's profile is persisted under.</summary>
    public static string KeyFor(string deviceId, int slot)
    {
        ValidateSlot(slot);
        return slot == MinSlot ? deviceId : $"preset{slot}:{deviceId}";
    }

    /// <summary>The profile stored in one slot, or null when the slot holds nothing.</summary>
    public GamepadProfile? LoadSlot(string deviceId, int slot)
    {
        ValidateSlot(slot);
        return _store.Load(KeyFor(deviceId, slot));
    }

    /// <summary>
    /// Persists <paramref name="profile"/> as one slot of a device, rebinding it to the
    /// slot key exactly the way the legacy leader alternate was stored, so a slot's
    /// profile never collides with the device's own.
    /// </summary>
    public void SaveSlot(string deviceId, int slot, GamepadProfile profile)
    {
        ValidateSlot(slot);
        ArgumentNullException.ThrowIfNull(profile);

        _store.Save(profile with { DeviceId = KeyFor(deviceId, slot) });
    }

    /// <summary>
    /// Removes one slot's stored profile for a device and repairs every Leader mapping that
    /// still pointed at it, so a delete can never leave a leader key flipping toward a slot
    /// that no longer exists. The slot is gone from <see cref="SlotsInUse"/> the moment this
    /// returns <see cref="PresetDeleteOutcome.Deleted"/>, and stays gone across a restart
    /// because the delete reaches the backing store.
    /// <para>
    /// Slot 1 is the device's own profile and the anchor the rest of the system reads from -
    /// the legacy migration base, the "unmigrated reads as primary" shape - so it is refused
    /// outright rather than deleted, and a device is never left preset-less. A slot that holds
    /// nothing is a no-op rather than an error. Leader repair runs before the delete, against
    /// the slots that survive it: each surviving profile with a leader naming the deleted slot
    /// is retargeted to the lowest surviving slot that keeps its two ends distinct, and a
    /// leader with no distinct survivor left is dropped rather than left unswitchable.
    /// </para>
    /// </summary>
    public PresetDeleteOutcome DeleteSlot(string deviceId, int slot)
    {
        ValidateSlot(slot);

        var inUse = SlotsInUse(deviceId);
        if (slot == MinSlot)
        {
            return PresetDeleteOutcome.PrimarySlot;
        }

        if (inUse.Count <= 1)
        {
            return PresetDeleteOutcome.LastRemaining;
        }

        if (!inUse.Contains(slot))
        {
            return PresetDeleteOutcome.NothingStored;
        }

        var surviving = inUse.Where(s => s != slot).ToList();
        RepairLeaderReferences(deviceId, slot, surviving);

        _store.Delete(KeyFor(deviceId, slot));
        return PresetDeleteOutcome.Deleted;
    }

    /// <summary>
    /// Rewrites every surviving profile's Leader mapping that named <paramref name="deletedSlot"/>
    /// so it flips between two slots that still exist. The deleted end is retargeted to the
    /// lowest surviving slot other than the mapping's surviving end; when only that one
    /// surviving slot remains the leader has nowhere distinct to flip to, so the mapping is
    /// dropped. Profiles untouched by the delete are never re-saved.
    /// </summary>
    private void RepairLeaderReferences(string deviceId, int deletedSlot, IReadOnlyList<int> surviving)
    {
        foreach (var slot in surviving)
        {
            var profile = LoadSlot(deviceId, slot);
            if (profile is null)
            {
                continue;
            }

            var repaired = false;
            var mappings = new List<MappingEntry>(profile.Mappings.Count);
            foreach (var mapping in profile.Mappings)
            {
                if (mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader } leader
                    && (leader.SlotA == deletedSlot || leader.SlotB == deletedSlot))
                {
                    repaired = true;

                    var other = leader.SlotA == deletedSlot ? leader.SlotB : leader.SlotA;
                    var retarget = surviving.Where(candidate => candidate != other).DefaultIfEmpty(0).Min();
                    if (retarget == 0)
                    {
                        // No distinct survivor: a leader that would flip a slot against itself
                        // is dropped rather than written, matching the editor's own refusal.
                        continue;
                    }

                    mappings.Add(mapping with
                    {
                        Target = leader with
                        {
                            SlotA = leader.SlotA == deletedSlot ? retarget : leader.SlotA,
                            SlotB = leader.SlotB == deletedSlot ? retarget : leader.SlotB,
                        },
                    });
                }
                else
                {
                    mappings.Add(mapping);
                }
            }

            if (repaired)
            {
                SaveSlot(deviceId, slot, profile with { Mappings = [.. mappings] });
            }
        }
    }

    /// <summary>
    /// Creates any missing slot as an independent copy of the device's primary profile.
    /// Existing slots are never overwritten. The returned profile is the one the runtime
    /// should activate for <paramref name="activeSlot"/>.
    /// </summary>
    public GamepadProfile EnsureDefaults(GamepadDescriptor descriptor, int activeSlot = MinSlot)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateSlot(activeSlot);

        var primary = LoadSlot(descriptor.DeviceId, MinSlot)
            ?? DefaultProfileLibrary.CreateDefault(descriptor);
        if (LoadSlot(descriptor.DeviceId, MinSlot) is null)
        {
            SaveSlot(descriptor.DeviceId, MinSlot, primary);
        }

        for (var slot = MinSlot + 1; slot <= DefaultSlotCount; slot++)
        {
            if (LoadSlot(descriptor.DeviceId, slot) is not null)
            {
                continue;
            }

            SaveSlot(descriptor.DeviceId, slot, primary with
            {
                Name = $"{primary.Name} · 预设 {slot}",
                DeviceId = KeyFor(descriptor.DeviceId, slot),
            });
        }

        return LoadSlot(descriptor.DeviceId, activeSlot)
            ?? throw new InvalidOperationException($"preset slot {activeSlot} was not initialized");
    }

    /// <summary>
    /// The slots that hold a profile for the device, in ascending order. Enumerated from
    /// the store itself rather than a fixed 1..N range, so a slot added past the initial
    /// four is discovered the same way the first four are.
    /// </summary>
    public IReadOnlyList<int> SlotsInUse(string deviceId)
    {
        var slots = new SortedSet<int>();

        foreach (var profile in _store.LoadAll())
        {
            if (SlotFromKey(profile.DeviceId, deviceId) is { } slot)
            {
                slots.Add(slot);
            }
        }

        return [.. slots];
    }

    /// <summary>
    /// The slot number a stored profile's key stands for, or null when it is not one of
    /// this device's slots. Slot 1 is the bare device id; every later slot is prefixed with
    /// its number. The device id - which may itself contain a colon, as XInput ids do - is
    /// matched as the suffix after the slot number, so a colon in the id cannot be mistaken
    /// for the separator.
    /// </summary>
    private static int? SlotFromKey(string? key, string deviceId)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (string.Equals(key, deviceId, StringComparison.Ordinal))
        {
            return MinSlot;
        }

        const string prefix = "preset";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = key.Substring(prefix.Length);
        var colon = rest.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        return int.TryParse(rest.AsSpan(0, colon), out var slot)
            && slot > MinSlot
            && string.Equals(rest.Substring(colon + 1), deviceId, StringComparison.Ordinal)
                ? slot
                : null;
    }

    /// <summary>
    /// One-time conversion of the legacy leader pair into the preset slots: the alternate
    /// (or a clone of the base profile when there is none) becomes slot 2 carrying one
    /// leader mapping that flips between slots 1 and 2, while the base profile stays slot
    /// 1. Returns the migrated slot-2 profile, or null when there was nothing to migrate.
    /// <para>
    /// Idempotent by shape: a slot-2 profile that already carries a leader entry for the
    /// button is returned as it stands, so a second run neither appends a duplicate entry
    /// nor clobbers customizations made to slot 2 since the first migration.
    /// </para>
    /// </summary>
    public GamepadProfile? MigrateLegacyLeader(
        string deviceId,
        GamepadButton? leaderButton,
        LeaderMode leaderMode,
        GamepadProfile? leaderAlternateProfile)
    {
        if (leaderButton is null)
        {
            return null;
        }

        var stored = LoadSlot(deviceId, 2);
        var baseProfile = LoadSlot(deviceId, 1);
        if (stored is null && leaderAlternateProfile is null && baseProfile is null)
        {
            return null;
        }

        // The handed-in alternate wins, then whatever slot 2 already holds, then a clone
        // of the base profile: preferring the stored slot-2 profile over the base is what
        // keeps a second run from overwriting it with a fresh clone.
        var slotTwo = (leaderAlternateProfile ?? stored ?? baseProfile!) with
        {
            DeviceId = KeyFor(deviceId, 2),
        };

        var migrated = HasLeaderEntry(slotTwo, leaderButton.Value)
            ? slotTwo
            : slotTwo with
            {
                Mappings = [.. slotTwo.Mappings, LeaderEntry(leaderButton.Value, leaderMode)],
            };

        SaveSlot(deviceId, 2, migrated);
        return migrated;
    }

    /// <summary>
    /// Whether the profile already carries a leader mapping for this button. Detection is
    /// by source and target shape rather than entry identity, so a re-run after the entry
    /// was edited still recognizes it instead of appending a second one.
    /// </summary>
    private static bool HasLeaderEntry(GamepadProfile profile, GamepadButton leaderButton)
        => profile.Mappings.Any(mapping =>
            mapping.Source is ButtonBinding { Button: var button } && button == leaderButton
            && mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader });

    private static MappingEntry LeaderEntry(GamepadButton leaderButton, LeaderMode leaderMode)
    {
        var switchMode = leaderMode == LeaderMode.Hold ? LeaderSwitchMode.Hold : LeaderSwitchMode.Press;

        return new MappingEntry
        {
            Source = new ButtonBinding(leaderButton),
            Target = new SpecialKeyTarget(
                SpecialKeyKind.Leader,
                PrecisionMode.Toggle,
                switchMode,
                SlotA: MinSlot,
                SlotB: 2),
            Enabled = true,
        };
    }

    private static void ValidateSlot(int slot)
    {
        if (slot < MinSlot)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slot),
                slot,
                $"preset slots run from {MinSlot} upward");
        }
    }
}
