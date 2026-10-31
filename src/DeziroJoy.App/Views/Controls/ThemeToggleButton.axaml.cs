using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DeziroJoy.App.Services;

namespace DeziroJoy.App.Views.Controls;

/// <summary>
/// Title-bar button that flips the light/dark preference at runtime. It is the UI
/// entry point for <see cref="ThemeService"/>, which already persisted the choice
/// but had nothing to change it with.
/// </summary>
public partial class ThemeToggleButton : Button
{
    private readonly ThemeService _themes;

    public ThemeToggleButton()
        : this(new ThemeService())
    {
    }

    public ThemeToggleButton(ThemeService themes)
    {
        ArgumentNullException.ThrowIfNull(themes);

        _themes = themes;

        AvaloniaXamlLoader.Load(this);

        Click += OnClick;
        UpdateGlyph();
    }

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        _themes.Toggle();
        UpdateGlyph();
    }

    /// <summary>
    /// Shows the moon while the dark theme is live and the sun once it is light, so
    /// the glyph names the theme the user is about to switch to.
    /// </summary>
    private void UpdateGlyph()
    {
        if (this.FindControl<Avalonia.Controls.Shapes.Path>("MoonGlyph") is { } moon)
        {
            moon.IsVisible = !_themes.IsLight;
        }

        if (this.FindControl<Avalonia.Controls.Shapes.Path>("SunGlyph") is { } sun)
        {
            sun.IsVisible = _themes.IsLight;
        }
    }
}
