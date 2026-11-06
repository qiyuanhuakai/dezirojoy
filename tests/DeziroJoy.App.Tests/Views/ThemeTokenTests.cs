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
        "StickAccentRingBrush",
        "StickThumbBrush", "DpadCenterBrush", "PadIdleBrush", "PadPressedBrush",
        "PadWellBrush", "PadCapBrush", "PadBodyStrokeBrush",
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
            ["PadLabelBrush"] = Color.FromRgb(0x9A, 0x9A, 0x9A),
            ["AccentForegroundBrush"] = Color.FromRgb(0x10, 0x10, 0x10),
            ["DangerForegroundBrush"] = Color.FromRgb(0xD0, 0xD0, 0xD0),
            ["AccentBrush"] = Color.FromRgb(0x4C, 0xC2, 0xFF),
            ["AccentHoverBrush"] = Color.FromRgb(0x6F, 0xCF, 0xFF),
            ["AccentPressedBrush"] = Color.FromRgb(0x6F, 0xCF, 0xFF),
            ["ConnectedBrush"] = Color.FromRgb(0x3F, 0xB9, 0x50),
            ["IdleDotBrush"] = Color.FromRgb(0x6E, 0x76, 0x81),
            ["DangerBrush"] = Color.FromRgb(0xC4, 0x2B, 0x1C),
            ["PadBodyBrush"] = Color.FromRgb(0xEC, 0xEA, 0xE3),
            ["StickPadBrush"] = Color.FromRgb(0x16, 0x16, 0x16),
            ["StickPadStrokeBrush"] = Color.FromRgb(0x6E, 0x6E, 0x6E),
            ["StickRingBrush"] = Color.FromRgb(0x6E, 0x6E, 0x6E),
            ["StickAccentRingBrush"] = Color.FromRgb(0xFF, 0x8A, 0x1E),
            ["StickThumbBrush"] = Color.FromRgb(0x3A, 0x3A, 0x3A),
            ["DpadCenterBrush"] = Color.FromRgb(0x16, 0x16, 0x16),
            ["PadIdleBrush"] = Color.FromRgb(0x16, 0x16, 0x16),
            ["PadPressedBrush"] = Color.FromRgb(0x4C, 0xC2, 0xFF),
            ["PadWellBrush"] = Color.FromRgb(0xDC, 0xD8, 0xCE),
            ["PadCapBrush"] = Color.FromRgb(0x1C, 0x1C, 0x1C),
            ["PadBodyStrokeBrush"] = Color.FromRgb(0xC9, 0xC6, 0xBD),
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
            ["PadLabelBrush"] = Color.FromRgb(0x9A, 0x9A, 0x9A),
            ["AccentForegroundBrush"] = Color.FromRgb(0xFF, 0xFF, 0xFF),
            ["DangerForegroundBrush"] = Color.FromRgb(0xFF, 0xFF, 0xFF),
            ["AccentBrush"] = Color.FromRgb(0x0A, 0x6F, 0xBF),
            ["AccentHoverBrush"] = Color.FromRgb(0x08, 0x59, 0x9B),
            ["AccentPressedBrush"] = Color.FromRgb(0x06, 0x4E, 0x86),
            ["ConnectedBrush"] = Color.FromRgb(0x1A, 0x7F, 0x37),
            ["IdleDotBrush"] = Color.FromRgb(0x57, 0x60, 0x6A),
            ["DangerBrush"] = Color.FromRgb(0xC4, 0x2B, 0x1C),
            ["PadBodyBrush"] = Color.FromRgb(0xF4, 0xF2, 0xEC),
            ["StickPadBrush"] = Color.FromRgb(0x16, 0x16, 0x16),
            ["StickPadStrokeBrush"] = Color.FromRgb(0x6E, 0x6E, 0x6E),
            ["StickRingBrush"] = Color.FromRgb(0x6E, 0x6E, 0x6E),
            ["StickAccentRingBrush"] = Color.FromRgb(0xFF, 0x8A, 0x1E),
            ["StickThumbBrush"] = Color.FromRgb(0x3A, 0x3A, 0x3A),
            ["DpadCenterBrush"] = Color.FromRgb(0x16, 0x16, 0x16),
            ["PadIdleBrush"] = Color.FromRgb(0x16, 0x16, 0x16),
            ["PadPressedBrush"] = Color.FromRgb(0x0A, 0x6F, 0xBF),
            ["PadWellBrush"] = Color.FromRgb(0xDC, 0xD8, 0xCE),
            ["PadCapBrush"] = Color.FromRgb(0x1C, 0x1C, 0x1C),
            ["PadBodyStrokeBrush"] = Color.FromRgb(0xC9, 0xC6, 0xBD),
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

    /// <summary>
    /// The redrawn model is a WHITE controller, so the dark theme is no longer a grey
    /// ladder: the body is an off-white shell and every piece of hardware on it is
    /// near-black. This pins that shell/hardware contrast instead of the old dim-to-bright
    /// ladder the near-black slab used to need.
    /// </summary>
    [AvaloniaFact]
    public void Dark_Pad_Is_An_Off_White_Shell_With_Black_Hardware()
    {
        // The shell is off-white: bright in the dark theme.
        var body = Luminance(ColorOf(Resolve("PadBodyBrush", ThemeVariant.Dark)));
        Assert.True(body > 0.6, $"the off-white shell must read as bright, luminance was {body}");

        // The shell clears the near-black card it sits on by a wide margin.
        var card = Luminance(ColorOf(Resolve("CardBrush", ThemeVariant.Dark)));
        Assert.True(body - card > 0.3,
            $"the off-white shell must stand off the dark card, delta was {body - card}");

        // Every piece of hardware sits far below the shell: black controls on an off-white body.
        var hardware = new[]
        {
            "PadIdleBrush", "StickPadBrush", "DpadCenterBrush", "PadCapBrush", "StickThumbBrush",
        };
        foreach (var key in hardware)
        {
            var luminance = Luminance(ColorOf(Resolve(key, ThemeVariant.Dark)));
            Assert.True(body - luminance > 0.5,
                $"{key} must contrast with the off-white shell, delta was {body - luminance}");
        }
    }

    /// <summary>
    /// DESIGN.md 4.6: the light theme stays soft rather than stark white. The pad body is an
    /// off-white shell clearly below the white card, so the silhouette reads as a surface
    /// instead of a hole punched in the panel, and the wells sit just below it as a recess.
    /// </summary>
    [AvaloniaFact]
    public void Light_Pad_Tokens_Stay_Soft_Rather_Than_Stark_White()
    {
        var body = Luminance(ColorOf(Resolve("PadBodyBrush", ThemeVariant.Light)));
        var card = Luminance(ColorOf(Resolve("CardBrush", ThemeVariant.Light)));

        Assert.True(card - body > 0.05,
            $"the light pad body must stay clearly softer than the white card, delta was {card - body}");

        // The wells sit below the body, so the light theme keeps a soft recessed step.
        var well = Luminance(ColorOf(Resolve("PadWellBrush", ThemeVariant.Light)));
        Assert.True(well < body, "the light wells must sit below the body");
    }

    /// <summary>
    /// No Xbox brand green anywhere in the pad tokens: the accent stays the app's cyan and
    /// every pad surface is a neutral grey step.
    /// </summary>
    [AvaloniaFact]
    public void No_Pad_Token_Carries_Brand_Green()
    {
        foreach (var key in TokenKeys.Where(key => key.StartsWith("Pad", StringComparison.Ordinal)
            || key.StartsWith("Stick", StringComparison.Ordinal)
            || key == "DpadCenterBrush"))
        {
            var color = ColorOf(Resolve(key, ThemeVariant.Dark));
            Assert.False(color.G > color.R + 8 && color.G > color.B + 8,
                $"{key} reads as brand green (#{color})");

            var light = ColorOf(Resolve(key, ThemeVariant.Light));
            Assert.False(light.G > light.R + 8 && light.G > light.B + 8,
                $"{key} reads as brand green in the light theme (#{light})");
        }
    }

    private static double Luminance(Color color)
    {
        double Channel(byte value)
        {
            var srgb = value / 255.0;
            return srgb <= 0.03928 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
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
