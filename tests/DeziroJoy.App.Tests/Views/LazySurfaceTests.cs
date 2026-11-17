using System.Diagnostics;
using System.Reflection;
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
using DeziroJoy.App.Views.Controls;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The window's heavy surfaces are lazily hosted: the settings overlay, the mapping editor
/// and the two sensitivity panels are built the first time the user opens them and released
/// again when they close, instead of being constructed once at window startup and merely
/// hidden. These tests pin that lifecycle - what exists when, and that repeated open/close
/// cycles leave no subscription behind.
/// </summary>
public sealed class LazySurfaceTests
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

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>Clicks a control through the pointer, the way a user does.</summary>
    private static void Click(Control target, Window window)
    {
        window.UpdateLayout();
        target.BringIntoView();
        window.UpdateLayout();
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        Settle(window);
    }

    private static SettingsPanel? SettingsPanelOf(Window window)
        => window.GetVisualDescendants().OfType<SettingsPanel>().FirstOrDefault();

    private static MappingEditorPanel? EditorPanelOf(Window window)
        => window.GetLogicalDescendants().OfType<MappingEditorPanel>().FirstOrDefault();

    private static MouseSensitivityPanel? MousePanelOf(Window window)
        => window.GetLogicalDescendants().OfType<MouseSensitivityPanel>().FirstOrDefault();

    private static ScrollSensitivityPanel? ScrollPanelOf(Window window)
        => window.GetLogicalDescendants().OfType<ScrollSensitivityPanel>().FirstOrDefault();

    /// <summary>How many subscribers an event holds whose target is of the given type.</summary>
    private static int SubscriberCount(object source, string eventName, Type targetType)
    {
        for (var type = source.GetType(); type is not null; type = type.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var field = type.GetField(eventName, flags);

            if (field is null)
            {
                continue;
            }

            return field.GetValue(source) is Delegate handler
                ? handler.GetInvocationList().Count(subscriber => subscriber.Target is not null && targetType.IsInstanceOfType(subscriber.Target))
                : 0;
        }

        return 0;
    }

    // ---- The settings surface ------------------------------------------------

    [AvaloniaFact]
    public void The_Settings_Surface_Is_Not_Built_Until_It_Is_Opened()
    {
        var (window, viewModel, _, _) = BuildWindow();
        Settle(window);

        Assert.False(viewModel.IsSettingsOpen);
        Assert.True(SettingsPanelOf(window) is null, "the settings panel must not exist before the first open");

        viewModel.OpenSettingsCommand.Execute(null);
        Settle(window);

        Assert.NotNull(SettingsPanelOf(window));
        Assert.Same(viewModel, SettingsPanelOf(window)!.DataContext);
    }

    [AvaloniaFact]
    public void Closing_The_Settings_Surface_Releases_It_And_Reopening_Builds_A_Fresh_One()
    {
        var (window, viewModel, _, _) = BuildWindow();
        Settle(window);

        viewModel.OpenSettingsCommand.Execute(null);
        Settle(window);
        var first = SettingsPanelOf(window);
        Assert.NotNull(first);

        viewModel.CloseSettingsCommand.Execute(null);
        Settle(window);

        Assert.False(viewModel.IsSettingsOpen);
        Assert.True(SettingsPanelOf(window) is null, "closing must release the panel, not hide it");

        viewModel.OpenSettingsCommand.Execute(null);
        Settle(window);

        var second = SettingsPanelOf(window);
        Assert.NotNull(second);
        Assert.True(!ReferenceEquals(first, second), "a reopened surface is a fresh panel over the same state");
        Assert.Same(viewModel, second!.DataContext);
    }

    [AvaloniaFact]
    public void Opening_And_Closing_Settings_Leaves_No_Subscription_Behind()
    {
        var (window, viewModel, _, _) = BuildWindow();
        Settle(window);

        var update = viewModel.Update;
        Assert.Equal(0, SubscriberCount(update, nameof(UpdateSettingsViewModel.OpenReleasePageRequested), typeof(SettingsPanel)));
        Assert.Equal(0, SubscriberCount(update, nameof(UpdateSettingsViewModel.RestartRequested), typeof(SettingsPanel)));

        for (var cycle = 0; cycle < 3; cycle++)
        {
            viewModel.OpenSettingsCommand.Execute(null);
            Settle(window);

            Assert.Equal(1, SubscriberCount(update, nameof(UpdateSettingsViewModel.OpenReleasePageRequested), typeof(SettingsPanel)));
            Assert.Equal(1, SubscriberCount(update, nameof(UpdateSettingsViewModel.RestartRequested), typeof(SettingsPanel)));

            viewModel.CloseSettingsCommand.Execute(null);
            Settle(window);

            // Unloaded is a dispatcher continuation, so the release lands a pass after the
            // close - the same pass a user's next click would take.
            Assert.Equal(0, SubscriberCount(update, nameof(UpdateSettingsViewModel.OpenReleasePageRequested), typeof(SettingsPanel)));
            Assert.Equal(0, SubscriberCount(update, nameof(UpdateSettingsViewModel.RestartRequested), typeof(SettingsPanel)));
        }
    }

    // ---- The mapping editor --------------------------------------------------

    [AvaloniaFact]
    public void The_Mapping_Editor_Is_Not_Built_Until_A_Mapping_Opens_And_Is_Released_On_Cancel()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, DeziroJoy.Core.Input.GamepadState.Create(default, 0f, 0f, 0f, 0f));
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        Assert.False(viewModel.IsEditing);
        Assert.True(EditorPanelOf(window) is null, "the editor must not exist before the first edit");
        Assert.False(window.FindControl<Border>("EditorPanel")!.IsVisible);

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));
        Settle(window);

        var editor = EditorPanelOf(window);
        Assert.NotNull(editor);
        Assert.Same(viewModel.Editor, editor!.DataContext);
        Assert.True(window.FindControl<Border>("EditorPanel")!.IsVisible);

        viewModel.CancelEditorCommand.Execute(null);
        Settle(window);

        Assert.False(viewModel.IsEditing);
        Assert.True(EditorPanelOf(window) is null, "cancelling must release the editor, not hide it");
    }

    [AvaloniaFact]
    public void Editing_Repeatedly_Leaves_No_Editor_Subscription_Behind()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, DeziroJoy.Core.Input.GamepadState.Create(default, 0f, 0f, 0f, 0f));
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        Assert.Equal(0, SubscriberCount(viewModel.Editor, nameof(viewModel.Editor.PropertyChanged), typeof(MappingEditorPanel)));

        for (var cycle = 0; cycle < 3; cycle++)
        {
            viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));
            Settle(window);

            Assert.Equal(1, SubscriberCount(viewModel.Editor, nameof(viewModel.Editor.PropertyChanged), typeof(MappingEditorPanel)));

            viewModel.CancelEditorCommand.Execute(null);
            Settle(window);

            Assert.Equal(0, SubscriberCount(viewModel.Editor, nameof(viewModel.Editor.PropertyChanged), typeof(MappingEditorPanel)));
        }
    }

    [AvaloniaFact]
    public void A_Reopened_Editor_Still_Edits_The_Mapping_The_Model_Points_At()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, DeziroJoy.Core.Input.GamepadState.Create(default, 0f, 0f, 0f, 0f));
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        // The first edit opens and closes through the real controls.
        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));
        Settle(window);
        Assert.NotNull(EditorPanelOf(window));
        viewModel.CancelEditorCommand.Execute(null);
        Settle(window);

        // The second open is a fresh panel over the same view model, so it edits the same
        // element and commits exactly what the first one would have.
        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.LeftBumper));
        Settle(window);

        var editor = EditorPanelOf(window)!;
        Assert.Equal(GamepadButton.LeftBumper, viewModel.Editor.SelectedButton);

        editor.FindControl<ComboBox>("TargetKindCombo")!.SelectedItem = MappingTargetKind.KeyPress;
        viewModel.Editor.KeyPickerExpanded = true;
        Settle(window);

        var key = editor.GetLogicalDescendants().OfType<Button>()
            .First(button => button.DataContext is KeyPickerItem { Key: GamepadKey.Space });
        Click(key, window);

        Assert.Equal(GamepadKey.Space, viewModel.Editor.SelectedKey);

        Click(editor.FindControl<Button>("CommitButton")!, window);

        Assert.False(viewModel.IsEditing);
        Assert.Null(EditorPanelOf(window));
        Assert.Contains(
            viewModel.Mappings,
            row => row.Entry.Source == new ButtonBinding(GamepadButton.LeftBumper)
                && row.Entry.Target is KeyPressTarget { Key: GamepadKey.Space });
    }

    // ---- The sensitivity panels ----------------------------------------------

    [AvaloniaFact]
    public void The_Sensitivity_Panels_Are_Not_Built_Until_Their_Tab_Is_Selected()
    {
        var (window, _, _, _) = BuildWindow();
        Settle(window);

        var tabs = window.FindControl<TabControl>("MainTabs")!;
        Assert.Equal(0, tabs.SelectedIndex);
        Assert.True(MousePanelOf(window) is null, "the mouse panel must not exist before its tab is opened");
        Assert.True(ScrollPanelOf(window) is null, "the scroll panel must not exist before its tab is opened");

        tabs.SelectedIndex = 1;
        Settle(window);

        var mouse = MousePanelOf(window);
        Assert.NotNull(mouse);
        Assert.True(ScrollPanelOf(window) is null, "selecting one tab must not build the other");

        tabs.SelectedIndex = 2;
        Settle(window);

        Assert.NotNull(ScrollPanelOf(window));
        Assert.IsType<ScrollSensitivityViewModel>(ScrollPanelOf(window)!.DataContext);

        // Leaving a tab releases its panel again; coming back builds a fresh one over the
        // same view model.
        var first = ScrollPanelOf(window);
        tabs.SelectedIndex = 0;
        Settle(window);
        Assert.True(ScrollPanelOf(window) is null, "leaving a tab must release its panel");

        tabs.SelectedIndex = 2;
        Settle(window);
        Assert.NotNull(ScrollPanelOf(window));
        Assert.True(!ReferenceEquals(first, ScrollPanelOf(window)));
    }

    // ---- The list's own request subscription ---------------------------------

    [AvaloniaFact]
    public void Closing_The_Window_Releases_The_Mapping_Lists_Request_Subscription()
    {
        var (window, viewModel, _, _) = BuildWindow();
        Settle(window);

        // The list answers the view model's card-visible request itself, so it holds a
        // subscription for exactly as long as its window is open: a window built afterwards
        // over the same view model subscribes once, not twice.
        Assert.Equal(1, SubscriberCount(viewModel, nameof(MainWindowViewModel.MappingCardVisibleRequested), typeof(LazyMappingList)));

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, SubscriberCount(viewModel, nameof(MainWindowViewModel.MappingCardVisibleRequested), typeof(LazyMappingList)));
    }

    // ---- The first open's cost -----------------------------------------------

    [AvaloniaFact]
    public void The_First_Open_Builds_Its_Surface_Without_A_Pathological_Delay()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, DeziroJoy.Core.Input.GamepadState.Create(default, 0f, 0f, 0f, 0f));
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        // The first open is the one that pays for the surface's construction, and the very
        // first one in a process also pays the JIT and theme warm-up. The budget is generous
        // on purpose: it catches a surface that has become pathologically expensive (a
        // runaway template, an accidental second realization pass) without pinning a
        // machine-dependent number. Measured on the development machine: the first settings
        // open 46-102 ms, the first editor open 42-231 ms.
        const int BudgetMilliseconds = 2000;

        var settingsStopwatch = Stopwatch.StartNew();
        viewModel.OpenSettingsCommand.Execute(null);
        Settle(window);
        settingsStopwatch.Stop();

        Assert.NotNull(SettingsPanelOf(window));
        Assert.True(
            settingsStopwatch.ElapsedMilliseconds < BudgetMilliseconds,
            $"the first settings open took {settingsStopwatch.ElapsedMilliseconds} ms");

        viewModel.CloseSettingsCommand.Execute(null);
        Settle(window);

        var editorStopwatch = Stopwatch.StartNew();
        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));
        Settle(window);
        editorStopwatch.Stop();

        Assert.NotNull(EditorPanelOf(window));
        Assert.True(
            editorStopwatch.ElapsedMilliseconds < BudgetMilliseconds,
            $"the first editor open took {editorStopwatch.ElapsedMilliseconds} ms");
    }
}
