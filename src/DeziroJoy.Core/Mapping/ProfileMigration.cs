using DeziroJoy.Core.Input;

namespace DeziroJoy.Core.Mapping;

/// <summary>
/// Forward-only profile upgrades. v1 stored the precision button inside each mouse-move
/// mapping's settings; v2 lifts it into one deduped special-key mapping per distinct
/// (button, mode) pair, so a single precision key can serve every stick and the editor
/// has one place to configure it. v3 merges the layer key into the leader: the separate
/// <see cref="SpecialKeyKind.Layer"/> target becomes a leader whose action switches the
/// temporary layer, and the several temporary layers a profile could carry fold into the
/// one that remains.
/// </summary>
public static class ProfileMigration
{
    public static GamepadProfile Migrate(GamepadProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.SchemaVersion >= GamepadProfile.CurrentSchemaVersion)
        {
            return profile;
        }

        var migrated = profile;

        if (migrated.SchemaVersion < 2)
        {
            migrated = LiftPrecisionButtons(migrated);
        }

        if (migrated.SchemaVersion < 3)
        {
            migrated = MergeLayerKeyIntoLeader(migrated);
        }

        return migrated;
    }

    /// <summary>
    /// v1 -&gt; v2: the precision button moves out of every mouse-move mapping's settings and
    /// into one deduped special-key mapping per distinct (button, mode) pair, and every
    /// curve strength is normalized onto the legal [0,1] range.
    /// </summary>
    private static GamepadProfile LiftPrecisionButtons(GamepadProfile profile)
    {
        var defaults = new MouseStickSettings();
        var mappings = new List<MappingEntry>(profile.Mappings);
        var seen = new HashSet<(GamepadButton Button, PrecisionMode Mode)>();
        var extracted = new List<MappingEntry>();

        for (var i = 0; i < mappings.Count; i++)
        {
            if (mappings[i].Target is not MouseMoveTarget mouse || !mappings[i].Enabled)
            {
                continue;
            }

            var settings = mouse.Settings;
            if (seen.Add((settings.PrecisionButton, settings.PrecisionMode)))
            {
                extracted.Add(new MappingEntry
                {
                    Source = new ButtonBinding(settings.PrecisionButton),
                    Target = new SpecialKeyTarget(SpecialKeyKind.Precision, settings.PrecisionMode),
                });
            }

            mappings[i] = mappings[i] with
            {
                Target = mouse with
                {
                    Settings = settings with
                    {
                        PrecisionButton = defaults.PrecisionButton,
                        PrecisionMode = defaults.PrecisionMode,
                    },
                },
            };
        }

        mappings.AddRange(extracted);

        // v1 profiles could store curve strength > 1, which extrapolated the response
        // curve and could reverse direction or leave [0,1]. The legal range is now [0,1],
        // so normalize it on every stick target while the profile is upgraded.
        for (var i = 0; i < mappings.Count; i++)
        {
            mappings[i] = mappings[i] with
            {
                Target = mappings[i].Target switch
                {
                    MouseMoveTarget mouse => mouse with
                    {
                        Settings = mouse.Settings with { CurveStrength = ClampStrength(mouse.Settings.CurveStrength) },
                    },
                    WheelScrollTarget wheel => wheel with
                    {
                        Settings = wheel.Settings with { CurveStrength = ClampStrength(wheel.Settings.CurveStrength) },
                    },
                    var other => other,
                },
            };
        }

        return profile with
        {
            SchemaVersion = 2,
            Mappings = mappings,
        };
    }

    /// <summary>
    /// v2 -&gt; v3: the layer key becomes a leader that switches the temporary layer, and the
    /// several temporary layers a profile could carry fold into the one that remains. The
    /// layer key held-only semantics are preserved exactly: an old layer target was always
    /// <see cref="LeaderSwitchMode.Hold"/>, and the merged leader keeps the switch mode the
    /// profile stored. A profile that carries neither shape is only stamped with the new
    /// schema version.
    /// </summary>
    private static GamepadProfile MergeLayerKeyIntoLeader(GamepadProfile profile)
    {
        var mappings = new List<MappingEntry>(profile.Mappings.Count);
        var changed = false;

        foreach (var mapping in profile.Mappings)
        {
            var migrated = mapping;

            if (mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Layer } layer)
            {
                migrated = migrated with
                {
                    Target = new SpecialKeyTarget(
                        SpecialKeyKind.Leader,
                        layer.PrecisionMode,
                        layer.SwitchMode,
                        layer.SlotA,
                        layer.SlotB,
                        LeaderAction.Layer),
                };
            }

            if (migrated.Layer != 0 && migrated.Layer != 1)
            {
                migrated = migrated with { Layer = 1 };
            }

            if (!Equals(migrated, mapping))
            {
                changed = true;
            }

            mappings.Add(migrated);
        }

        return changed
            ? profile with { SchemaVersion = 3, Mappings = mappings }
            : profile with { SchemaVersion = 3 };
    }

    private static float ClampStrength(float strength) => Math.Clamp(strength, 0f, 1f);
}
