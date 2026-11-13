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
/// <para>
/// The slots are independent in every respect but one: the special keys - precision and
/// leader <see cref="SpecialKeyTarget"/>s, whether they sit on a mapping's own target or
/// on a button gesture's action - are device-global. Every write that lands a special key
/// fans it out to the device's other slots (<see cref="SyncSpecialKeys"/>), and a device
/// whose slots predate that rule is converged onto one subset (<see cref="CanonicalizeSpecialKeys"/>).
/// </para>
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
    /// Creates any missing slot as an independent copy of the device's primary profile, on the
    /// device's first use only. Existing slots are never overwritten. The returned profile is
    /// the one the runtime should activate for <paramref name="activeSlot"/>, tagged with the
    /// slot it actually loaded; when the device lacks <paramref name="activeSlot"/> it falls
    /// back to the primary anchor so a reconnect still succeeds without resurrecting a deleted
    /// slot or moving the app-wide slot.
    /// <para>
    /// Every call also converges the device's slots onto one device-global special-key subset
    /// (<see cref="CanonicalizeSpecialKeys"/>), so the returned profile already carries it.
    /// </para>
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

        // Special keys are device-global, so every slot is converged onto one subset here:
        // a fresh device's copies start identical and stay that way, and a device whose
        // slots predate the sharing rule - or a store edited by hand - heals on reconnect.
        // Idempotent, so a device already in agreement is not rewritten.
        CanonicalizeSpecialKeys(descriptor.DeviceId);

        // A device that lacks the requested active slot - it deleted it, or never had it -
        // reconnects to a surviving profile rather than failing runtime creation: fall back to
        // the primary anchor, which is always ensured above. The returned profile carries the
        // slot it actually loaded, so the runtime is tagged with the slot it truly runs; the
        // deleted slot is not resurrected and the app-wide slot is not changed here.
        if (LoadSlot(descriptor.DeviceId, activeSlot) is { } requested)
        {
            return requested with { PresetSlot = activeSlot };
        }

        if (LoadSlot(descriptor.DeviceId, MinSlot) is { } anchor)
        {
            return anchor with { PresetSlot = MinSlot };
        }

        throw new InvalidOperationException($"preset slot {MinSlot} was not initialized for {descriptor.DeviceId}");
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
    /// 1. Returns the migrated slot-2 profile as it stands after the device's slots have
    /// been converged onto one special-key subset, or null when there was nothing to
    /// migrate.
    /// <para>
    /// The leader is device-global, so the conversion propagates it to every slot rather
    /// than writing it into slot 2 alone: slot 1 keeps priority for a leader it already
    /// carried, and a special key only the alternate had is merged in rather than dropped.
    /// </para>
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

        // The leader is device-global, so the migration converges the whole device onto one
        // special-key subset: the leader lands in every slot, slot 1 keeps priority for a
        // leader it already carried, and a special key only the alternate (or any other
        // non-primary slot) had is merged in rather than dropped.
        CanonicalizeSpecialKeys(deviceId);

        return LoadSlot(deviceId, 2);
    }

    // ---- Device-global special keys -------------------------------------------

    /// <summary>
    /// Fans one profile's device-global special-key subset out to every slot of the device.
    /// Each sibling slot keeps its own mappings and entry ids, but its special-key subset -
    /// every precision and leader <see cref="SpecialKeyTarget"/>, on a mapping's own target
    /// or on a gesture action - becomes exactly <paramref name="canonical"/>'s. This is the
    /// write-time half of the sharing rule: an edit that adds, changes or removes a special
    /// key lands in every preset at once, while every other mapping stays where it was
    /// written.
    /// <para>
    /// Store-level and idempotent: a slot already carrying the subset is not re-saved, and
    /// the fan-out never re-enters the apply path that called it, so no revision is bumped
    /// twice and no snapshot is pushed from inside a write. The caller persists
    /// <paramref name="canonical"/> to its own slot first; syncing it back onto that slot is
    /// a no-op unless the profile carried the same key twice, which the rewrite normalizes.
    /// </para>
    /// </summary>
    public void SyncSpecialKeys(string deviceId, GamepadProfile canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);

        WriteSpecialKeys(deviceId, SlotsInUse(deviceId), ExtractSpecialKeys(canonical));
    }

    /// <summary>
    /// Converges every slot of the device onto one device-global special-key subset - the
    /// migration half of the sharing rule, for a device whose slots predate it or a store
    /// edited by hand. Slot 1, the primary anchor, has priority; a special key that exists
    /// only in a later slot is merged in rather than dropped, so a leader or precision key
    /// no other slot carries survives. Where two slots place different kinds on the same
    /// source at the same position - one click cannot be two special keys - the earlier slot
    /// wins.
    /// <para>
    /// Idempotent by shape: a device already in agreement is not rewritten, so a reconnect
    /// may run this on every connect. Returns whether any slot was rewritten.
    /// </para>
    /// </summary>
    public bool CanonicalizeSpecialKeys(string deviceId)
    {
        var slots = SlotsInUse(deviceId);
        if (slots.Count == 0)
        {
            return false;
        }

        var canonical = new Dictionary<SpecialKeyId, SpecialKeyValue>();
        var placed = new HashSet<(GamepadButton Button, int Code, SpecialKeyPosition Position)>();

        foreach (var slot in slots)
        {
            if (LoadSlot(deviceId, slot) is not { } profile)
            {
                continue;
            }

            foreach (var (id, value) in ExtractSpecialKeys(profile))
            {
                // An earlier slot already placed a special key on this source at this
                // position; slot-1 priority means that one wins and this one is superseded.
                if (placed.Add((id.Button, id.Code, id.Position)))
                {
                    canonical[id] = value;
                }
            }
        }

        return WriteSpecialKeys(deviceId, slots, canonical);
    }

    /// <summary>
    /// The device-global special keys the device has in effect, in slot-then-profile order.
    /// The subset is the one <see cref="CanonicalizeSpecialKeys"/> would converge the slots
    /// onto - every slot read in ascending order, the first occurrence of each identity
    /// winning - so this describes the device as it stands without writing anything. It is
    /// what a scheme bundle exports as its special-key section, and what an import preview
    /// shows as the keys the device will end up with.
    /// </summary>
    public IReadOnlyList<DeviceSpecialKey> SpecialKeysInEffect(string deviceId)
        => SpecialKeysInEffect(
            SlotsInUse(deviceId)
                .Select(slot => LoadSlot(deviceId, slot))
                .Where(profile => profile is not null)
                .Select(profile => profile!));

    /// <summary>
    /// The device-global special keys a set of profiles carries, under the same rule the
    /// device walk uses: the profiles in the order given, the first occurrence of each
    /// identity winning. A scheme bundle uses it over its own presets, so the subset a
    /// bundle installs is decided exactly the way a device's would be.
    /// </summary>
    public static IReadOnlyList<DeviceSpecialKey> SpecialKeysInEffect(IEnumerable<GamepadProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var keys = new List<DeviceSpecialKey>();
        var seen = new HashSet<SpecialKeyId>();

        foreach (var profile in profiles)
        {
            foreach (var (id, value) in ExtractSpecialKeys(profile))
            {
                if (seen.Add(id))
                {
                    keys.Add(new DeviceSpecialKey(
                        id.Button, id.Code, id.Kind, id.Position, value.Target, value.Enabled));
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// Fans an explicit device-global special-key subset out to every slot of the device -
    /// the write half of the sharing rule driven by a listed set rather than by one profile.
    /// A scheme import uses it: the bundle names the keys the device will carry, so the
    /// profiles alone do not have to be the ones that introduce them, and a key the bundle
    /// lists but no preset carries is installed rather than dropped. Where the list and a
    /// slot disagree, the list wins - the same collision rule
    /// <see cref="SyncSpecialKeys(string, GamepadProfile)"/> applies, one source at one
    /// position holding one thing.
    /// </summary>
    public void SyncSpecialKeys(string deviceId, IReadOnlyList<DeviceSpecialKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var canonical = new Dictionary<SpecialKeyId, SpecialKeyValue>();
        foreach (var key in keys)
        {
            // The bundle's section is data, not a type guarantee: anything that is not a
            // special key cannot occupy a special-key position and is left out rather than
            // written where the fan-out would put it.
            if (key.Target is SpecialKeyTarget target)
            {
                canonical[new SpecialKeyId(key.Button, key.Code, key.Kind, key.Position)] =
                    new SpecialKeyValue(target, key.Enabled);
            }
        }

        WriteSpecialKeys(deviceId, SlotsInUse(deviceId), canonical);
    }

    /// <summary>
    /// Replaces the device's whole preset set with the scheme's: every slot the scheme names
    /// is written, and every slot the device holds that the scheme does not name is removed.
    /// This is the store half of a scheme import - one operation, so the device is never seen
    /// half-imported. Slot numbers are preserved rather than renumbered, because a leader key
    /// names the two slots it flips between: a device whose live slots are 1, 3 and 4 keeps
    /// that shape, and its leader keeps pointing where the user aimed it. The profiles are
    /// rebound to their slot keys and tagged with the slot they are stored in, exactly the way
    /// <see cref="SaveSlot"/> stores any other slot.
    /// <para>
    /// The removed slots are deleted outright rather than through <see cref="DeleteSlot"/>:
    /// that method repairs leader references and refuses to leave a device preset-less, both
    /// of which are the scheme's own business - a scheme always carries its base preset, and
    /// its leaders name slots it carries, which the import validates before it gets here.
    /// </para>
    /// </summary>
    public void ReplaceScheme(string deviceId, IReadOnlyDictionary<int, GamepadProfile> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        if (slots.Count == 0)
        {
            throw new ArgumentException("a scheme carries at least its base preset", nameof(slots));
        }

        foreach (var slot in slots.Keys)
        {
            ValidateSlot(slot);
        }

        foreach (var slot in SlotsInUse(deviceId).Where(slot => !slots.ContainsKey(slot)))
        {
            _store.Delete(KeyFor(deviceId, slot));
        }

        foreach (var (slot, profile) in slots.OrderBy(pair => pair.Key))
        {
            SaveSlot(deviceId, slot, profile with { PresetSlot = slot });
        }
    }

    /// <summary>
    /// The device's current preset set as a snapshot: every slot in use mapped to the profile
    /// it holds. The caller keeps it across a scheme import so the replace can be undone, or
    /// rolled back when the reload that follows it fails.
    /// </summary>
    public IReadOnlyDictionary<int, GamepadProfile?> CaptureSlots(string deviceId)
    {
        var snapshot = new Dictionary<int, GamepadProfile?>();

        foreach (var slot in SlotsInUse(deviceId))
        {
            snapshot[slot] = LoadSlot(deviceId, slot);
        }

        return snapshot;
    }

    /// <summary>
    /// Restores a device's preset set to a captured snapshot exactly: every slot the snapshot
    /// holds is written back, and every slot the device holds that the snapshot does not name
    /// is removed. The counterpart of <see cref="ReplaceScheme"/> - an import's undo, or a
    /// half-landed import's rollback - so the device ends up precisely where it started,
    /// including slots the replace created and slots it deleted.
    /// </summary>
    public void RestoreSlots(string deviceId, IReadOnlyDictionary<int, GamepadProfile?> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        foreach (var slot in SlotsInUse(deviceId).Where(slot => !snapshot.ContainsKey(slot)))
        {
            _store.Delete(KeyFor(deviceId, slot));
        }

        foreach (var (slot, profile) in snapshot)
        {
            if (profile is null)
            {
                _store.Delete(KeyFor(deviceId, slot));
                continue;
            }

            SaveSlot(deviceId, slot, profile);
        }
    }

    /// <summary>
    /// Rewrites each listed slot's special-key subset to <paramref name="canonical"/>,
    /// saving only the slots that actually changed. Returns whether any slot was rewritten.
    /// </summary>
    private bool WriteSpecialKeys(
        string deviceId,
        IReadOnlyList<int> slots,
        IReadOnlyDictionary<SpecialKeyId, SpecialKeyValue> canonical)
    {
        var rewritten = false;

        foreach (var slot in slots)
        {
            if (LoadSlot(deviceId, slot) is not { } stored)
            {
                continue;
            }

            var (updated, changed) = ApplySpecialKeys(stored, canonical);
            if (changed)
            {
                SaveSlot(deviceId, slot, updated);
                rewritten = true;
            }
        }

        return rewritten;
    }

    /// <summary>
    /// The device-global special keys one profile carries, keyed by identity and in profile
    /// order. A key sits on a mapping's own target or on one of its gesture's two actions;
    /// the first occurrence wins when a hand-edited profile carries the same key twice, so
    /// the result stays deterministic.
    /// </summary>
    private static Dictionary<SpecialKeyId, SpecialKeyValue> ExtractSpecialKeys(GamepadProfile profile)
    {
        var keys = new Dictionary<SpecialKeyId, SpecialKeyValue>();

        foreach (var mapping in profile.Mappings)
        {
            if (mapping.Source is not ButtonBinding button)
            {
                continue;
            }

            if (mapping.Target is SpecialKeyTarget target)
            {
                keys.TryAdd(
                    new SpecialKeyId(button.Button, button.Code, target.Kind, SpecialKeyPosition.Target),
                    new SpecialKeyValue(target, mapping.Enabled));
            }

            if (button.Gesture is not { } gesture)
            {
                continue;
            }

            if (gesture.DoubleClickAction is SpecialKeyTarget doubleClick)
            {
                keys.TryAdd(
                    new SpecialKeyId(button.Button, button.Code, doubleClick.Kind, SpecialKeyPosition.DoubleClick),
                    new SpecialKeyValue(doubleClick, Enabled: true));
            }

            if (gesture.LongPressAction is SpecialKeyTarget longPress)
            {
                keys.TryAdd(
                    new SpecialKeyId(button.Button, button.Code, longPress.Kind, SpecialKeyPosition.LongPress),
                    new SpecialKeyValue(longPress, Enabled: true));
            }
        }

        return keys;
    }

    /// <summary>
    /// One profile rewritten so its special-key subset is exactly <paramref name="canonical"/>'s.
    /// A key the shared set carries is updated in place - a mapping's own target replaced, its
    /// entry id and enabled state preserved; a key it no longer carries is removed - the whole
    /// entry for a target key, since that entry's target IS the key, or just the action for a
    /// gesture key, whose timing and re-fire-the-click fallback stay; a key the profile cannot
    /// express in place is added, appended after the mappings it already had. Every other
    /// mapping, and every entry id, comes back exactly as it went in.
    /// <para>
    /// The rewrite never persists a shape the engine would reject: where the shared set claims
    /// a button's long press, a v1 complex shortcut stored on that button is normalized first
    /// (<see cref="NormalizeComplexShortcutShape"/>), because the engine normalizes that shape
    /// in memory at build time and never writes it back - the store has to.
    /// </para>
    /// </summary>
    private static (GamepadProfile Profile, bool Changed) ApplySpecialKeys(
        GamepadProfile profile, IReadOnlyDictionary<SpecialKeyId, SpecialKeyValue> canonical)
    {
        var changed = false;
        var mappings = new List<MappingEntry>(profile.Mappings.Count);
        var present = new HashSet<SpecialKeyId>();
        var appendedGestures = new HashSet<(GamepadButton Button, int Code)>();

        foreach (var mapping in profile.Mappings)
        {
            if (mapping.Source is not ButtonBinding button)
            {
                mappings.Add(mapping);
                continue;
            }

            // The shared set claims this button's long press. A v1 complex shortcut stored
            // there - the program as the mapping's own target with a bare long press - would
            // be persisted as the one shape the engine rejects: a complex-shortcut target
            // paired with a long-press action. Normalizing it first, exactly as the engine
            // would, leaves the position holding the program as an ordinary action, which the
            // gesture sync below then displaces per the collision rule - so what lands in the
            // store is always a shape the engine builds.
            var entry = SpecialKeyAt(canonical, button.Button, button.Code, SpecialKeyPosition.LongPress) is not null
                ? NormalizeComplexShortcutShape(mapping)
                : mapping;
            var source = entry.Source as ButtonBinding ?? button;
            var desiredGesture = DesiredGesture(source.Gesture, canonical, source.Button, source.Code);
            var gestureChanged = desiredGesture != source.Gesture;

            if (entry.Target is SpecialKeyTarget target)
            {
                var id = new SpecialKeyId(source.Button, source.Code, target.Kind, SpecialKeyPosition.Target);
                present.Add(id);

                if (canonical.TryGetValue(id, out var value))
                {
                    if (target != value.Target)
                    {
                        entry = entry with { Target = value.Target };
                    }

                    if (entry.Enabled != value.Enabled)
                    {
                        entry = entry with { Enabled = value.Enabled };
                    }
                }
                else if (CarriesSpecialKey(desiredGesture))
                {
                    // The key is gone from the shared set, so the entry's own target goes
                    // with it - but its gesture carries a shared key of its own that
                    // dropping the entry would lose, so the entry stays as a bare gesture
                    // whose click does nothing.
                    entry = entry with { Target = new NoneTarget() };
                }
                else
                {
                    // The entry's target IS the key and nothing else on it is shared, so the
                    // whole entry goes with it.
                    changed = true;
                    continue;
                }
            }

            if (gestureChanged)
            {
                entry = entry with { Source = source with { Gesture = desiredGesture } };
            }

            if (!ReferenceEquals(entry, mapping))
            {
                changed = true;
            }

            mappings.Add(entry);

            // Whatever gesture this mapping ends up carrying marks its positions as present,
            // so the add phase below never duplicates a key the profile already expresses -
            // including a gesture key riding on an entry whose own target is another key.
            if (desiredGesture is { } carried)
            {
                if (carried.DoubleClickAction is SpecialKeyTarget doubleClick)
                {
                    present.Add(new SpecialKeyId(source.Button, source.Code, doubleClick.Kind, SpecialKeyPosition.DoubleClick));
                }

                if (carried.LongPressAction is SpecialKeyTarget longPress)
                {
                    present.Add(new SpecialKeyId(source.Button, source.Code, longPress.Kind, SpecialKeyPosition.LongPress));
                }
            }
        }

        // Keys the shared set carries that this profile could not express in place.
        foreach (var (id, value) in canonical)
        {
            if (present.Contains(id))
            {
                continue;
            }

            changed = true;

            if (id.Position == SpecialKeyPosition.Target)
            {
                mappings.Add(new MappingEntry
                {
                    Source = new ButtonBinding(id.Button, id.Code),
                    Target = value.Target,
                    Enabled = value.Enabled,
                });
                continue;
            }

            // A gesture key on a button this profile does not map at all. One entry carries
            // every key that button has, and its own click is 无功能 - no non-special mapping
            // is invented for the slot.
            if (appendedGestures.Add((id.Button, id.Code)))
            {
                mappings.Add(new MappingEntry
                {
                    Source = new ButtonBinding(id.Button, id.Code)
                    {
                        Gesture = DesiredGesture(null, canonical, id.Button, id.Code),
                    },
                    Target = new NoneTarget(),
                    Enabled = true,
                });
            }
        }

        return (changed ? profile with { Mappings = [.. mappings] } : profile, changed);
    }

    /// <summary>
    /// The v1 complex-shortcut shape rewritten into the shape the engine runs, with the same
    /// semantics as the engine's own normalizer: the program moves off the mapping's own
    /// target onto the gesture's long-press action, and the click becomes 无功能 - what a
    /// quick press did when the program owned the target. The engine normalizes this in
    /// memory when it builds a profile and never writes the result back, so a store that
    /// rewrites a profile has to do it itself: leaving a complex-shortcut target paired with
    /// a long-press action behind is the one shape the engine rejects outright. Only the
    /// exact v1 shape is rewritten - a stick source, no long press, a click auto-repeat or a
    /// long-press action of its own is one the engine already rejects as it stands, and
    /// repairing those is not this layer's business.
    /// </summary>
    private static MappingEntry NormalizeComplexShortcutShape(MappingEntry mapping)
    {
        if (mapping.Target is not ComplexShortcutTarget complex
            || mapping.Source is not ButtonBinding button
            || button.Gesture is not { } gesture
            || !gesture.HasEffectiveLongPress
            || gesture.LongPressAction is not null)
        {
            return mapping;
        }

        return mapping with
        {
            Target = new NoneTarget(),
            Source = button with
            {
                Gesture = gesture with { LongPressAction = complex },
            },
        };
    }

    /// <summary>
    /// Whether a (already synchronized) gesture carries a special key of its own. Only a
    /// canonical action survives <see cref="DesiredGesture"/>, so any special key it carries is
    /// one the shared set names - which is what makes dropping the entry that owns the gesture
    /// lose a shared key.
    /// </summary>
    private static bool CarriesSpecialKey(ButtonGesture? gesture)
        => gesture is { } carried
            && (carried.DoubleClickAction is SpecialKeyTarget || carried.LongPressAction is SpecialKeyTarget);

    /// <summary>
    /// The gesture a slot's button must carry so every shared gesture-position key for it is
    /// present: the slot's own gesture when it has one - its timing, including a position it
    /// deliberately left disabled, is the slot's own and stays - with only the actions
    /// synchronized; a freshly materialized gesture at the pinned default timings when the
    /// button has no gesture at all, which is the only way the shared key can be expressed
    /// there. A position the shared set does not name keeps any non-special action it already
    /// had and loses only a special-key action. The result equals the gesture the slot already
    /// carries when nothing about it changes, so the caller can compare and skip the write.
    /// <para>
    /// Collision rule: a position the shared set names takes the shared key, whatever occupied
    /// it - a stale special key, or a per-preset complex shortcut program (the engine builds a
    /// v1 program as a long-press action, so by the time this runs the program is an ordinary
    /// action there and the shared key displaces it). One source at one position holds one
    /// thing, and for a special-key position that thing is the device-global value.
    /// </para>
    /// </summary>
    private static ButtonGesture? DesiredGesture(
        ButtonGesture? existing,
        IReadOnlyDictionary<SpecialKeyId, SpecialKeyValue> canonical,
        GamepadButton button,
        int code)
    {
        var doubleClick = SpecialKeyAt(canonical, button, code, SpecialKeyPosition.DoubleClick);
        var longPress = SpecialKeyAt(canonical, button, code, SpecialKeyPosition.LongPress);

        if (doubleClick is null && longPress is null)
        {
            if (existing is null)
            {
                return null;
            }

            var cleared = existing;
            if (existing.DoubleClickAction is SpecialKeyTarget)
            {
                cleared = cleared with { DoubleClickAction = null };
            }

            if (existing.LongPressAction is SpecialKeyTarget)
            {
                cleared = cleared with { LongPressAction = null };
            }

            return cleared;
        }

        var gesture = existing ?? new ButtonGesture();

        if (doubleClick is not null)
        {
            gesture = gesture with { DoubleClickAction = doubleClick };
            if (existing is null)
            {
                gesture = gesture with { DoubleClickWindow = ButtonGesture.DefaultDoubleClickWindow };
            }
        }
        else if (gesture.DoubleClickAction is SpecialKeyTarget)
        {
            gesture = gesture with { DoubleClickAction = null };
        }

        if (longPress is not null)
        {
            gesture = gesture with { LongPressAction = longPress };
            if (existing is null)
            {
                gesture = gesture with { LongPressThreshold = ButtonGesture.DefaultLongPressThreshold };
            }
        }
        else if (gesture.LongPressAction is SpecialKeyTarget)
        {
            gesture = gesture with { LongPressAction = null };
        }

        return gesture;
    }

    /// <summary>
    /// The special key the shared set places on this source at this position, or null when it
    /// places none. A well-formed set holds at most one kind per position - one action slot
    /// cannot carry two special keys - and the first kind in declaration order wins if a set
    /// ever disagreed with itself.
    /// </summary>
    private static SpecialKeyTarget? SpecialKeyAt(
        IReadOnlyDictionary<SpecialKeyId, SpecialKeyValue> canonical,
        GamepadButton button,
        int code,
        SpecialKeyPosition position)
    {
        foreach (var kind in new[] { SpecialKeyKind.Precision, SpecialKeyKind.Leader })
        {
            if (canonical.TryGetValue(new SpecialKeyId(button, code, kind, position), out var value))
            {
                return value.Target;
            }
        }

        return null;
    }

    /// <summary>
    /// Where one device-global special key sits inside a profile: the mapping's own target, or
    /// one of the button gesture's two actions. Two special keys that share a source button and
    /// a <see cref="SpecialKeyKind"/> but sit in different positions are different keys - one
    /// button can be a precision key on its click and a leader on its long press.
    /// </summary>
    public enum SpecialKeyPosition
    {
        /// <summary>The mapping's own <see cref="MappingEntry.Target"/>.</summary>
        Target,

        /// <summary>The gesture's <see cref="ButtonGesture.DoubleClickAction"/>.</summary>
        DoubleClick,

        /// <summary>The gesture's <see cref="ButtonGesture.LongPressAction"/>.</summary>
        LongPress,
    }

    /// <summary>
    /// One device-global special key as it sits in a profile: the physical button that triggers
    /// it - the button plus the raw code that tells one extra button from another - its kind,
    /// the position it occupies, the target itself and whether the mapping that carries it is
    /// enabled. This is the whole identity of the key, so it can be listed, written to a scheme
    /// bundle and fanned back out to every slot of a device without the profile it came from.
    /// </summary>
    public sealed record DeviceSpecialKey(
        GamepadButton Button,
        int Code,
        SpecialKeyKind Kind,
        SpecialKeyPosition Position,
        MappingTarget Target,
        bool Enabled);

    /// <summary>
    /// The identity of one device-global special key: the physical button that triggers it -
    /// the button plus the raw code that tells one extra button from another - its kind, and
    /// the position it occupies. This is the key the fan-out and the migration match on.
    /// </summary>
    private readonly record struct SpecialKeyId(
        GamepadButton Button, int Code, SpecialKeyKind Kind, SpecialKeyPosition Position);

    /// <summary>
    /// What one device-global special key carries: the target itself and, for a mapping's own
    /// target, whether that mapping is enabled - a disabled special key is a different
    /// configuration from an enabled one. Gesture actions carry no enabled flag of their own.
    /// </summary>
    private sealed record SpecialKeyValue(SpecialKeyTarget Target, bool Enabled);

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
