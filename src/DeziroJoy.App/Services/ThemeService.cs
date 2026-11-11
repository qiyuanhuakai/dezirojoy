using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Styling;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;

namespace DeziroJoy.App.Services;

/// <summary>
/// Owns the persisted app preferences in <c>settings.json</c> beside the profile store
/// (%APPDATA%\DeziroJoy on Windows, ~/.config/DeziroJoy on Linux): the light/dark
/// choice, gamepad UI ownership, the independent close/minimize tray policies and the
/// startup release check. A missing or corrupt file falls back to the documented
/// defaults rather than throwing.
/// </summary>
/// <remarks>
/// The variant is applied from code rather than declared on the Application
/// element in App.axaml: a XAML <c>RequestedThemeVariant</c> is a local value
/// that would pin the app and block the runtime switch.
/// </remarks>
public sealed class ThemeService
{
    private const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new ThemeVariantConverter() },
    };

    private readonly string _settingsPath;
    private AppSettings _settings = new();

    public ThemeService(string? directory = null)
    {
        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeziroJoy");

        Directory.CreateDirectory(root);
        _settingsPath = Path.Combine(root, SettingsFileName);
    }

    /// <summary>Absolute path of the persisted preference file.</summary>
    public string SettingsPath => _settingsPath;

    /// <summary>The variant currently applied to <see cref="Application.Current"/>.</summary>
    public ThemeVariant Current { get; private set; } = ThemeVariant.Dark;

    /// <summary>Raised whenever the applied variant changes, so every entry point can resync.</summary>
    public event Action<ThemeVariant>? Changed;

    /// <summary>Raised whenever the gamepad-navigation preference changes.</summary>
    public event Action<bool>? GamepadNavigationChanged;

    public bool IsLight => Current == ThemeVariant.Light;

    /// <summary>
    /// Whether gamepad presses may drive the UI. Off means the pad only highlights the
    /// model and feeds the mapping editor; it never navigates and never opens the editor.
    /// </summary>
    public bool GamepadNavigationEnabled => _settings.GamepadNavigationEnabled;

    /// <summary>Whether closing the window hands the resident role to the tray.</summary>
    public bool CloseToTrayEnabled => _settings.CloseToTrayEnabled;

    /// <summary>Whether minimizing the window hands the resident role to the tray.</summary>
    public bool MinimizeToTrayEnabled => _settings.MinimizeToTrayEnabled;

    /// <summary>
    /// Whether the app checks GitHub for a newer release once at startup. Off by default: a
    /// launch-time network call is something the user opts into, and a manual check is always
    /// one click away in the settings.
    /// </summary>
    public bool AutoCheckUpdatesEnabled => _settings.AutoCheckUpdatesEnabled;

    /// <summary>
    /// Reads the persisted preferences. Any unreadable, malformed or unknown
    /// value degrades to the documented default rather than throwing.
    /// </summary>
    public ThemeVariant Load()
    {
        _settings = new AppSettings();

        try
        {
            if (File.Exists(_settingsPath))
            {
                _settings = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(_settingsPath), SerializerOptions) ?? new AppSettings();
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return _settings.Theme ?? ThemeVariant.Dark;
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, SerializerOptions));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Writes the preferences. A read-only or unwritable settings directory must
    /// not take the app down, so IO failures are swallowed.
    /// </summary>
    public void Save(ThemeVariant variant)
    {
        _settings = _settings with { Theme = variant };
        Save();
    }

    /// <summary>Turns gamepad-driven navigation on or off and persists it.</summary>
    public void SetGamepadNavigation(bool enabled)
    {
        if (_settings.GamepadNavigationEnabled == enabled)
        {
            return;
        }

        _settings = _settings with { GamepadNavigationEnabled = enabled };
        Save();
        GamepadNavigationChanged?.Invoke(enabled);
    }

    public void SetCloseToTrayEnabled(bool enabled)
    {
        if (_settings.CloseToTrayEnabled == enabled)
        {
            return;
        }

        _settings = _settings with { CloseToTrayEnabled = enabled };
        Save();
    }

    public void SetMinimizeToTrayEnabled(bool enabled)
    {
        if (_settings.MinimizeToTrayEnabled == enabled)
        {
            return;
        }

        _settings = _settings with { MinimizeToTrayEnabled = enabled };
        Save();
    }

    /// <summary>Turns the startup release check on or off and persists it.</summary>
    public void SetAutoCheckUpdates(bool enabled)
    {
        if (_settings.AutoCheckUpdatesEnabled == enabled)
        {
            return;
        }

        _settings = _settings with { AutoCheckUpdatesEnabled = enabled };
        Save();
    }

    /// <summary>
    /// The retired leader button, read for the one-shot migration into the preset slots
    /// only. New saves never write it, so this is null on every settings file written
    /// after the retirement.
    /// </summary>
    internal GamepadButton? LegacyLeaderButton => _settings.LeaderButton;

    /// <summary>The retired leader mode, read for the one-shot migration only.</summary>
    internal LeaderMode LegacyLeaderMode => _settings.LeaderMode;

    /// <summary>
    /// Clears the retired leader settings and persists the change, so the migration runs
    /// at most once per stored legacy entry.
    /// </summary>
    internal void ClearLegacyLeader()
    {
        if (_settings.LeaderButton is null && _settings.LeaderMode == LeaderMode.Press)
        {
            return;
        }

        _settings = _settings with { LeaderButton = null, LeaderMode = LeaderMode.Press };
        Save();
    }

    /// <summary>
    /// Applies <paramref name="variant"/> to the running application. Setting it
    /// on <see cref="Application"/> (never on a window) is what lets the change
    /// propagate to every open window and its whole visual tree at runtime.
    /// </summary>
    public void Apply(ThemeVariant variant)
    {
        Current = variant;

        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = variant;
        }

        Changed?.Invoke(variant);
    }

    /// <summary>Loads the persisted preference and applies it.</summary>
    public ThemeVariant ApplyPersisted()
    {
        var variant = Load();
        Apply(variant);

        return variant;
    }

    /// <summary>Flips between Light and Dark, applies it and persists it.</summary>
    public void Toggle()
    {
        var next = Current == ThemeVariant.Light ? ThemeVariant.Dark : ThemeVariant.Light;

        Apply(next);
        Save(next);
    }

    private sealed record AppSettings
    {
        public ThemeVariant? Theme { get; init; }

        public bool GamepadNavigationEnabled { get; init; } = true;

        public bool CloseToTrayEnabled { get; init; } = true;

        public bool MinimizeToTrayEnabled { get; init; } = true;

        public bool AutoCheckUpdatesEnabled { get; init; }

        /// <summary>
        /// The retired leader pair. Kept so an old settings.json still deserializes and can
        /// be migrated, but never written again: the leader key is a preset-slot mapping now.
        /// The condition is write-only, so a file written before the retirement still reads
        /// back while every save after the migration drops the pair.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public GamepadButton? LeaderButton { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public LeaderMode LeaderMode { get; init; } = LeaderMode.Press;
    }

    /// <summary>
    /// Persists the variant as its key ("Light" / "Dark"). Anything that is not
    /// the string "Light" reads back as Dark, so an unknown value can never
    /// leave the app in an undefined theme.
    /// </summary>
    private sealed class ThemeVariantConverter : JsonConverter<ThemeVariant>
    {
        public override ThemeVariant Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                reader.Skip();

                return ThemeVariant.Dark;
            }

            var name = reader.GetString();

            return string.Equals(name, "Light", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        }

        public override void Write(Utf8JsonWriter writer, ThemeVariant value, JsonSerializerOptions options)
            => writer.WriteStringValue(value == ThemeVariant.Light ? "Light" : "Dark");
    }
}
