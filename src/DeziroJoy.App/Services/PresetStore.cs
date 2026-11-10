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
    /// Removes one slot's stored profile for a device and repairs every Leader reference that
    /// still pointed at it - the mapping's own target or a button gesture's double-click /
    /// long-press action - so a delete can never leave a leader key flipping toward a slot
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

    /// <summary>The result of trying to repair one Leader reference against a deleted slot.</summary>
    private enum LeaderRepair
    {
        /// <summary>The candidate is not a Leader naming the deleted slot; leave it untouched.</summary>
        Untouched,

        /// <summary>The Leader's deleted end was retargeted to a distinct surviving slot.</summary>
        Retargeted,

        /// <summary>No distinct survivor remains; the Leader cannot flip anywhere and must go.</summary>
        Dropped,
    }

    /// <summary>
    /// Rewrites every surviving profile's Leader reference that named <paramref name="deletedSlot"/>
    /// so it flips between two slots that still exist. A Leader reference lives in one of three
    /// places - the mapping's own <see cref="MappingEntry.Target"/>, or a button gesture's
    /// <see cref="ButtonGesture.DoubleClickAction"/> / <see cref="ButtonGesture.LongPressAction"/> -
    /// and all three are repaired, because the engine executes gesture actions exactly as it
    /// executes the mapping's own target. The deleted end is retargeted to the lowest surviving
    /// slot that keeps the pair distinct; when no distinct survivor remains the reference is
    /// removed - the whole mapping for a target leader (its <see cref="MappingEntry.Target"/> is
    /// required), or just the gesture action for a gesture leader (the gesture then re-fires the
    /// click, its timing and fallback intact). Profiles untouched by the delete are never re-saved.
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
                var updated = RepairMapping(mapping, deletedSlot, surviving);
                if (!ReferenceEquals(updated, mapping))
                {
                    repaired = true;
                }

                if (updated is not null)
                {
                    mappings.Add(updated);
                }
            }

            if (repaired)
            {
                SaveSlot(deviceId, slot, profile with { Mappings = [.. mappings] });
            }
        }
    }

    /// <summary>
    /// Repairs one mapping's Leader references, returning it unchanged when nothing named the
    /// deleted slot. A target leader with no distinct survivor is unswitchable, so the whole
    /// mapping is dropped (null); a gesture leader with no distinct survivor loses only its
    /// action, keeping the gesture's timing and its re-fire-the-click fallback.
    /// </summary>
    private static MappingEntry? RepairMapping(MappingEntry mapping, int deletedSlot, IReadOnlyList<int> surviving)
    {
        var targetRepair = TryRepairLeader(mapping.Target, deletedSlot, surviving, out var repairedTarget);
        if (targetRepair == LeaderRepair.Dropped)
        {
            return null;
        }

        var result = mapping;
        var changed = targetRepair == LeaderRepair.Retargeted;
        if (changed)
        {
            result = result with { Target = repairedTarget! };
        }

        if (mapping.Source is ButtonBinding { Gesture: { } gesture } button)
        {
            var newGesture = gesture;
            var gestureChanged = false;

            switch (TryRepairLeader(gesture.DoubleClickAction, deletedSlot, surviving, out var repairedDouble))
            {
                case LeaderRepair.Retargeted:
                    newGesture = newGesture with { DoubleClickAction = repairedDouble };
                    gestureChanged = true;
                    break;
                case LeaderRepair.Dropped:
                    newGesture = newGesture with { DoubleClickAction = null };
                    gestureChanged = true;
                    break;
            }

            switch (TryRepairLeader(gesture.LongPressAction, deletedSlot, surviving, out var repairedLong))
            {
                case LeaderRepair.Retargeted:
                    newGesture = newGesture with { LongPressAction = repairedLong };
                    gestureChanged = true;
                    break;
                case LeaderRepair.Dropped:
                    newGesture = newGesture with { LongPressAction = null };
                    gestureChanged = true;
                    break;
            }

            if (gestureChanged)
            {
                result = result with { Source = button with { Gesture = newGesture } };
                changed = true;
            }
        }

        return changed ? result : mapping;
    }

    /// <summary>
    /// Classifies one candidate Leader target against the deleted slot and, when it can be kept,
    /// produces the retargeted leader. A candidate that is not a Leader naming the deleted slot is
    /// <see cref="LeaderRepair.Untouched"/>; one that is, is retargeted to the lowest surviving
    /// slot other than its surviving end, or <see cref="LeaderRepair.Dropped"/> when none remains.
    /// </summary>
    private static LeaderRepair TryRepairLeader(
        MappingTarget? target, int deletedSlot, IReadOnlyList<int> surviving, out MappingTarget? repaired)
    {
        repaired = target;
        if (target is not SpecialKeyTarget { Kind: SpecialKeyKind.Leader } leader
            || (leader.SlotA != deletedSlot && leader.SlotB != deletedSlot))
        {
            return LeaderRepair.Untouched;
        }

        var other = leader.SlotA == deletedSlot ? leader.SlotB : leader.SlotA;
        var retarget = surviving.Where(candidate => candidate != other).DefaultIfEmpty(0).Min();
        if (retarget == 0)
        {
            return LeaderRepair.Dropped;
        }

        repaired = leader with
        {
            SlotA = leader.SlotA == deletedSlot ? retarget : leader.SlotA,
            SlotB = leader.SlotB == deletedSlot ? retarget : leader.SlotB,
        };
        return LeaderRepair.Retargeted;
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

        // Slot 1 is the device's own profile and the anchor the rest of the system reads from,
        // so its absence is the one reliable signal that the device has never been initialized.
        // Only then is the initial 2..N set materialized; afterwards the device owns its slot
        // set, and a slot the user deleted stays deleted across a reconnect rather than being
        // resurrected here on every EngineHost.EnsureRuntime. Only the primary anchor is ever
        // re-ensured on a later call.
        var firstMaterialization = LoadSlot(descriptor.DeviceId, MinSlot) is null;

        var primary = LoadSlot(descriptor.DeviceId, MinSlot)
            ?? DefaultProfileLibrary.CreateDefault(descriptor);
        if (LoadSlot(descriptor.DeviceId, MinSlot) is null)
        {
            SaveSlot(descriptor.DeviceId, MinSlot, primary);
        }

        if (firstMaterialization)
        {
            for (var slot = MinSlot + 1; slot <= DefaultSlotCount; slot++)
            {
                if (LoadSlot(descriptor.DeviceId, slot) is null)
                {
                    SaveSlot(descriptor.DeviceId, slot, primary with
                    {
                        Name = $"{primary.Name} · 预设 {slot}",
                        DeviceId = KeyFor(descriptor.DeviceId, slot),
                    });
                }
            }
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
