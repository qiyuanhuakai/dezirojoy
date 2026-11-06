using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// Covers persistence and restore of the light/dark preference. The settings
/// directory is injected so the tests never touch the real profile store.
/// </summary>
public sealed class ThemeServiceTests : IDisposable
{
    private readonly string _directory;

    public ThemeServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-theme-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private static ThemeService Service(string directory) => new(directory);

    [Fact]
    public void A_Missing_Settings_File_Falls_Back_To_Dark()
    {
        Assert.Equal(ThemeVariant.Dark, Service(_directory).Load());
    }

    [Fact]
    public void Saving_Light_Then_Loading_Restores_Light()
    {
        var service = Service(_directory);

        service.Save(ThemeVariant.Light);

        Assert.Equal(ThemeVariant.Light, service.Load());
    }

    [Fact]
    public void Saving_Dark_Then_Loading_Restores_Dark()
    {
        var service = Service(_directory);

        service.Save(ThemeVariant.Dark);

        Assert.Equal(ThemeVariant.Dark, service.Load());
    }

    [Fact]
    public void A_Corrupt_Settings_File_Falls_Back_To_Dark()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{ this is not json");

        Assert.Equal(ThemeVariant.Dark, Service(_directory).Load());
    }

    [Fact]
    public void An_Unknown_Theme_Value_Falls_Back_To_Dark()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), """{"Theme":"Neon"}""");

        Assert.Equal(ThemeVariant.Dark, Service(_directory).Load());
    }

    [Fact]
    public void A_Non_String_Theme_Value_Falls_Back_To_Dark()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), """{"Theme":42}""");

        Assert.Equal(ThemeVariant.Dark, Service(_directory).Load());
    }

    [Fact]
    public void The_Settings_File_Sits_Next_To_The_Profiles_Directory()
    {
        var service = Service(_directory);

        service.Save(ThemeVariant.Light);

        Assert.Equal(Path.Combine(_directory, "settings.json"), service.SettingsPath);
        Assert.True(File.Exists(service.SettingsPath));
    }

    [Fact]
    public void A_Missing_Settings_File_Leaves_Gamepad_Navigation_Enabled()
    {
        var service = Service(_directory);
        service.Load();

        Assert.True(service.GamepadNavigationEnabled);
    }

    [Fact]
    public void Disabling_Gamepad_Navigation_Round_Trips_And_Keeps_The_Theme()
    {
        var service = Service(_directory);

        service.Save(ThemeVariant.Light);
        service.SetGamepadNavigation(false);

        var reloaded = Service(_directory);
        reloaded.Load();

        Assert.False(reloaded.GamepadNavigationEnabled);
        Assert.Equal(ThemeVariant.Light, reloaded.Load());
    }

    [Fact]
    public void A_Corrupt_Settings_File_Leaves_Gamepad_Navigation_Enabled()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{ not json");

        var service = Service(_directory);

        Assert.Equal(ThemeVariant.Dark, service.Load());
        Assert.True(service.GamepadNavigationEnabled);
    }

    [Fact]
    public void The_Default_Directory_Matches_The_Profile_Store_Convention()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.SkipWhen(string.IsNullOrEmpty(appData), "This platform exposes no ApplicationData folder.");

        Assert.Equal(
            Path.Combine(appData, "DeziroJoy", "settings.json"),
            new ThemeService().SettingsPath);
    }

    [AvaloniaFact]
    public void Toggle_Flips_The_Variant_And_Persists_It()
    {
        var application = Application.Current!;
        var original = application.RequestedThemeVariant;
        var service = new ThemeService(_directory);

        try
        {
            service.Apply(ThemeVariant.Dark);
            service.Toggle();

            Assert.Equal(ThemeVariant.Light, service.Current);
            Assert.True(service.IsLight);
            Assert.Equal(ThemeVariant.Light, application.RequestedThemeVariant);
            Assert.Equal(ThemeVariant.Light, new ThemeService(_directory).Load());

            service.Toggle();

            Assert.Equal(ThemeVariant.Dark, service.Current);
            Assert.False(service.IsLight);
            Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);
            Assert.Equal(ThemeVariant.Dark, new ThemeService(_directory).Load());
        }
        finally
        {
            application.RequestedThemeVariant = original;
        }
    }

    [AvaloniaFact]
    public void ApplyPersisted_Applies_The_Stored_Preference()
    {
        var application = Application.Current!;
        var original = application.RequestedThemeVariant;

        try
        {
            new ThemeService(_directory).Save(ThemeVariant.Light);

            var service = new ThemeService(_directory);

            Assert.Equal(ThemeVariant.Light, service.ApplyPersisted());
            Assert.Equal(ThemeVariant.Light, application.RequestedThemeVariant);
        }
        finally
        {
            application.RequestedThemeVariant = original;
        }
    }
}
