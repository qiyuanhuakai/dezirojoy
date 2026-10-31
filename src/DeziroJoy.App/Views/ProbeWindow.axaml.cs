using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views;

public partial class ProbeWindow : Window
{
    public ProbeWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public ProbeWindow(MainWindowViewModel vm)
        : this()
    {
        DataContext = vm;
    }
}
