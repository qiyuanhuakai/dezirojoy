using Avalonia.Headless.XUnit;
using DeziroJoy.App;
using Xunit;

namespace DeziroJoy.App.Tests;

public class MainWindowSmokeTests
{
    [AvaloniaFact]
    public void MainWindow_Opens_With_Product_Title()
    {
        var window = new MainWindow();
        window.Show();

        Assert.Equal("DeziroJoy", window.Title);
    }
}
