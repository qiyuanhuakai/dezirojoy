using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// Proves the token layer in Themes/Palette.axaml resolves per theme variant and
/// that the runtime switch reaches a live control.
/// </summary>
public sealed class ThemeTokenTests
{
    private static readonly string[] TokenKeys =
    [
        "AppBackgroundBrush", "TitleBarBrush", "PanelBrush", "CardBrush", "SunkenBrush",
        "SubtleBrush", "ControlHoverBrush", "CaptionHoverBrush", "AccentSurfaceBrush",
        "OverlayBackgroundBrush", "CardBorderBrush", "ControlBorderBrush",
        "PreviewBorderBrush", "TrackBrush", "TextPrimaryBrush", "TextSecondaryBrush",
        "HintTextBrush", "MutedTextBrush", "ControlForegroundBrush", "ToggleForegroundBrush",
        "CaptionGlyphBrush", "BadgeTextBrush", "OverlayTextBrush", "PadLabelBrush",
        "AccentForegroundBrush", "DangerForegroundBrush", "AccentBrush", "AccentHoverBrush",
        "AccentPressedBrush", "ConnectedBrush", "IdleDotBrush", "DangerBrush",
        "PadBodyBrush", "StickPadBrush", "StickPadStrokeBrush", "StickRingBrush",
        "StickThumbBrush", "DpadCenterBrush", "PadIdleBrush", "PadPressedBrush",
    ];

    private static Color ColorOf(object? resource)
        => Assert.IsType<SolidColorBrush>(resource, exactMatch: false).Color;

    private static object? Resolve(string key, ThemeVariant variant)
    {
        Assert.True(
            Application.Current!.TryFindResource(key, variant, out var value),
            $"Token '{key}' did not resolve under {variant}.");

        return value;
    }

    [AvaloniaFact]
    public void Every_Token_Resolves_As_A_Solid_Brush_Under_Both_Variants()
    {
        foreach (var key in TokenKeys)
        {
            Assert.IsType<SolidColorBrush>(Resolve(key, ThemeVariant.Dark), exactMatch: false);
            Assert.IsType<SolidColorBrush>(Resolve(key, ThemeVariant.Light), exactMatch: false);
        }
    }

