using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The precision-key selector on the mapping editor: the combo is bound to
/// <see cref="MappingEditorViewModel.ActivePrecisionButton"/>, so loading a mouse-move mapping
/// or moving the gesture switcher must push the loaded key into the box. The write direction
/// (picking a key in the combo) was covered by MappingSurfaceTests; these cover the read path.
/// </summary>
public sealed class PrecisionButtonGuiTests
{
    private static (MainWindow Window, MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider) BuildWindow()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var viewModel = new MainWindowViewModel(host, store);
        var window = new MainWindow(viewModel, host);
        window.Show();
        return (window, viewModel, host, provider);
    }

    private static MappingEditorPanel EditorPanelOf(Window window)
        => window.GetLogicalDescendants().OfType<MappingEditorPanel>().First();

    [AvaloniaFact]
    public void Loading_A_MouseMove_Mapping_Shows_Its_Precision_Key_In_The_Combo()
    {
        var (window, viewModel, _, _) = BuildWindow();

        viewModel.Editor.LoadFrom(new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Left),
            Target = new MouseMoveTarget(
                new MouseStickSettings { PrecisionButton = GamepadButton.RightStick }),
        });
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var combo = EditorPanelOf(window).FindControl<ComboBox>("EditorPrecisionButtonCombo")!;

        Assert.Equal(GamepadButton.RightStick, combo.SelectedItem);
    }
}
