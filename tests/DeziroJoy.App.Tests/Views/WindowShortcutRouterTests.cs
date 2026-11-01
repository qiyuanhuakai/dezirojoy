using Avalonia.Input;
using DeziroJoy.App.Views;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

public sealed class WindowShortcutRouterTests
{
    [Theory]
    [InlineData(Key.D1, WindowShortcutAction.SelectMappingTab)]
    [InlineData(Key.D2, WindowShortcutAction.SelectMouseTab)]
    [InlineData(Key.D3, WindowShortcutAction.SelectScrollTab)]
    public void Ctrl_Number_Selects_The_Matching_Tab(Key key, WindowShortcutAction expected)
    {
        var action = WindowShortcutRouter.Route(key, KeyModifiers.Control, false, false);

        Assert.Equal(expected, action);
    }

    [Fact]
    public void Ctrl_Comma_Toggles_The_Settings_Surface()
    {
        Assert.Equal(
            WindowShortcutAction.OpenSettings,
            WindowShortcutRouter.Route(Key.OemComma, KeyModifiers.Control, false, false));
        Assert.Equal(
            WindowShortcutAction.CloseSettings,
            WindowShortcutRouter.Route(Key.OemComma, KeyModifiers.Control, true, false));
    }

    [Fact]
    public void Settings_Is_The_Topmost_Keyboard_Layer()
    {
        Assert.Equal(
            WindowShortcutAction.CloseSettings,
            WindowShortcutRouter.Route(Key.Escape, KeyModifiers.None, true, false));
        Assert.Equal(
            WindowShortcutAction.None,
            WindowShortcutRouter.Route(Key.D1, KeyModifiers.Control, true, false));
    }

    [Fact]
    public void Capture_Suppresses_Global_Shortcuts()
    {
        Assert.Equal(
            WindowShortcutAction.None,
            WindowShortcutRouter.Route(Key.D1, KeyModifiers.Control, false, true));
        Assert.Equal(
            WindowShortcutAction.None,
            WindowShortcutRouter.Route(Key.OemComma, KeyModifiers.Control, false, true));
    }

    [Fact]
    public void Unmodified_Number_Keys_Are_Not_Global_Shortcuts()
    {
        Assert.Equal(
            WindowShortcutAction.None,
            WindowShortcutRouter.Route(Key.D1, KeyModifiers.None, false, false));
    }
}