    [AvaloniaFact]
    public void Dark_Tokens_Reproduce_The_Original_Hardcoded_Hexes()
    {
        var expected = new Dictionary<string, Color>
        {
            ["AppBackgroundBrush"] = Color.FromRgb(0x10, 0x10, 0x10),
            ["TitleBarBrush"] = Color.FromRgb(0x16, 0x16, 0x16),
            ["PanelBrush"] = Color.FromRgb(0x14, 0x14, 0x14),
            ["CardBrush"] = Color.FromRgb(0x1B, 0x1B, 0x1B),
            ["SunkenBrush"] = Color.FromRgb(0x15, 0x15, 0x15),
            ["SubtleBrush"] = Color.FromRgb(0x23, 0x23, 0x23),
            ["ControlHoverBrush"] = Color.FromRgb(0x2C, 0x2C, 0x2C),
            ["CaptionHoverBrush"] = Color.FromRgb(0x2A, 0x2A, 0x2A),
            ["AccentSurfaceBrush"] = Color.FromRgb(0x1E, 0x3A, 0x48),
            ["OverlayBackgroundBrush"] = Color.FromArgb(0xDD, 0x15, 0x15, 0x15),
            ["CardBorderBrush"] = Color.FromRgb(0x2B, 0x2B, 0x2B),
            ["ControlBorderBrush"] = Color.FromRgb(0x33, 0x33, 0x33),
            ["PreviewBorderBrush"] = Color.FromRgb(0x2C, 0x2C, 0x2C),
            ["TrackBrush"] = Color.FromRgb(0x24, 0x24, 0x24),
            ["TextPrimaryBrush"] = Color.FromRgb(0xF0, 0xF0, 0xF0),
            ["TextSecondaryBrush"] = Color.FromRgb(0x9A, 0x9A, 0x9A),
            ["HintTextBrush"] = Color.FromRgb(0x7A, 0x7A, 0x7A),
            ["MutedTextBrush"] = Color.FromRgb(0x8A, 0x8A, 0x8A),
            ["ControlForegroundBrush"] = Color.FromRgb(0xDA, 0xDA, 0xDA),
            ["ToggleForegroundBrush"] = Color.FromRgb(0xB4, 0xB4, 0xB4),
            ["CaptionGlyphBrush"] = Color.FromRgb(0xD0, 0xD0, 0xD0),
            ["BadgeTextBrush"] = Color.FromRgb(0xB9, 0xB9, 0xB9),
            ["OverlayTextBrush"] = Color.FromRgb(0xD6, 0xD6, 0xD6),
            ["PadLabelBrush"] = Color.FromRgb(0xCF, 0xCF, 0xCF),
            ["AccentForegroundBrush"] = Color.FromRgb(0x10, 0x10, 0x10),
            ["DangerForegroundBrush"] = Color.FromRgb(0xD0, 0xD0, 0xD0),
            ["AccentBrush"] = Color.FromRgb(0x4C, 0xC2, 0xFF),
            ["AccentHoverBrush"] = Color.FromRgb(0x6F, 0xCF, 0xFF),
            ["AccentPressedBrush"] = Color.FromRgb(0x6F, 0xCF, 0xFF),
            ["ConnectedBrush"] = Color.FromRgb(0x3F, 0xB9, 0x50),
            ["IdleDotBrush"] = Color.FromRgb(0x6E, 0x76, 0x81),
            ["DangerBrush"] = Color.FromRgb(0xC4, 0x2B, 0x1C),
            ["PadBodyBrush"] = Color.FromRgb(0x1F, 0x1F, 0x1F),
            ["StickPadBrush"] = Color.FromRgb(0x26, 0x26, 0x26),
            ["StickPadStrokeBrush"] = Color.FromRgb(0x3A, 0x3A, 0x3A),
            ["StickRingBrush"] = Color.FromRgb(0x4A, 0x4A, 0x4A),
            ["StickThumbBrush"] = Color.FromRgb(0xD8, 0xD8, 0xD8),
            ["DpadCenterBrush"] = Color.FromRgb(0x2E, 0x2E, 0x2E),
            ["PadIdleBrush"] = Color.FromRgb(0x3A, 0x3A, 0x3A),
            ["PadPressedBrush"] = Color.FromRgb(0x4C, 0xC2, 0xFF),
        };

        Assert.Equal(TokenKeys.Length, expected.Count);

        foreach (var (key, color) in expected)
        {
            Assert.Equal(color, ColorOf(Resolve(key, ThemeVariant.Dark)));
        }
    }

