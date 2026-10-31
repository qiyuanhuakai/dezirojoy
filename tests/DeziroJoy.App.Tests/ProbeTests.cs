using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using Xunit;

namespace DeziroJoy.App.Tests;

public class ProbeTests
{
    [AvaloniaFact]
    public void Slider_TwoWay_To_Float_Property()
    {
        var vm = new MouseSensitivityViewModel();
        var window = new ProbeWindow(vm);
        window.Show();

        var slider = window.FindControl<Slider>("ProbeSlider")!;
        Assert.Equal(1600d, slider.Value);

        slider.Value = 3000;
        Assert.Equal(3000f, vm.MaxSpeed);

        vm.MaxSpeed = 2500f;
        Assert.Equal(2500d, slider.Value);
    }
}
