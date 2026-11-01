using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using DeziroJoy.App.Services;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// Sidebar binding for the light/dark preference. It shares the single
/// <see cref="ThemeService"/> the title-bar toggle uses, so the two entry points never
/// disagree: a change through either one is mirrored here through <see cref="ThemeService.Changed"/>.
/// </summary>
public partial class AppearanceViewModel : ObservableObject
{
    private ThemeService? _themes;

    [ObservableProperty]
    private bool _isLight;

    /// <summary>Binds this panel to the running preference. Called once by the view.</summary>
    public void Attach(ThemeService themes)
    {
        ArgumentNullException.ThrowIfNull(themes);

        if (_themes is not null)
        {
            _themes.Changed -= OnThemeChanged;
        }

        _themes = themes;
        _themes.Changed += OnThemeChanged;
        IsLight = themes.IsLight;
    }

    partial void OnIsLightChanged(bool value)
    {
        if (_themes is null)
        {
            return;
        }

        var variant = value ? ThemeVariant.Light : ThemeVariant.Dark;

        if (_themes.Current == variant)
        {
            return;
        }

        _themes.Apply(variant);
        _themes.Save(variant);
    }

    private void OnThemeChanged(ThemeVariant variant)
    {
        var light = variant == ThemeVariant.Light;

        if (IsLight != light)
        {
            IsLight = light;
        }
    }
}
