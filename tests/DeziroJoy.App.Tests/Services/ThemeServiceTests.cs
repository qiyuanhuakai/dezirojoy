using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
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
    public void Tray_Preferences_Default_To_Enabled_When_The_File_Is_Missing_Or_Corrupt()
    {
        var missing = Service(_directory);
        missing.Load();

        Assert.True(missing.CloseToTrayEnabled);
        Assert.True(missing.MinimizeToTrayEnabled);

        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{ not json");
        var corrupt = Service(_directory);
        corrupt.Load();

        Assert.True(corrupt.CloseToTrayEnabled);
        Assert.True(corrupt.MinimizeToTrayEnabled);
    }

    [Fact]
    public void Tray_Preferences_Round_Trip_Independently_And_Preserve_Existing_Settings()
    {
        var service = Service(_directory);
        service.Save(ThemeVariant.Light);
        service.SetGamepadNavigation(false);
        service.SetCloseToTrayEnabled(false);

        var closeDisabled = Service(_directory);
        closeDisabled.Load();
        Assert.False(closeDisabled.CloseToTrayEnabled);
        Assert.True(closeDisabled.MinimizeToTrayEnabled);
        Assert.False(closeDisabled.GamepadNavigationEnabled);
        Assert.Equal(ThemeVariant.Light, closeDisabled.Load());

        closeDisabled.SetMinimizeToTrayEnabled(false);
        closeDisabled.SetCloseToTrayEnabled(true);

        var reloaded = Service(_directory);
        reloaded.Load();
        Assert.True(reloaded.CloseToTrayEnabled);
        Assert.False(reloaded.MinimizeToTrayEnabled);
        Assert.False(reloaded.GamepadNavigationEnabled);
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

    [Fact]
    public void The_Layer_Hint_Preferences_Default_To_Shown_Fixed_Bottom_Centre()
    {
        var service = Service(_directory);
        service.Load();

        Assert.True(service.LayerHintEnabled);
        Assert.Equal(LayerHintPositionMode.Fixed, service.OverlayPositionMode);
        Assert.Equal(LayerHintAnchor.BottomCenter, service.OverlayAnchor);
        Assert.Equal(
            new LayerHintPlacement(LayerHintPositionMode.Fixed, LayerHintAnchor.BottomCenter),
            service.LayerHintPlacement);
    }

    [Fact]
    public void The_Layer_Hint_Preferences_Round_Trip_Together_And_Preserve_The_Theme()
    {
        var service = Service(_directory);
        service.Save(ThemeVariant.Light);
        service.SetLayerHintEnabled(false);
        service.SetOverlayPositionMode(LayerHintPositionMode.FollowMouse);
        service.SetOverlayAnchor(LayerHintAnchor.TopRight);

        var reloaded = Service(_directory);
        reloaded.Load();

        Assert.False(reloaded.LayerHintEnabled);
        Assert.Equal(LayerHintPositionMode.FollowMouse, reloaded.OverlayPositionMode);
        Assert.Equal(LayerHintAnchor.TopRight, reloaded.OverlayAnchor);
        Assert.Equal(ThemeVariant.Light, reloaded.Load());
    }

    [Fact]
    public void A_Corrupt_Settings_File_Falls_The_Overlay_Preferences_Back_To_Their_Defaults()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{ not json");

        var service = Service(_directory);
        service.Load();

        Assert.True(service.LayerHintEnabled);
        Assert.Equal(LayerHintPositionMode.Fixed, service.OverlayPositionMode);
        Assert.Equal(LayerHintAnchor.BottomCenter, service.OverlayAnchor);
    }

    [Fact]
    public void An_Unknown_Overlay_Value_Falls_Back_Without_Failing_The_Whole_File()
    {
        // A hand-edited or newer settings file names an overlay value this build does not know:
        // that one preference degrades to its default, and the rest of the file still reads.
        File.WriteAllText(
            Path.Combine(_directory, "settings.json"),
            """{"Theme":"Light","OverlayAnchor":"BottomMiddleOfNowhere"}""");

        var service = Service(_directory);
        service.Load();

        Assert.Equal(ThemeVariant.Light, service.Load());
        Assert.Equal(LayerHintAnchor.BottomCenter, service.OverlayAnchor);
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
