using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The extracted sensitivity panels expose the engine's already-working semantics -
/// precision mode, per-axis gain and scroll curve strength - through named controls.
/// These tests drive those controls the way the window does and assert the machine
/// values that reach the Core settings and the active profile, so a renamed control or
/// a broken binding fails here instead of silently dropping the user's setting.
/// </summary>
public sealed class SensitivityPanelGuiTests
{
    private const string DeviceId = "pad-01";

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

    private static void ConnectPad(FakeGamepadProvider provider)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
    }

    // The panels are UserControls with their own name scopes, so their controls are
    // reached through the panel instance in the logical tree, which holds every tab's
    // content whether or not that tab is the selected one.
    private static MouseSensitivityPanel MouseSensitivityPanelOf(Window window)
        => window.GetLogicalDescendants().OfType<MouseSensitivityPanel>().First();

    private static ScrollSensitivityPanel ScrollSensitivityPanelOf(Window window)
        => window.GetLogicalDescendants().OfType<ScrollSensitivityPanel>().First();

    [AvaloniaFact]
    public void PrecisionModeCombo_Round_Trips_Hold_And_Toggle()
    {
        var (window, viewModel, _, _) = BuildWindow();
        var combo = MouseSensitivityPanelOf(window).FindControl<ComboBox>("PrecisionModeCombo")!;

        var hold = viewModel.MouseSensitivity.PrecisionModeOptions.Single(option => option.Value == PrecisionMode.Hold);
        var toggle = viewModel.MouseSensitivity.PrecisionModeOptions.Single(option => option.Value == PrecisionMode.Toggle);

        Assert.Same(hold, combo.SelectedItem);
        Assert.Equal(PrecisionMode.Hold, viewModel.MouseSensitivity.PrecisionMode);

        combo.SelectedItem = toggle;
        Assert.Equal(PrecisionMode.Toggle, viewModel.MouseSensitivity.PrecisionMode);
        Assert.Equal(PrecisionMode.Toggle, viewModel.MouseSensitivity.BuildSettings().PrecisionMode);

        combo.SelectedItem = hold;
        Assert.Equal(PrecisionMode.Hold, viewModel.MouseSensitivity.PrecisionMode);
        Assert.Equal(PrecisionMode.Hold, viewModel.MouseSensitivity.BuildSettings().PrecisionMode);

        // A load moves the selection with the setting, so the combo shows what the
        // mapping actually carries.
        viewModel.MouseSensitivity.LoadFrom(new MouseStickSettings { PrecisionMode = PrecisionMode.Toggle });
        Assert.Same(toggle, combo.SelectedItem);
    }

    [AvaloniaFact]
    public void ScaleSliders_Survive_BuildSettings_And_LoadFrom()
    {
        var (window, viewModel, _, _) = BuildWindow();
        var panel = MouseSensitivityPanelOf(window);
        var scaleX = panel.FindControl<Slider>("ScaleXSlider")!;
        var scaleY = panel.FindControl<Slider>("ScaleYSlider")!;

        Assert.Equal(0.1d, scaleX.Minimum);
        Assert.Equal(4.0d, scaleX.Maximum);
        Assert.Equal(0.1d, scaleY.Minimum);
        Assert.Equal(4.0d, scaleY.Maximum);

        scaleX.Value = 1.25;
        scaleY.Value = 0.75;

        Assert.Equal(1.25f, viewModel.MouseSensitivity.ScaleX);
        Assert.Equal(0.75f, viewModel.MouseSensitivity.ScaleY);

        var settings = viewModel.MouseSensitivity.BuildSettings();
        Assert.Equal(1.25f, settings.ScaleX);
        Assert.Equal(0.75f, settings.ScaleY);

        var loaded = new MouseSensitivityViewModel();
        loaded.LoadFrom(settings);
        Assert.Equal(1.25f, loaded.ScaleX);
        Assert.Equal(0.75f, loaded.ScaleY);
    }

    [AvaloniaFact]
    public void Changing_Scale_Does_Not_Reset_PrecisionMode_Or_Other_Settings()
    {
        var (window, viewModel, _, _) = BuildWindow();
        var panel = MouseSensitivityPanelOf(window);

        viewModel.MouseSensitivity.PrecisionMode = PrecisionMode.Toggle;
        viewModel.MouseSensitivity.MaxSpeed = 2400f;
        viewModel.MouseSensitivity.DeadZone = 0.3f;

        panel.FindControl<Slider>("ScaleXSlider")!.Value = 2.0;
        panel.FindControl<Slider>("ScaleYSlider")!.Value = 0.25;

        Assert.Equal(PrecisionMode.Toggle, viewModel.MouseSensitivity.PrecisionMode);
        Assert.Equal(2400f, viewModel.MouseSensitivity.MaxSpeed);
        Assert.Equal(0.3f, viewModel.MouseSensitivity.DeadZone);
    }

    [AvaloniaFact]
    public void ScrollCurveStrengthSlider_Updates_And_Round_Trips()
    {
        var (window, viewModel, _, _) = BuildWindow();
        var slider = ScrollSensitivityPanelOf(window).FindControl<Slider>("ScrollCurveStrengthSlider")!;

        Assert.Equal(0d, slider.Minimum);
        Assert.Equal(2d, slider.Maximum);
        Assert.Equal(1d, slider.Value);

        slider.Value = 1.75;

        Assert.Equal(1.75f, viewModel.ScrollSensitivity.CurveStrength);
        Assert.Equal(1.75f, viewModel.ScrollSensitivity.BuildSettings().CurveStrength);

        var loaded = new ScrollSensitivityViewModel();
        loaded.LoadFrom(viewModel.ScrollSensitivity.BuildSettings());
        Assert.Equal(1.75f, loaded.CurveStrength);
    }

    [AvaloniaFact]
    public void Apply_Preserves_The_New_Sensitivity_Values()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var mousePanel = MouseSensitivityPanelOf(window);
        mousePanel.FindControl<Slider>("ScaleXSlider")!.Value = 1.25;
        mousePanel.FindControl<Slider>("ScaleYSlider")!.Value = 0.75;
        mousePanel.FindControl<ComboBox>("PrecisionModeCombo")!.SelectedItem =
            viewModel.MouseSensitivity.PrecisionModeOptions.Single(option => option.Value == PrecisionMode.Toggle);

        viewModel.ApplyMouseSensitivityCommand.Execute(null);

        var mouse = Assert.IsType<MouseMoveTarget>(
            viewModel.ActiveProfile!.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Right }).Target);
        Assert.Equal(1.25f, mouse.Settings.ScaleX);
        Assert.Equal(0.75f, mouse.Settings.ScaleY);
        Assert.Equal(PrecisionMode.Toggle, mouse.Settings.PrecisionMode);

        // The window reloads the panels from the applied profile, so the applied values are
        // what the panel shows afterwards.
        Assert.Equal(1.25f, viewModel.MouseSensitivity.ScaleX);
        Assert.Equal(0.75f, viewModel.MouseSensitivity.ScaleY);
        Assert.Equal(PrecisionMode.Toggle, viewModel.MouseSensitivity.PrecisionMode);

        // Each apply reloads both panels from the profile, so the scroll edit is made after
        // the mouse apply - the order a user works in, one panel at a time.
        ScrollSensitivityPanelOf(window).FindControl<Slider>("ScrollCurveStrengthSlider")!.Value = 1.75;
        viewModel.ApplyScrollSensitivityCommand.Execute(null);

        var wheel = Assert.IsType<WheelScrollTarget>(
            viewModel.ActiveProfile!.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Left }).Target);
        Assert.Equal(1.75f, wheel.Settings.CurveStrength);
        Assert.Equal(1.75f, viewModel.ScrollSensitivity.CurveStrength);

        // The mouse values survive the scroll apply: the reload reads them back from the profile.
        Assert.Equal(1.25f, viewModel.MouseSensitivity.ScaleX);
        Assert.Equal(PrecisionMode.Toggle, viewModel.MouseSensitivity.PrecisionMode);
    }
}
