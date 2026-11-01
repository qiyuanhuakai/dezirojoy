using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DeziroJoy.App.Views.Controls;

/// <summary>
/// Sidebar card for the resident-daemon and logon-autostart settings. It is a pure view over
/// <see cref="ViewModels.DaemonSettingsViewModel"/>, which owns all state and commands.
/// </summary>
public partial class DaemonSettingsCard : UserControl
{
    public DaemonSettingsCard()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
