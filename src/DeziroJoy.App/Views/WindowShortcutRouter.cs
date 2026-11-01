using Avalonia.Input;

namespace DeziroJoy.App.Views;

public enum WindowShortcutAction
{
    None,
    OpenSettings,
    CloseSettings,
    SelectMappingTab,
    SelectMouseTab,
    SelectScrollTab,
}

public static class WindowShortcutRouter
{
    public static WindowShortcutAction Route(
        Key key,
        KeyModifiers modifiers,
        bool settingsOpen,
        bool captureActive)
    {
        if (settingsOpen)
        {
            if (key == Key.Escape || (key == Key.OemComma && modifiers == KeyModifiers.Control))
            {
                return WindowShortcutAction.CloseSettings;
            }

            return WindowShortcutAction.None;
        }

        if (captureActive || modifiers != KeyModifiers.Control)
        {
            return WindowShortcutAction.None;
        }

        return key switch
        {
            Key.OemComma => WindowShortcutAction.OpenSettings,
            Key.D1 => WindowShortcutAction.SelectMappingTab,
            Key.D2 => WindowShortcutAction.SelectMouseTab,
            Key.D3 => WindowShortcutAction.SelectScrollTab,
            _ => WindowShortcutAction.None,
        };
    }
}
