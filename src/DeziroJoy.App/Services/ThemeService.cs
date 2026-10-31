using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Styling;

namespace DeziroJoy.App.Services;

/// <summary>
/// Owns the light/dark preference. Reads the persisted choice, applies it to
/// <see cref="Application.Current"/> at runtime, and writes it back on toggle.
/// The preference lives in <c>settings.json</c> beside the profile store
/// (%APPDATA%\DeziroJoy on Windows, ~/.config/DeziroJoy on Linux); a missing or
/// corrupt file falls back to <see cref="ThemeVariant.Dark"/>.
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

    public bool IsLight => Current == ThemeVariant.Light;

    /// <summary>
    /// Reads the persisted preference. Any unreadable, malformed or unknown
    /// value degrades to <see cref="ThemeVariant.Dark"/> rather than throwing.
    /// </summary>
    public ThemeVariant Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return ThemeVariant.Dark;
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(_settingsPath), SerializerOptions);

            return settings?.Theme ?? ThemeVariant.Dark;
        }
        catch (JsonException)
        {
            return ThemeVariant.Dark;
        }
        catch (IOException)
        {
            return ThemeVariant.Dark;
        }
    }

    /// <summary>
    /// Writes the preference. A read-only or unwritable settings directory must
    /// not take the app down, so IO failures are swallowed.
    /// </summary>
    public void Save(ThemeVariant variant)
    {
        try
        {
            var json = JsonSerializer.Serialize(new AppSettings { Theme = variant }, SerializerOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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

    private sealed class AppSettings
    {
        public ThemeVariant? Theme { get; init; }
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