    [AvaloniaFact]
    public void Light_Tokens_Are_Distinct_From_Dark_And_Readable()
    {
        var expected = new Dictionary<string, Color>
        {
            ["AppBackgroundBrush"] = Color.FromRgb(0xF6, 0xF8, 0xFA),
            ["TitleBarBrush"] = Color.FromRgb(0xFF, 0xFF, 0xFF),
            ["PanelBrush"] = Color.FromRgb(0xFF, 0xFF, 0xFF),
            ["CardBrush"] = Color.FromRgb(0xFF, 0xFF, 0xFF),
            ["SunkenBrush"] = Color.FromRgb(0xF6, 0xF8, 0xFA),
            ["SubtleBrush"] = Color.FromRgb(0xF0, 0xF2, 0xF5),
            ["ControlHoverBrush"] = Color.FromRgb(0xE9, 0xED, 0xF1),
            ["CaptionHoverBrush"] = Color.FromRgb(0xE9, 0xED, 0xF1),
            ["AccentSurfaceBrush"] = Color.FromRgb(0xE6, 0xF1, 0xF9),
            ["OverlayBackgroundBrush"] = Color.FromArgb(0x33, 0x24, 0x29, 0x2E),
            ["CardBorderBrush"] = Color.FromRgb(0xD0, 0xD7, 0xDE),
            ["ControlBorderBrush"] = Color.FromRgb(0xAF, 0xB8, 0xC1),
            ["PreviewBorderBrush"] = Color.FromRgb(0xC9, 0xD1, 0xD9),
            ["TrackBrush"] = Color.FromRgb(0xC9, 0xD1, 0xD9),
            ["TextPrimaryBrush"] = Color.FromRgb(0x1F, 0x23, 0x28),
            ["TextSecondaryBrush"] = Color.FromRgb(0x59, 0x63, 0x6E),
            ["HintTextBrush"] = Color.FromRgb(0x65, 0x6E, 0x78),
            ["MutedTextBrush"] = Color.FromRgb(0x5C, 0x64, 0x70),
            ["ControlForegroundBrush"] = Color.FromRgb(0x24, 0x29, 0x2F),
            ["ToggleForegroundBrush"] = Color.FromRgb(0x24, 0x29, 0x2F),
            ["CaptionGlyphBrush"] = Color.FromRgb(0x57, 0x60, 0x6A),
            ["BadgeTextBrush"] = Color.FromRgb(0x57, 0x60, 0x6A),
            ["OverlayTextBrush"] = Color.FromRgb(0x1F, 0x23, 0x28),
            ["PadLabelBrush"] = Color.FromRgb(0x57, 0x60, 0x6A),
            ["AccentForegroundBrush"] = Color.FromRgb(0xFF, 0xFF, 0xFF),
            ["DangerForegroundBrush"] = Color.FromRgb(0xFF, 0xFF, 0xFF),
            ["AccentBrush"] = Color.FromRgb(0x0A, 0x6F, 0xBF),
            ["AccentHoverBrush"] = Color.FromRgb(0x08, 0x59, 0x9B),
            ["AccentPressedBrush"] = Color.FromRgb(0x06, 0x4E, 0x86),
            ["ConnectedBrush"] = Color.FromRgb(0x1A, 0x7F, 0x37),
            ["IdleDotBrush"] = Color.FromRgb(0x57, 0x60, 0x6A),
            ["DangerBrush"] = Color.FromRgb(0xC4, 0x2B, 0x1C),
            ["PadBodyBrush"] = Color.FromRgb(0xE9, 0xED, 0xF1),
            ["StickPadBrush"] = Color.FromRgb(0xDC, 0xE1, 0xE6),
            ["StickPadStrokeBrush"] = Color.FromRgb(0xAF, 0xB8, 0xC1),
            ["StickRingBrush"] = Color.FromRgb(0xAE, 0xB6, 0xBE),
            ["StickThumbBrush"] = Color.FromRgb(0x57, 0x60, 0x6A),
            ["DpadCenterBrush"] = Color.FromRgb(0xD2, 0xD8, 0xDE),
            ["PadIdleBrush"] = Color.FromRgb(0xBF, 0xC7, 0xCF),
            ["PadPressedBrush"] = Color.FromRgb(0x0A, 0x6F, 0xBF),
        };

        Assert.Equal(TokenKeys.Length, expected.Count);

        foreach (var (key, color) in expected)
        {
            Assert.Equal(color, ColorOf(Resolve(key, ThemeVariant.Light)));
        }
    }

    [AvaloniaFact]
    public void The_Same_Key_Resolves_To_Different_Brushes_Per_Variant()
    {
        var dark = Resolve("AppBackgroundBrush", ThemeVariant.Dark);
        var light = Resolve("AppBackgroundBrush", ThemeVariant.Light);

        Assert.NotSame(dark, light);
        Assert.NotEqual(ColorOf(dark), ColorOf(light));
    }

    [AvaloniaFact]
    public void A_Live_Control_Follows_The_Runtime_Theme_Switch()
    {
        var application = Application.Current!;
        var original = application.RequestedThemeVariant;

        var border = new Border();
        var window = new Window { Width = 120, Height = 120, Content = border };

        try
        {
            application.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();

            Assert.Equal(ThemeVariant.Dark, border.ActualThemeVariant);
            Assert.Equal(
                Color.FromRgb(0x10, 0x10, 0x10),
                ColorOf(border.TryFindResource("AppBackgroundBrush", border.ActualThemeVariant, out var dark)
                    ? dark
                    : null));

            application.RequestedThemeVariant = ThemeVariant.Light;

            Assert.Equal(ThemeVariant.Light, border.ActualThemeVariant);
            Assert.Equal(
                Color.FromRgb(0xF6, 0xF8, 0xFA),
                ColorOf(border.TryFindResource("AppBackgroundBrush", border.ActualThemeVariant, out var light)
                    ? light
                    : null));
        }
        finally
        {
            application.RequestedThemeVariant = original;
            window.Close();
        }
    }
}
