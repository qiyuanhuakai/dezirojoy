using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Profiles;

namespace DeziroJoy.App.Services;

/// <summary>
/// The preset-slot service layer over <see cref="IProfileStore"/>: the four slots of one
/// device are stored beside each other under derived keys, so a leader key can flip
/// between them and a reconnect restores whichever slot was saved. Slot 1 keeps the
/// device's own key, which is what lets an unmigrated profile read as the primary slot.
/// </summary>
public sealed class PresetStore
{
    /// <summary>Slots are numbered 1..4; slot 1 is the device's own profile.</summary>
    public const int MinSlot = 1;

    public const int MaxSlot = 4;

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

        for (var slot = MinSlot + 1; slot <= MaxSlot; slot++)
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

    /// <summary>The slots 1..4 that hold a profile for the device, in ascending order.</summary>
    public IReadOnlyList<int> SlotsInUse(string deviceId)
    {
        var slots = new List<int>();

        for (var slot = MinSlot; slot <= MaxSlot; slot++)
        {
            if (_store.Load(KeyFor(deviceId, slot)) is not null)
            {
                slots.Add(slot);
            }
        }

        return slots;
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
        if (slot is < MinSlot or > MaxSlot)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slot),
                slot,
                $"preset slots run from {MinSlot} to {MaxSlot}");
        }
    }
}
