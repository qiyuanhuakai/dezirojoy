using DeziroJoy.Core.Input;

namespace DeziroJoy.Core.Mapping;

/// <summary>
/// Forward-only profile upgrades. v1 stored the precision button inside each mouse-move
/// mapping's settings; v2 lifts it into one deduped special-key mapping per distinct
/// (button, mode) pair, so a single precision key can serve every stick and the editor
/// has one place to configure it.
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

        return profile with
        {
            SchemaVersion = GamepadProfile.CurrentSchemaVersion,
            Mappings = mappings,
        };
    }
}
