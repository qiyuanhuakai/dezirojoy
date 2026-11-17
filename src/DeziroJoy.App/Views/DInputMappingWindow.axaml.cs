using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Platform.Sdl3;

namespace DeziroJoy.App.Views;

public partial class DInputMappingWindow : Window
{
    private readonly DInputMappingViewModel _mapping;

    public DInputMappingWindow()
        : this(new DInputMappingViewModel(new Sdl3JoystickInfo
        {
            InstanceId = 0,
            Name = "DInput device",
            GuidString = "00000000000000000000000000000000",
            Vendor = 0,
            Product = 0,
            ProductVersion = 0,
            Serial = string.Empty,
            Path = string.Empty,
            ButtonCount = 0,
            AxisCount = 0,
            HatCount = 0,
            TypeName = "Unknown",
            HasStandardMapping = false,
        }))
    {
    }

    public DInputMappingWindow(Sdl3JoystickInfo device)
        : this(new DInputMappingViewModel(device))
    {
    }

    internal DInputMappingWindow(DInputMappingViewModel mapping)
    {
        AvaloniaXamlLoader.Load(this);
        _mapping = mapping;
        DataContext = mapping;
        mapping.CloseRequested += OnCloseRequested;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = _mapping.RunAsync();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_mapping.IsCapturing)
        {
            _mapping.CancelCommand.Execute(null);
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _mapping.CloseRequested -= OnCloseRequested;
        _mapping.Dispose();
        base.OnClosed(e);
    }

    private void OnCloseRequested(bool saved) => Close(saved);
}
