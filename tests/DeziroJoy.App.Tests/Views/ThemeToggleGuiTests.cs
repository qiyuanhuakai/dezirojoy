using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.App.Views.Controls;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The runtime theme switch has no entry point in the window chrome, so the
/// persisted preference in <see cref="ThemeService"/> could never be changed
/// without editing settings.json by hand. These tests pin the title-bar toggle
/// that closes that gap.
/// </summary>
public sealed class ThemeToggleGuiTests : IDisposable
{
    private readonly string _directory;
    private readonly Application _application;
    private readonly ThemeVariant? _originalTheme;

    public ThemeToggleGuiTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-theme-toggle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _application = Application.Current!;
        _originalTheme = _application.RequestedThemeVariant;
    }

    public void Dispose()
    {
        _application.RequestedThemeVariant = _originalTheme;

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private (MainWindow Window, ThemeService Themes) BuildWindow()
    {
        var themes = new ThemeService(_directory);
        var store = new InMemoryProfileStore();
        var host = new EngineHost(new NoGamepadProvider(), new NullOutputSink(), store);
        var viewModel = new MainWindowViewModel(host, store);
        var window = new MainWindow(viewModel, host, themes);

        window.Show();

        return (window, themes);
    }

    private static ThemeToggleButton ToggleOf(Window window)
        => window.GetVisualDescendants().OfType<ThemeToggleButton>().Single();

    private static void Click(Control target, Window window)
    {
        window.UpdateLayout();
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_Title_Bar_Offers_A_Theme_Toggle_Left_Of_Minimize()
    {
        var (window, _) = BuildWindow();
        window.UpdateLayout();

        var toggle = ToggleOf(window);
        var minimize = window.FindControl<Button>("MinimizeButton")!;

        Assert.Same(toggle.Parent, minimize.Parent);
        Assert.Equal(44, toggle.Bounds.Width);
        Assert.Equal(32, toggle.Bounds.Height);
        Assert.True(
            toggle.Bounds.Right <= minimize.Bounds.Left,
            $"the toggle ends at {toggle.Bounds.Right} but minimize starts at {minimize.Bounds.Left}");
    }

    [AvaloniaFact]
    public void The_Title_Bar_Toggle_Receives_Input_Over_The_Chrome_Drag_Region()
    {
        var (window, _) = BuildWindow();
        var toggle = ToggleOf(window);

        Assert.Equal(
            WindowDecorationsElementRole.User,
            WindowDecorationProperties.GetElementRole(toggle));
    }

    [AvaloniaFact]
    public void Clicking_The_Toggle_Switches_Light_And_Dark_And_Persists_It()
    {
        var (window, themes) = BuildWindow();
        var toggle = ToggleOf(window);

        themes.Apply(ThemeVariant.Dark);
        Assert.Equal(ThemeVariant.Dark, _application.RequestedThemeVariant);

        Click(toggle, window);

        Assert.Equal(ThemeVariant.Light, _application.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Light, new ThemeService(_directory).Load());

        Click(toggle, window);

        Assert.Equal(ThemeVariant.Dark, _application.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Dark, new ThemeService(_directory).Load());
    }

    [AvaloniaFact]
    public void The_Settings_Appearance_Radios_Switch_The_Whole_App()
    {
        var (window, themes) = BuildWindow();
        themes.Apply(ThemeVariant.Dark);

        Click(window.FindControl<ToggleButton>("SettingsButton")!, window);

        var panel = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
        var radios = panel.GetVisualDescendants().OfType<RadioButton>()
            .Where(radio => radio.Content is "浅色" or "深色")
            .ToList();
        var light = radios.Single(radio => (string?)radio.Content == "浅色");
        var dark = radios.Single(radio => (string?)radio.Content == "深色");

        Assert.Equal(2, radios.Count);
        Assert.True(dark.IsChecked, "dark is the persisted default");
        Assert.False(light.IsChecked);

        Click(light, window);
        Assert.Equal(ThemeVariant.Light, _application.RequestedThemeVariant);
        Assert.True(light.IsChecked);
        Assert.False(dark.IsChecked);

        Click(dark, window);
        Assert.Equal(ThemeVariant.Dark, _application.RequestedThemeVariant);
        Assert.True(dark.IsChecked);
        Assert.False(light.IsChecked);
    }

    [AvaloniaFact]
    public void The_Title_Bar_Toggle_Updates_The_Settings_Selection()
    {
        var (window, themes) = BuildWindow();
        themes.Apply(ThemeVariant.Dark);

        Click(window.FindControl<ToggleButton>("SettingsButton")!, window);

        var light = window.GetVisualDescendants().OfType<SettingsPanel>().Single()
            .GetVisualDescendants().OfType<RadioButton>().Single(radio => (string?)radio.Content == "浅色");
        Assert.False(light.IsChecked);

        Click(ToggleOf(window), window);

        Assert.Equal(ThemeVariant.Light, _application.RequestedThemeVariant);
        Assert.True(light.IsChecked, "the settings radio must follow the title-bar toggle");
    }

    [AvaloniaFact]
    public void The_Toggle_Glyph_Follows_The_Current_Theme()
    {
        var (window, _) = BuildWindow();
        var toggle = ToggleOf(window);
        var moon = toggle.GetVisualDescendants().OfType<ShapePath>().Single(path => path.Name == "MoonGlyph");
        var sun = toggle.GetVisualDescendants().OfType<ShapePath>().Single(path => path.Name == "SunGlyph");

        Assert.True(moon.IsVisible, "dark mode shows the moon");
        Assert.False(sun.IsVisible);

        Click(toggle, window);

        Assert.False(moon.IsVisible);
        Assert.True(sun.IsVisible, "light mode shows the sun");
    }

    [AvaloniaFact]
    public void The_Toggle_Glyph_Is_Painted_From_The_Theme_Token()
    {
        var (window, _) = BuildWindow();
        var moon = ToggleOf(window).GetVisualDescendants().OfType<ShapePath>().Single(path => path.Name == "MoonGlyph");

        // A raw hex would stay put when the variant flips; a token re-resolves.
        UnderTheme(ThemeVariant.Dark, () =>
            Assert.Equal(Color.FromRgb(0xD0, 0xD0, 0xD0), ColorOf(moon.Fill)));
        UnderTheme(ThemeVariant.Light, () =>
            Assert.Equal(Color.FromRgb(0x57, 0x60, 0x6A), ColorOf(moon.Fill)));
    }

    private static void UnderTheme(ThemeVariant variant, Action body)
    {
        var application = Application.Current!;
        var original = application.RequestedThemeVariant;
        application.RequestedThemeVariant = variant;

        try
        {
            body();
        }
        finally
        {
            application.RequestedThemeVariant = original;
        }
    }

    private static Color ColorOf(IBrush? brush)
        => Assert.IsType<ISolidColorBrush>(brush, exactMatch: false).Color;
}
