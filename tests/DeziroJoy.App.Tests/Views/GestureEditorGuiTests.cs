using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.App.Views.Converters;
using DeziroJoy.App.Views.Controls;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using CoreKeyModifiers = DeziroJoy.Core.Mapping.KeyModifiers;
using CoreMouseButton = DeziroJoy.Core.Mapping.MouseButton;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The gesture editor surface. 单击 / 双击 / 长按 are three equal peers: one switcher leads the
/// editor, and every slot is configured through the same target controls bound to
/// <see cref="MappingEditorViewModel.ActiveSlot"/>. The optional categorized key list stays
/// collapsed until it is asked for, and a commit applies all three slots in one entry.
/// </summary>
public sealed class GestureEditorGuiTests
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

    private static void ConnectPad(FakeGamepadProvider provider, GamepadButtonMask buttons = default)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(buttons, 0f, 0f, 0f, 0f));
    }

    private static void Click(Control target, Window window)
    {
        window.Focus();
        Dispatcher.UIThread.RunJobs();

        window.UpdateLayout();
        // The editor's body scrolls, so a control below the fold must be scrolled into view
        // first - what a user does before clicking it. The scroll lands on the next
        // dispatcher cycle, so the jobs are run before the hit point is measured.
        target.BringIntoView();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static GamepadVisualizer Model(Window window)
        => window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;

    private static MappingEditorPanel EditorPanelOf(Window window)
        => window.GetLogicalDescendants().OfType<MappingEditorPanel>().First();

    private static (MainWindow Window, MainWindowViewModel ViewModel) OpenEditorOn(string element)
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(Model(window).FindControl<Control>(element)!, window);
        Assert.True(viewModel.IsEditing);

        return (window, viewModel);
    }

    private static void SelectTab(Window window, MainWindowViewModel viewModel, string tabName)
    {
        Click(EditorPanelOf(window).FindControl<RadioButton>(tabName)!, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void ChooseTargetKind(Window window, MappingTargetKind kind)
    {
        var combo = EditorPanelOf(window).FindControl<ComboBox>("TargetKindCombo")!;
        combo.SelectedItem = kind;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void ChooseKey(Window window, GamepadKey key)
    {
        // The picker is a grid of key-cap buttons, and a gesture slot that is 已关闭 keeps
        // its target parameters disabled - so the choice is written the way the old list's
        // SelectedItem was: straight onto the editor, which is exactly what the grid's
        // click handler does. The click and keyboard paths have tests of their own.
        var editor = (MappingEditorViewModel)EditorPanelOf(window).DataContext!;
        editor.ActiveSelectedKeyOption = editor.KeyOptions.First(option => !option.IsHeader && option.Key == key);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static Button KeyCapButton(Window window, GamepadKey key)
        => EditorPanelOf(window).GetLogicalDescendants().OfType<Button>()
            .First(button => button.DataContext is KeyPickerItem item && item.Key == key);

    private static RadioButton KeyCategorySegment(Window window, string name)
        => EditorPanelOf(window).GetLogicalDescendants().OfType<RadioButton>()
            .First(button => button.Classes.Contains("keyCategorySegment") && button.Content as string == name);

    private static void ChooseDirection(Window window, InputDirection direction)
    {
        var editor = (MappingEditorViewModel)EditorPanelOf(window).DataContext!;
        var combo = EditorPanelOf(window).FindControl<ComboBox>("DirectionCombo")!;
        combo.SelectedItem = editor.DirectionOptions.First(option => option.Direction == direction);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertDirectionShown(Window window, MainWindowViewModel viewModel, InputDirection direction)
    {
        // Both the editor's state and the picker row on screen carry the direction, so a
        // reopened mapping never shows the previous edit's choice.
        Assert.Equal(direction, viewModel.Editor.SelectedDirection);
        Assert.Equal(
            direction,
            Assert.IsType<DirectionPickerItem>(
                EditorPanelOf(window).FindControl<ComboBox>("DirectionCombo")!.SelectedItem).Direction);
    }

    private static void ExpandKeyList(Window window)
    {
        var expander = EditorPanelOf(window).FindControl<Expander>("KeyPickerExpander")!;
        expander.IsExpanded = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static Color ColorOf(IBrush? brush)
        => Assert.IsType<ISolidColorBrush>(brush, exactMatch: false).Color;

    private static Color Token(ThemeVariant variant, string key)
    {
        var app = Application.Current!;
        app.TryFindResource(key, variant, out var brush);
        return ColorOf(brush as IBrush);
    }

    // ---- The switcher leads, one editor serves every slot -------------------

    [AvaloniaFact]
    public void The_Gesture_Switcher_Leads_The_Shared_Target_Editor()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        var panel = EditorPanelOf(window);
        var switcher = panel.FindControl<Border>("GestureSwitcher")!;
        var targetKind = panel.FindControl<ComboBox>("TargetKindCombo")!;

        var switcherTop = switcher.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        var targetTop = targetKind.TranslatePoint(new Point(0, 0), window)!.Value.Y;

        Assert.True(switcherTop < targetTop, "the gesture switcher must lead the shared target editor");

        // The switcher is the first section of the body, above the target editor's own label.
        var targetLabel = panel.GetLogicalDescendants().OfType<TextBlock>()
            .First(text => text.Text == "目标动作");
        Assert.True(switcherTop < targetLabel.TranslatePoint(new Point(0, 0), window)!.Value.Y);
    }

    [AvaloniaFact]
    public void One_Shared_Target_Editor_Serves_All_Three_Slots()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        var panel = EditorPanelOf(window);

        // There is exactly one target-kind combo and one categorized key grid: the
        // per-gesture action dropdowns the old surface carried are gone, not merely hidden.
        Assert.Single(panel.GetLogicalDescendants().OfType<ComboBox>(), c => c.Name == "TargetKindCombo");
        Assert.Single(panel.GetLogicalDescendants().OfType<ItemsControl>(), c => c.Name == "KeyCategoryGrid");
        Assert.DoesNotContain(panel.GetLogicalDescendants().OfType<ListBox>(), c => c.Name == "TargetKeyList");
        Assert.DoesNotContain(panel.GetLogicalDescendants().OfType<ComboBox>(),
            c => c.Name is "GestureDoubleClickActionCombo" or "GestureLongPressActionCombo");

        // The click leads, and the shared editor is bound to it.
        Assert.Equal(GestureKind.Click, viewModel.Editor.SelectedGestureKind);
        Assert.Same(viewModel.Editor.ClickSlot, viewModel.Editor.ActiveSlot);

        ChooseTargetKind(window, MappingTargetKind.KeyCombo);
        Assert.Equal(MappingTargetKind.KeyCombo, viewModel.Editor.ClickSlot.TargetKind);
        Assert.Equal(MappingTargetKind.KeyPress, viewModel.Editor.DoubleClickSlot.TargetKind);
        Assert.Equal(MappingTargetKind.KeyPress, viewModel.Editor.LongPressSlot.TargetKind);

        // Switching the tab moves the same editor onto the next slot.
        SelectTab(window, viewModel, "GestureDoubleClickTab");
        Assert.Same(viewModel.Editor.DoubleClickSlot, viewModel.Editor.ActiveSlot);
        Assert.Equal(MappingTargetKind.KeyPress, viewModel.Editor.ActiveSlot.TargetKind);

        ChooseTargetKind(window, MappingTargetKind.MouseButton);
        Assert.Equal(MappingTargetKind.MouseButton, viewModel.Editor.DoubleClickSlot.TargetKind);
        Assert.Equal(MappingTargetKind.KeyCombo, viewModel.Editor.ClickSlot.TargetKind);
    }

    [AvaloniaFact]
    public void Every_Slot_Produces_A_Key_Press_Through_The_Shared_Editor()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        foreach (var (tab, slot) in new[]
        {
            ("GestureClickTab", (Func<GestureSlotEditor>)(() => viewModel.Editor.ClickSlot)),
            ("GestureDoubleClickTab", () => viewModel.Editor.DoubleClickSlot),
            ("GestureLongPressTab", () => viewModel.Editor.LongPressSlot),
        })
        {
            SelectTab(window, viewModel, tab);
            ChooseTargetKind(window, MappingTargetKind.KeyPress);
            ChooseKey(window, GamepadKey.Space);

            Assert.Equal(new KeyPressTarget(GamepadKey.Space), slot().BuildTarget());
        }

        Assert.Equal(new KeyPressTarget(GamepadKey.Space), viewModel.Editor.ClickSlot.BuildTarget());
        Assert.Equal(new KeyPressTarget(GamepadKey.Space), viewModel.Editor.DoubleClickSlot.BuildTarget());
        Assert.Equal(new KeyPressTarget(GamepadKey.Space), viewModel.Editor.LongPressSlot.BuildTarget());
    }

    [AvaloniaFact]
    public void Every_Slot_Produces_A_Combo_Through_The_Shared_Editor()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        foreach (var tab in new[] { "GestureClickTab", "GestureDoubleClickTab", "GestureLongPressTab" })
        {
            SelectTab(window, viewModel, tab);
            ChooseTargetKind(window, MappingTargetKind.KeyCombo);
            ChooseKey(window, GamepadKey.C);

            var ctrl = EditorPanelOf(window).FindControl<CheckBox>("ModifierCtrlBox")!;
            Click(ctrl, window);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                new KeyComboTarget(CoreKeyModifiers.Ctrl, GamepadKey.C),
                viewModel.Editor.ActiveSlot.BuildTarget());
        }
    }

    [AvaloniaFact]
    public void Every_Slot_Produces_No_Function_Through_The_Shared_Editor()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        foreach (var (tab, isGesture) in new[]
        {
            ("GestureClickTab", false),
            ("GestureDoubleClickTab", true),
            ("GestureLongPressTab", true),
        })
        {
            SelectTab(window, viewModel, tab);

            if (isGesture)
            {
                Click(EditorPanelOf(window).FindControl<CheckBox>("ActiveGestureEnabledBox")!, window);
            }

            Click(EditorPanelOf(window).FindControl<CheckBox>("ActiveNoneBox")!, window);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.True(viewModel.Editor.ActiveSlot.IsNone);
            Assert.IsType<NoneTarget>(viewModel.Editor.ActiveSlot.BuildTarget());
        }

        Assert.IsType<NoneTarget>(viewModel.Editor.ClickSlot.BuildTarget());
        Assert.IsType<NoneTarget>(viewModel.Editor.DoubleClickSlot.BuildAction());
        Assert.IsType<NoneTarget>(viewModel.Editor.LongPressSlot.BuildAction());
    }

    [AvaloniaFact]
    public void None_Is_Reversible_On_Every_Slot_Through_The_Shared_Editor()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        foreach (var (tab, isGesture) in new[]
        {
            ("GestureClickTab", false),
            ("GestureDoubleClickTab", true),
            ("GestureLongPressTab", true),
        })
        {
            SelectTab(window, viewModel, tab);

            if (isGesture)
            {
                Click(EditorPanelOf(window).FindControl<CheckBox>("ActiveGestureEnabledBox")!, window);
            }

            var noneBox = EditorPanelOf(window).FindControl<CheckBox>("ActiveNoneBox")!;

            // 无功能 first: the slot resolves and emits nothing.
            Click(noneBox, window);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.True(viewModel.Editor.ActiveSlot.IsNone);
            Assert.IsType<NoneTarget>(viewModel.Editor.ActiveSlot.BuildTarget());

            // Choosing it must not disable the box that takes it back: only the target
            // parameters go dark, so the way out of 无功能 is the same box that led in.
            Assert.True(
                noneBox.IsEffectivelyEnabled,
                "checking 无功能 must not disable the None selector itself");

            Click(noneBox, window);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.False(viewModel.Editor.ActiveSlot.IsNone);

            // A real target of the slot's own, and back to 无功能 through the same box.
            ChooseTargetKind(window, MappingTargetKind.KeyPress);
            ChooseKey(window, GamepadKey.Space);
            Assert.Equal(new KeyPressTarget(GamepadKey.Space), viewModel.Editor.ActiveSlot.BuildTarget());

            Click(noneBox, window);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.True(viewModel.Editor.ActiveSlot.IsNone);
            Assert.IsType<NoneTarget>(viewModel.Editor.ActiveSlot.BuildTarget());
        }
    }

    [AvaloniaFact]
    public void A_Reopened_Saved_None_Keeps_The_Selector_Live()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        // Save a mapping whose own target is 无功能.
        SelectTab(window, viewModel, "GestureClickTab");
        Click(EditorPanelOf(window).FindControl<CheckBox>("ActiveNoneBox")!, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Click(EditorPanelOf(window).FindControl<Button>("CommitButton")!, window);
        Assert.False(viewModel.IsEditing);

        // Reopen the saved mapping: the click comes back 无功能, and the box that says so is
        // live - a saved None is a choice the user can still take back, not a one-way door.
        Click(Model(window).FindControl<Control>("ButtonA")!, window);
        Assert.True(viewModel.IsEditing);

        var noneBox = EditorPanelOf(window).FindControl<CheckBox>("ActiveNoneBox")!;
        Assert.True(viewModel.Editor.ClickSlot.IsNone);
        Assert.True(noneBox.IsChecked);
        Assert.True(noneBox.IsEffectivelyEnabled, "a reopened 无功能 must stay reversible");

        Click(noneBox, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.Editor.ClickSlot.IsNone);

        ChooseTargetKind(window, MappingTargetKind.KeyPress);
        ChooseKey(window, GamepadKey.Space);
        Assert.Equal(new KeyPressTarget(GamepadKey.Space), viewModel.Editor.BuildTarget());

        Click(EditorPanelOf(window).FindControl<Button>("CommitButton")!, window);

        var committed = viewModel.ActiveProfile!.Mappings
            .First(mapping => mapping.Source is ButtonBinding { Button: GamepadButton.A });

        Assert.Equal(new KeyPressTarget(GamepadKey.Space), committed.Target);
    }

    [AvaloniaFact]
    public void Switching_Tabs_Preserves_Each_Slots_Draft()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        SelectTab(window, viewModel, "GestureClickTab");
        ChooseTargetKind(window, MappingTargetKind.KeyCombo);
        ChooseKey(window, GamepadKey.C);
        Click(EditorPanelOf(window).FindControl<CheckBox>("ModifierCtrlBox")!, window);

        SelectTab(window, viewModel, "GestureDoubleClickTab");
        ChooseTargetKind(window, MappingTargetKind.KeyPress);
        ChooseKey(window, GamepadKey.Space);

        SelectTab(window, viewModel, "GestureLongPressTab");
        ChooseTargetKind(window, MappingTargetKind.MouseButton);
        var mouseCombo = EditorPanelOf(window).FindControl<ComboBox>("MouseButtonCombo")!;
        mouseCombo.SelectedItem = CoreMouseButton.Right;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // Away and back: every draft is exactly where its slot left it.
        SelectTab(window, viewModel, "GestureClickTab");
        Assert.Equal(MappingTargetKind.KeyCombo, viewModel.Editor.ClickSlot.TargetKind);
        Assert.Equal(GamepadKey.C, viewModel.Editor.ClickSlot.SelectedKey);
        Assert.True(viewModel.Editor.ClickSlot.ModifierCtrl);

        SelectTab(window, viewModel, "GestureDoubleClickTab");
        Assert.Equal(MappingTargetKind.KeyPress, viewModel.Editor.DoubleClickSlot.TargetKind);
        Assert.Equal(GamepadKey.Space, viewModel.Editor.DoubleClickSlot.SelectedKey);

        SelectTab(window, viewModel, "GestureLongPressTab");
        Assert.Equal(MappingTargetKind.MouseButton, viewModel.Editor.LongPressSlot.TargetKind);
        Assert.Equal(CoreMouseButton.Right, viewModel.Editor.LongPressSlot.SelectedMouseButton);

        // The shared editor shows the slot it is on, not a stale one.
        Assert.Equal(MappingTargetKind.MouseButton, EditorPanelOf(window).FindControl<ComboBox>("TargetKindCombo")!.SelectedItem);
    }

    // ---- 已关闭 is not 无功能 ------------------------------------------------

    [AvaloniaFact]
    public void A_Disabled_Gesture_Is_Distinct_From_No_Function()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        SelectTab(window, viewModel, "GestureDoubleClickTab");

        var enabledBox = EditorPanelOf(window).FindControl<CheckBox>("ActiveGestureEnabledBox")!;
        Assert.True(enabledBox.IsVisible, "a gesture slot must offer its own on/off");
        Assert.False(viewModel.Editor.HasDoubleClick);

        // 已关闭: the gesture never resolves, so no timing window is written at all.
        var off = Assert.IsType<ButtonBinding>(viewModel.Editor.BuildSource()).Gesture;
        Assert.Null(off);

        // Enabling and choosing 无功能 is a different state: the gesture resolves and emits
        // nothing, which needs the double-click window to exist.
        Click(enabledBox, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.Editor.HasDoubleClick);

        Click(EditorPanelOf(window).FindControl<CheckBox>("ActiveNoneBox")!, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var none = Assert.IsType<ButtonBinding>(viewModel.Editor.BuildSource()).Gesture;
        Assert.NotNull(none);
        Assert.Equal(ButtonGesture.DefaultDoubleClickWindow, none.DoubleClickWindow);
        Assert.IsType<NoneTarget>(none.DoubleClickAction);

        // Turning it back off drops the action rather than leaving a dormant one behind.
        Click(enabledBox, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.Editor.HasDoubleClick);
        Assert.Null(Assert.IsType<ButtonBinding>(viewModel.Editor.BuildSource()).Gesture);
    }

    [AvaloniaFact]
    public void The_Click_Has_No_Off_State_Only_No_Function()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        var panel = EditorPanelOf(window);

        // The click is the mapping's own target, so it has no off state of its own; only the
        // two gestures carry one, and the row that offers it is hidden for the click.
        Assert.False(panel.FindControl<StackPanel>("ActiveGestureRow")!.IsVisible);
        Assert.True(panel.FindControl<CheckBox>("ActiveNoneBox")!.IsVisible);

        Click(panel.FindControl<CheckBox>("ActiveNoneBox")!, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.Editor.ClickIsNone);
        Assert.IsType<NoneTarget>(viewModel.Editor.BuildTarget());
    }

    [AvaloniaFact]
    public void The_Legacy_Repeat_The_Click_State_Stays_Reachable()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        SelectTab(window, viewModel, "GestureDoubleClickTab");

        // A fresh gesture slot carries the legacy state: no function of its own, so it
        // re-fires the click. Enabling the gesture keeps that state rather than inventing a
        // target the user never asked for.
        Click(EditorPanelOf(window).FindControl<CheckBox>("ActiveGestureEnabledBox")!, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.Editor.DoubleClickSlot.RepeatsClick);
        Assert.Null(viewModel.Editor.DoubleClickAction);
        Assert.True(viewModel.Editor.HasDoubleClick);

        var gesture = Assert.IsType<ButtonBinding>(viewModel.Editor.BuildSource()).Gesture;
        Assert.NotNull(gesture);
        Assert.Null(gesture.DoubleClickAction);

        // And the user can leave it for a target of the slot's own.
        Click(EditorPanelOf(window).FindControl<CheckBox>("ActiveRepeatBox")!, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.Editor.DoubleClickSlot.RepeatsClick);
        Assert.Equal(new KeyPressTarget(GamepadKey.Enter), viewModel.Editor.DoubleClickAction);
    }

    // ---- The optional key list stays collapsed -------------------------------

    [AvaloniaFact]
    public void The_Key_List_Starts_Collapsed_And_Expands()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        var expander = EditorPanelOf(window).FindControl<Expander>("KeyPickerExpander")!;

        Assert.False(expander.IsExpanded, "the optional key list must start collapsed");
        Assert.False(viewModel.Editor.KeyPickerExpanded);

        // Collapsed, it must not crowd the editor: the card keeps the model in view.
        var collapsedHeight = expander.Bounds.Height;

        expander.IsExpanded = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(expander.IsExpanded);
        Assert.True(viewModel.Editor.KeyPickerExpanded);
        Assert.True(expander.Bounds.Height > collapsedHeight, "expanding must reveal the list");

        // The expanded picker leads with its category navigator: one segment per category,
        // in the picker's own order, with exactly the selected key's category checked.
        var segments = EditorPanelOf(window).GetLogicalDescendants().OfType<RadioButton>()
            .Where(button => button.Classes.Contains("keyCategorySegment"))
            .ToArray();

        Assert.Equal(
            ["面部按键", "肩键与扳机", "摇杆", "十字键", "系统键", "扩展按键"],
            segments.Select(segment => segment.Content as string));

        // The loaded A mapping is A -> Esc, a 系统键 key, so that is the segment the picker
        // opens on - and the only one that is checked.
        Assert.Equal(
            ["系统键"],
            segments.Where(segment => segment.IsChecked == true).Select(segment => segment.Content as string));
        Assert.Equal(6, viewModel.Editor.ActiveKeyCategoryColumns);
    }

    [AvaloniaFact]
    public void Collapsing_The_Key_List_Preserves_The_Selection()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        ExpandKeyList(window);
        ChooseKey(window, GamepadKey.F13);

        Assert.Equal(GamepadKey.F13, viewModel.Editor.ActiveSlot.SelectedKey);

        var expander = EditorPanelOf(window).FindControl<Expander>("KeyPickerExpander")!;
        expander.IsExpanded = false;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        expander.IsExpanded = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(GamepadKey.F13, viewModel.Editor.ActiveSlot.SelectedKey);
        Assert.Equal(GamepadKey.F13, viewModel.Editor.ActiveSelectedKeyOption!.Key);

        // The cap itself comes back marked, so the selection survives both states visually.
        Assert.Contains("selected", KeyCapButton(window, GamepadKey.F13).Classes);
    }

    [AvaloniaFact]
    public void Key_List_Headers_Are_Not_Selectable()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        ExpandKeyList(window);
        ChooseKey(window, GamepadKey.F13);

        var header = viewModel.Editor.KeyOptions.First(item => item.IsHeader);
        viewModel.Editor.ActiveSelectedKeyOption = header;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(GamepadKey.F13, viewModel.Editor.ActiveSlot.SelectedKey);
        Assert.Equal(GamepadKey.F13, viewModel.Editor.ActiveSelectedKeyOption!.Key);
    }

    [AvaloniaFact]
    public void A_New_Editor_Opens_With_The_Key_List_Collapsed()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        ExpandKeyList(window);
        Assert.True(EditorPanelOf(window).FindControl<Expander>("KeyPickerExpander")!.IsExpanded);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(viewModel.IsEditing);

        Click(Model(window).FindControl<Control>("ButtonB")!, window);
        Assert.True(viewModel.IsEditing);

        Assert.False(
            EditorPanelOf(window).FindControl<Expander>("KeyPickerExpander")!.IsExpanded,
            "every new editor must open with the key list collapsed");
    }

    // ---- Capture writes the slot being edited --------------------------------

    [AvaloniaFact]
    public void Keyboard_Capture_Writes_The_Slot_Being_Edited()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        SelectTab(window, viewModel, "GestureDoubleClickTab");
        Click(EditorPanelOf(window).FindControl<CheckBox>("ActiveGestureEnabledBox")!, window);
        Click(EditorPanelOf(window).FindControl<Button>("CaptureKeyButton")!, window);
        Assert.True(viewModel.Editor.IsCapturingKey);

        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        window.KeyRelease(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);

        Assert.False(viewModel.Editor.IsCapturingKey);
        Assert.Equal(MappingTargetKind.KeyCombo, viewModel.Editor.DoubleClickSlot.TargetKind);
        Assert.Equal(GamepadKey.C, viewModel.Editor.DoubleClickSlot.SelectedKey);
        Assert.True(viewModel.Editor.DoubleClickSlot.ModifierCtrl);

        // The click's own target is untouched by a capture aimed at another slot. A carries
        // the default profile's A -> Esc mapping, so the loaded value must survive.
        Assert.Equal(MappingTargetKind.KeyPress, viewModel.Editor.ClickSlot.TargetKind);
        Assert.Equal(GamepadKey.Escape, viewModel.Editor.ClickSlot.SelectedKey);
    }

    // ---- The direction picker for the cross combinations ---------------------

    [AvaloniaFact]
    public void The_Direction_Picker_Appears_For_A_Stick_Driving_A_Digital_Target()
    {
        var (window, viewModel) = OpenEditorOn("LeftStickPad");

        var panel = EditorPanelOf(window);
        var row = panel.FindControl<StackPanel>("DirectionRow")!;

        // The loaded mapping is the stick driving the wheel, which consumes both axes:
        // there is no direction to choose until the target becomes a digital one.
        Assert.False(row.IsVisible);

        ChooseTargetKind(window, MappingTargetKind.KeyPress);
        Assert.True(row.IsVisible, "a stick driving a key needs a direction");

        var combo = panel.FindControl<ComboBox>("DirectionCombo")!;
        Assert.Equal(4, combo.ItemCount);
        Assert.Equal(
            ["上", "下", "左", "右"],
            combo.Items.Cast<DirectionPickerItem>().Select(item => item.Label));

        // A native pairing takes the row away again: a stick driving mouse move has no
        // direction, so offering one would only configure a value the engine ignores.
        ChooseTargetKind(window, MappingTargetKind.MouseMove);
        Assert.False(row.IsVisible);
    }

    [AvaloniaFact]
    public void The_Direction_Picker_Appears_For_A_Button_Driving_A_Continuous_Target()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        var panel = EditorPanelOf(window);
        var row = panel.FindControl<StackPanel>("DirectionRow")!;

        // A drives a key in the default profile, and a key has no axis to deflect.
        Assert.False(row.IsVisible);

        foreach (var kind in new[] { MappingTargetKind.MouseMove, MappingTargetKind.WheelScroll })
        {
            ChooseTargetKind(window, kind);
            Assert.True(row.IsVisible, $"a button driving {kind} needs a direction");
        }

        // The pick writes through to the mapping's source, and the source description
        // names the direction the picker was given.
        Assert.Equal(4, panel.FindControl<ComboBox>("DirectionCombo")!.ItemCount);
        ChooseDirection(window, InputDirection.Right);

        Assert.Equal(InputDirection.Right, viewModel.Editor.SelectedDirection);
        Assert.Equal(
            InputDirection.Right,
            Assert.IsType<ButtonBinding>(viewModel.Editor.BuildSource()).Direction);
        Assert.Equal("A 键（右）", viewModel.Editor.SourceDescription);
    }

    [AvaloniaFact]
    public void A_Committed_Direction_Survives_Commit_And_Reopen()
    {
        var (window, viewModel) = OpenEditorOn("LeftStickPad");
        ChooseTargetKind(window, MappingTargetKind.KeyPress);
        ChooseKey(window, GamepadKey.W);
        ChooseDirection(window, InputDirection.Right);

        Assert.Equal("左摇杆（右）", viewModel.Editor.SourceDescription);

        Click(EditorPanelOf(window).FindControl<Button>("CommitButton")!, window);
        Assert.False(viewModel.IsEditing);

        var committed = viewModel.ActiveProfile!.Mappings
            .First(mapping => mapping.Source is StickBinding { Stick: GamepadStick.Left });

        Assert.Equal(InputDirection.Right, Assert.IsType<StickBinding>(committed.Source).Direction);

        // The mapping row names the direction too, so 上/下/左/右 is no longer invisible
        // once the editor is closed.
        var row = viewModel.Mappings.First(r => r.Entry == committed);
        Assert.Equal("左摇杆（右）", row.SourceText);

        // Reopen the saved mapping: the direction comes back, both in the editor's state
        // and in the picker row that displays it.
        Click(Model(window).FindControl<Control>("LeftStickPad")!, window);
        Assert.True(viewModel.IsEditing);

        AssertDirectionShown(window, viewModel, InputDirection.Right);
        Assert.Equal("左摇杆（右）", viewModel.Editor.SourceDescription);
    }

    [AvaloniaFact]
    public void Reopening_Different_Mappings_Shows_Each_Ones_Saved_Direction()
    {
        var (window, viewModel) = OpenEditorOn("LeftStickPad");

        // First mapping: the left stick presses W when pushed right.
        ChooseTargetKind(window, MappingTargetKind.KeyPress);
        ChooseKey(window, GamepadKey.W);
        ChooseDirection(window, InputDirection.Right);
        Click(EditorPanelOf(window).FindControl<Button>("CommitButton")!, window);
        Assert.False(viewModel.IsEditing);

        // Second mapping on the other stick, with its own different direction.
        Click(Model(window).FindControl<Control>("RightStickPad")!, window);
        Assert.True(viewModel.IsEditing);
        ChooseTargetKind(window, MappingTargetKind.KeyPress);
        ChooseKey(window, GamepadKey.K);
        ChooseDirection(window, InputDirection.Left);
        Click(EditorPanelOf(window).FindControl<Button>("CommitButton")!, window);
        Assert.False(viewModel.IsEditing);

        // Reopen each in turn: the picker shows the direction that mapping saved, never
        // the one the previous edit chose.
        Click(Model(window).FindControl<Control>("LeftStickPad")!, window);
        Assert.True(viewModel.IsEditing);
        AssertDirectionShown(window, viewModel, InputDirection.Right);

        Click(Model(window).FindControl<Control>("RightStickPad")!, window);
        Assert.True(viewModel.IsEditing);
        AssertDirectionShown(window, viewModel, InputDirection.Left);
    }

    [AvaloniaFact]
    public void A_Stick_Bound_To_A_Special_Key_Commits_With_Its_Direction()
    {
        // A special key is a digital target: without a direction the engine refuses the
        // whole mapping at application time, so the picker has to appear and the commit
        // has to carry the choice.
        var (window, viewModel) = OpenEditorOn("LeftStickPad");
        ChooseTargetKind(window, MappingTargetKind.SpecialKey);

        var row = EditorPanelOf(window).FindControl<StackPanel>("DirectionRow")!;
        Assert.True(row.IsVisible, "a stick driving a special key needs a direction");

        ChooseDirection(window, InputDirection.Left);
        Click(EditorPanelOf(window).FindControl<Button>("CommitButton")!, window);
        Assert.False(viewModel.IsEditing);

        var committed = viewModel.ActiveProfile!.Mappings
            .First(mapping => mapping.Source is StickBinding { Stick: GamepadStick.Left });

        Assert.IsType<SpecialKeyTarget>(committed.Target);
        Assert.Equal(InputDirection.Left, Assert.IsType<StickBinding>(committed.Source).Direction);

        // Reopening keeps the direction on both the state and the picker row.
        Click(Model(window).FindControl<Control>("LeftStickPad")!, window);
        Assert.True(viewModel.IsEditing);
        AssertDirectionShown(window, viewModel, InputDirection.Left);
    }

    [AvaloniaFact]
    public void Choosing_A_Direction_Refreshes_The_Bound_Header()
    {
        var (window, viewModel) = OpenEditorOn("LeftStickPad");
        ChooseTargetKind(window, MappingTargetKind.KeyPress);

        Assert.Equal("左摇杆（上）", viewModel.Editor.SourceDescription);

        // The header TextBlock the panel binds SourceDescription to, captured before the
        // choice so the assertion is on the rendered control, not on the getter.
        var header = EditorPanelOf(window).GetLogicalDescendants().OfType<TextBlock>()
            .First(text => text.Text == viewModel.Editor.SourceDescription);

        var raised = new List<string>();
        viewModel.Editor.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        ChooseDirection(window, InputDirection.Right);

        Assert.Equal("左摇杆（右）", header.Text);
        Assert.Contains(nameof(MappingEditorViewModel.SourceDescription), raised);
        Assert.Equal("左摇杆（右）", viewModel.Editor.SourceDescription);
    }

    // ---- Commit applies every slot in one entry ------------------------------

    [AvaloniaFact]
    public void Commit_Applies_All_Three_Slots_In_One_Entry()
    {
        var (window, viewModel) = OpenEditorOn("LeftBumper");
        var list = window.FindControl<ItemsControl>("MappingsList")!;
        var before = list.ItemCount;

        SelectTab(window, viewModel, "GestureClickTab");
        ChooseTargetKind(window, MappingTargetKind.KeyCombo);
        ChooseKey(window, GamepadKey.C);
        Click(EditorPanelOf(window).FindControl<CheckBox>("ModifierCtrlBox")!, window);

        SelectTab(window, viewModel, "GestureDoubleClickTab");
        Click(EditorPanelOf(window).FindControl<CheckBox>("ActiveGestureEnabledBox")!, window);
        ChooseTargetKind(window, MappingTargetKind.KeyPress);
        ChooseKey(window, GamepadKey.Space);

        SelectTab(window, viewModel, "GestureLongPressTab");
        Click(EditorPanelOf(window).FindControl<CheckBox>("ActiveGestureEnabledBox")!, window);
        Click(EditorPanelOf(window).FindControl<CheckBox>("ActiveNoneBox")!, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Click(EditorPanelOf(window).FindControl<Button>("CommitButton")!, window);

        Assert.False(viewModel.IsEditing);
        Assert.Equal(before + 1, list.ItemCount);

        var committed = viewModel.ActiveProfile!.Mappings
            .First(mapping => mapping.Source is ButtonBinding { Button: GamepadButton.LeftBumper });

        Assert.Equal(new KeyComboTarget(CoreKeyModifiers.Ctrl, GamepadKey.C), committed.Target);

        var gesture = Assert.IsType<ButtonBinding>(committed.Source).Gesture;
        Assert.NotNull(gesture);
        Assert.Equal(new KeyPressTarget(GamepadKey.Space), gesture.DoubleClickAction);
        Assert.IsType<NoneTarget>(gesture.LongPressAction);
    }

    [AvaloniaFact]
    public void Commit_Is_Atomic_For_The_Edited_Mapping_Only()
    {
        var (window, viewModel) = OpenEditorOn("ButtonB");
        var list = window.FindControl<ItemsControl>("MappingsList")!;

        // B already maps to Enter; editing it must replace that one entry, not add a second.
        Assert.Contains(
            list.Items.OfType<MappingRowViewModel>(),
            row => row.Entry.Source == new ButtonBinding(GamepadButton.B));

        SelectTab(window, viewModel, "GestureDoubleClickTab");
        Click(EditorPanelOf(window).FindControl<CheckBox>("ActiveGestureEnabledBox")!, window);
        ChooseTargetKind(window, MappingTargetKind.KeyPress);
        ChooseKey(window, GamepadKey.Space);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Click(EditorPanelOf(window).FindControl<Button>("CommitButton")!, window);

        var rows = list.Items.OfType<MappingRowViewModel>()
            .Where(row => row.Entry.Source is ButtonBinding { Button: GamepadButton.B })
            .ToArray();

        Assert.Single(rows);
        Assert.Equal(new KeyPressTarget(GamepadKey.Enter), rows[0].Entry.Target);
        Assert.Equal(
            new KeyPressTarget(GamepadKey.Space),
            Assert.IsType<ButtonBinding>(rows[0].Entry.Source).Gesture!.DoubleClickAction);
    }

    // ---- The operation row ---------------------------------------------------

    [AvaloniaFact]
    public void The_Operation_Row_Is_Fixed_And_Carries_No_Keyboard_Shortcut()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        var panel = EditorPanelOf(window);
        var commit = panel.FindControl<Button>("CommitButton")!;
        var cancel = panel.FindControl<Button>("CancelButton")!;

        Assert.Equal("确定", commit.Content);
        Assert.Equal("取消", cancel.Content);
        Assert.False(commit.IsDefault);
        Assert.False(cancel.IsCancel);

        // The row sits outside the scrolling body, so it never scrolls away.
        var scroller = panel.GetVisualDescendants().OfType<ScrollViewer>().First();
        var scrolled = scroller.GetVisualDescendants().ToArray();
        Assert.Equal(0, scrolled.Count(v => v == commit || v == cancel));

        // Readable hit targets without turning the card into a wall.
        Assert.True(commit.Bounds.Height >= 30, $"the commit button is only {commit.Bounds.Height}px tall");
        Assert.True(cancel.Bounds.Height >= 30, $"the cancel button is only {cancel.Bounds.Height}px tall");
    }

    [AvaloniaFact]
    public void The_Editor_Opens_As_The_Existing_Overlay_Not_A_New_Window()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        var panel = window.FindControl<Border>("EditorPanel")!;
        Assert.True(panel.IsVisible);

        // The model and the source list stay on screen beside it.
        Assert.True(Model(window).IsVisible);
        Assert.True(window.FindControl<ItemsControl>("MappingsList")!.IsVisible);

        var modelTop = Model(window).TranslatePoint(new Point(0, 0), window)!.Value.Y;
        var panelTop = panel.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        Assert.True(modelTop < panelTop, "the model must stay above the editor");
    }

    // ---- The expanded picker is a navigator over one compact grid -------------

    [AvaloniaFact]
    public void The_Expanded_Picker_Is_A_Compact_Grid_Across_The_Full_Card_Width()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        ExpandKeyList(window);

        var panel = EditorPanelOf(window);
        var grid = panel.GetVisualDescendants().OfType<UniformGrid>().Single();

        // One category at a time: the grid is the active category's keys - 系统键's eleven,
        // since A maps to Esc - packed into as many columns as they fill, not the whole
        // picker stacked section over section.
        Assert.Equal(6, grid.Columns);
        Assert.Equal(11, grid.Children.Count);

        // The grid takes the editor card's width: not a narrow centered popup, and not a
        // column beside the rest of the editor.
        Assert.True(grid.Bounds.Width > 400, $"the grid is only {grid.Bounds.Width}px wide");
        Assert.True(
            grid.Bounds.Width >= panel.Bounds.Width * 0.9,
            $"the grid ({grid.Bounds.Width}px) must fill the card ({panel.Bounds.Width}px)");

        // Six caps share the first row, each a comfortable hit target even at the denser
        // packing: the grid's own children are the per-item containers, so the row is read
        // from them; the caps themselves are reached through the panel's logical tree.
        var cells = grid.Children.Cast<Control>().ToArray();
        var firstRow = cells.Where(cell => Math.Abs(cell.Bounds.Y - cells[0].Bounds.Y) < 0.5).ToArray();

        Assert.Equal(6, firstRow.Length);

        var caps = panel.GetLogicalDescendants().OfType<Button>()
            .Where(button => button.DataContext is KeyPickerItem)
            .Take(6)
            .ToArray();

        Assert.All(caps, cap => Assert.True(cap.Bounds.Height >= 32, $"a key cap is only {cap.Bounds.Height}px tall"));
        Assert.All(caps, cap => Assert.True(cap.Bounds.Width >= 80, $"a key cap is only {cap.Bounds.Width}px wide"));
    }

    [AvaloniaFact]
    public void Switching_Categories_Shows_That_Categorys_Keys()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        ExpandKeyList(window);

        // The picker opens on the loaded key's category, and only that category's caps exist.
        Assert.Equal("系统键", viewModel.Editor.ActiveKeyCategory!.Name);
        Assert.Equal(11, EditorPanelOf(window).GetLogicalDescendants().OfType<Button>()
            .Count(button => button.DataContext is KeyPickerItem));
        Assert.True(KeyCategorySegment(window, "系统键").IsChecked);

        // A click on the extension segment moves the grid there - the whole long category,
        // one press away instead of a scroll past every other one.
        Click(KeyCategorySegment(window, "扩展按键"), window);

        Assert.Equal(KeyCategories.ExtensionGroupName, viewModel.Editor.ActiveKeyCategory.Name);
        Assert.True(KeyCategorySegment(window, "扩展按键").IsChecked);
        Assert.False(KeyCategorySegment(window, "系统键").IsChecked);

        var extension = KeyCategories.All.Count(key => !KeyCategories.Grouped.Contains(key));
        Assert.Equal(extension, EditorPanelOf(window).GetLogicalDescendants().OfType<Button>()
            .Count(button => button.DataContext is KeyPickerItem));

        // The selection itself survives the switch - it is the editor's own state, not the
        // grid's - and a cap click writes it: the check moves to the clicked cap alone.
        Assert.Equal(GamepadKey.Escape, viewModel.Editor.ActiveSlot.SelectedKey);

        Click(KeyCapButton(window, GamepadKey.F13), window);
        Assert.Equal(GamepadKey.F13, viewModel.Editor.ActiveSlot.SelectedKey);
        Assert.Contains("selected", KeyCapButton(window, GamepadKey.F13).Classes);
        Assert.DoesNotContain("selected", KeyCapButton(window, GamepadKey.F14).Classes);
    }

    [AvaloniaFact]
    public void The_Category_Navigator_Is_Keyboard_Reachable()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        ExpandKeyList(window);

        var segment = KeyCategorySegment(window, "扩展按键");

        Assert.True(segment.Focusable, "a category segment must be reachable from the keyboard");
        Assert.True(segment.Focus());
        Dispatcher.UIThread.RunJobs();
        Assert.True(segment.IsFocused);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(segment.IsChecked);
        Assert.Equal(KeyCategories.ExtensionGroupName, viewModel.Editor.ActiveKeyCategory!.Name);
        Assert.NotNull(EditorPanelOf(window).GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.DataContext is KeyPickerItem item && item.Key == GamepadKey.F13));
    }

    [AvaloniaFact]
    public void The_Picker_Stays_Compact_And_Reachable_At_The_Minimum_Window_Size()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        // The smallest window the app allows: the card is at its narrowest, so the picker's
        // density claim has to hold here rather than only at the default size.
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        ExpandKeyList(window);

        var panel = EditorPanelOf(window);
        var well = panel.GetLogicalDescendants().OfType<Border>()
            .First(border => border.Classes.Contains("pickerWell"));

        // The navigator fits the card without clipping and without a scrollbar of its own.
        foreach (var segment in panel.GetLogicalDescendants().OfType<RadioButton>()
                     .Where(button => button.Classes.Contains("keyCategorySegment")))
        {
            Assert.True(segment.Bounds.Right <= well.Bounds.Right + 0.5,
                $"the {segment.Content} segment overflows the picker");
            Assert.True(segment.Bounds.Width > 0);
        }

        var grid = panel.GetVisualDescendants().OfType<UniformGrid>().Single();

        Assert.Equal(6, grid.Columns);

        var caps = panel.GetLogicalDescendants().OfType<Button>()
            .Where(button => button.DataContext is KeyPickerItem)
            .ToArray();

        Assert.Equal(11, caps.Length);
        Assert.All(caps, cap => Assert.True(cap.Bounds.Height >= 32, $"a key cap is only {cap.Bounds.Height}px tall"));
        Assert.All(caps, cap => Assert.True(cap.Bounds.Width >= 64, $"a key cap is only {cap.Bounds.Width}px wide"));

        // The body ScrollViewer remains the only scroller, at every size.
        Assert.Empty(well.GetVisualDescendants().OfType<ScrollViewer>());
    }

    [AvaloniaFact]
    public void Reopening_A_Mapping_Shows_The_Selected_Key_In_Its_Category()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        ExpandKeyList(window);
        ChooseKey(window, GamepadKey.F13);

        Assert.Equal(KeyCategories.ExtensionGroupName, viewModel.Editor.ActiveKeyCategory!.Name);

        Click(EditorPanelOf(window).FindControl<Button>("CommitButton")!, window);
        Assert.False(viewModel.IsEditing);

        Click(Model(window).FindControl<Control>("ButtonA")!, window);
        Assert.True(viewModel.IsEditing);

        // The reopened editor opens collapsed, and expanding it lands on the category the
        // committed key lives in - with that key's cap wearing the selection.
        ExpandKeyList(window);

        Assert.Equal(KeyCategories.ExtensionGroupName, viewModel.Editor.ActiveKeyCategory.Name);
        Assert.Contains("selected", KeyCapButton(window, GamepadKey.F13).Classes);
    }

    [AvaloniaFact]
    public void Switching_Gestures_Keeps_The_Selected_Key_In_View()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        ExpandKeyList(window);
        ChooseKey(window, GamepadKey.Y);
        Assert.Equal("面部按键", viewModel.Editor.ActiveKeyCategory!.Name);

        // The double-click's own key is a 系统键 key: the switcher move carries the category
        // with it, so whichever slot is active, its key is the one the grid shows.
        SelectTab(window, viewModel, "GestureDoubleClickTab");
        ChooseKey(window, GamepadKey.Escape);
        Assert.Equal("系统键", viewModel.Editor.ActiveKeyCategory.Name);
        Assert.Contains("selected", KeyCapButton(window, GamepadKey.Escape).Classes);

        SelectTab(window, viewModel, "GestureClickTab");
        Assert.Equal("面部按键", viewModel.Editor.ActiveKeyCategory.Name);
        Assert.Contains("selected", KeyCapButton(window, GamepadKey.Y).Classes);
    }

    [AvaloniaFact]
    public void The_Expanded_Picker_Has_No_Inner_Scroll_Viewer()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        ExpandKeyList(window);

        var expander = EditorPanelOf(window).FindControl<Expander>("KeyPickerExpander")!;

        // The picker owns no scrollbar: the editor body above it is the only scroller, so a
        // selection never snaps the list to the top.
        Assert.Empty(expander.GetVisualDescendants().OfType<ScrollViewer>());
    }

    [AvaloniaFact]
    public void Selecting_A_Key_Realizes_Every_Cap_Of_The_Active_Category()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        ExpandKeyList(window);

        // Every cap of the shown category is realized at once: the picker virtualizes
        // nothing, so a key far down the grid is as reachable as the first one and nothing
        // scrolls under the selection.
        var caps = EditorPanelOf(window).GetLogicalDescendants().OfType<Button>()
            .Where(button => button.DataContext is KeyPickerItem)
            .ToArray();

        Assert.Equal(11, caps.Length);

        ChooseKey(window, GamepadKey.PageDown);

        Assert.Equal(GamepadKey.PageDown, viewModel.Editor.ActiveSlot.SelectedKey);
        Assert.Contains("selected", KeyCapButton(window, GamepadKey.PageDown).Classes);
    }

    [AvaloniaFact]
    public void The_Key_Caps_Are_Keyboard_Focusable_And_Enter_Selects()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        ExpandKeyList(window);

        // F13 lives in the extension category, so the picker is moved there first - the
        // selection leads, the grid follows.
        ChooseKey(window, GamepadKey.F14);

        var cap = KeyCapButton(window, GamepadKey.F13);

        Assert.True(cap.Focusable, "a key cap must be reachable from the keyboard");
        Assert.True(cap.Focus());
        Dispatcher.UIThread.RunJobs();
        Assert.True(cap.IsFocused);

        // The focus ring is visible: the cap's frame takes the accent border while it holds
        // focus, and the border thickness never changes, so focusing shifts no layout.
        var frame = cap.GetVisualDescendants().OfType<Border>()
            .First(border => border.Classes.Contains("keyCapFrame"));

        Assert.Equal(Token(window.ActualThemeVariant, "AccentBrush"), ColorOf(frame.BorderBrush));
        Assert.Equal(new Thickness(1), frame.BorderThickness);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(GamepadKey.F13, viewModel.Editor.ActiveSlot.SelectedKey);
        Assert.Equal(GamepadKey.F13, viewModel.Editor.ActiveSelectedKeyOption!.Key);
        Assert.Contains("selected", cap.Classes);
    }

    [AvaloniaFact]
    public void The_Selected_Key_Cap_Shows_An_Accent_Surface_And_A_Check_Glyph()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        ExpandKeyList(window);
        ChooseKey(window, GamepadKey.F13);

        var selected = KeyCapButton(window, GamepadKey.F13);
        var resting = KeyCapButton(window, GamepadKey.F14);

        // One surface per cap, washed by the selection: the selected cap wears the accent
        // tone and a resting cap keeps the subtle one, so the two never read the same.
        var selectedSurface = selected.GetVisualDescendants().OfType<Border>()
            .First(border => border.Classes.Contains("keyCap"));
        var restingSurface = resting.GetVisualDescendants().OfType<Border>()
            .First(border => border.Classes.Contains("keyCap"));

        Assert.True(selectedSurface.IsVisible, "the selected cap wears the accent surface");
        Assert.True(restingSurface.IsVisible, "a resting cap wears the subtle surface");
        Assert.Equal(Token(window.ActualThemeVariant, "AccentSurfaceBrush"), ColorOf(selectedSurface.Background));
        Assert.Equal(Token(window.ActualThemeVariant, "SubtleBrush"), ColorOf(restingSurface.Background));

        // The check glyph is the non-colour channel: visible on the selected cap alone.
        var selectedCheck = selected.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Classes.Contains("keyCapCheck"));
        var restingCheck = resting.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Classes.Contains("keyCapCheck"));

        Assert.Equal(1d, selectedCheck.Opacity);
        Assert.Equal(0d, restingCheck.Opacity);
    }

    // ---- The click-repeat interval editor --------------------------------------

    [AvaloniaFact]
    public void The_Click_Repeat_Checkbox_Wording_Is_Neutral()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        var box = EditorPanelOf(window).FindControl<CheckBox>("GestureClickRepeatBox")!;

        Assert.Equal("长按重复触发", box.Content);
        Assert.DoesNotContain("0.10", box.Content?.ToString() ?? string.Empty);
    }

    [AvaloniaFact]
    public void Enabling_Click_Repeat_Reveals_The_Interval_Editor_With_The_Default()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        var row = EditorPanelOf(window).FindControl<StackPanel>("ClickRepeatIntervalRow")!;

        Assert.False(row.IsVisible, "the interval waits for repeat to be enabled");

        Click(EditorPanelOf(window).FindControl<CheckBox>("GestureClickRepeatBox")!, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.Editor.ClickRepeatEnabled);
        Assert.True(row.IsVisible);

        var interval = EditorPanelOf(window).FindControl<NumericUpDown>("ClickRepeatIntervalBox")!;

        Assert.NotNull(interval.Value);
        Assert.Equal(0.10, (double)interval.Value!, precision: 3);
        Assert.Equal(ButtonGesture.DefaultClickRepeatInterval, viewModel.Editor.ClickRepeatIntervalSeconds, precision: 3);
    }

    [AvaloniaFact]
    public void The_Interval_Editor_Writes_A_Snapped_Value_Through_Commit()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        Click(EditorPanelOf(window).FindControl<CheckBox>("GestureClickRepeatBox")!, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var interval = EditorPanelOf(window).FindControl<NumericUpDown>("ClickRepeatIntervalBox")!;
        interval.Value = 0.15m;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0.15f, viewModel.Editor.ClickRepeatInterval);

        Click(EditorPanelOf(window).FindControl<Button>("CommitButton")!, window);
        Assert.False(viewModel.IsEditing);

        var committed = viewModel.ActiveProfile!.Mappings
            .First(mapping => mapping.Source is ButtonBinding { Button: GamepadButton.A });

        Assert.Equal(0.15f, Assert.IsType<ButtonBinding>(committed.Source).Gesture!.ClickRepeatInterval);
    }

    [AvaloniaFact]
    public void The_Interval_Editor_Is_Hidden_While_Repeat_Is_Off_Or_On_Another_Tab()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        var row = EditorPanelOf(window).FindControl<StackPanel>("ClickRepeatIntervalRow")!;
        var repeatBox = EditorPanelOf(window).FindControl<CheckBox>("GestureClickRepeatBox")!;

        Click(repeatBox, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.True(row.IsVisible);

        // The interval belongs to the click tab: another gesture's tab hides it.
        SelectTab(window, viewModel, "GestureDoubleClickTab");
        Assert.False(row.IsVisible);

        SelectTab(window, viewModel, "GestureClickTab");
        Assert.True(row.IsVisible);

        // And switching repeat off takes it away again.
        Click(repeatBox, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.Editor.ClickRepeatEnabled);
        Assert.False(row.IsVisible);
    }

    // ---- 复杂快捷键: the long press runs a hand-written program ---------------

    [AvaloniaFact]
    public void The_Target_Kind_Combo_Reads_复杂快捷键()
    {
        // The picker's Chinese label comes from the shared converter, so the kind this
        // wave adds reads as Chinese everywhere it is shown.
        var converter = new EnumDisplayConverter();

        Assert.Equal(
            "复杂快捷键",
            converter.Convert(MappingTargetKind.ComplexShortcut, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(
            "特殊键",
            converter.Convert(MappingTargetKind.SpecialKey, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [AvaloniaFact]
    public void The_Complex_Shortcut_Card_Opens_On_The_Gesture_That_Carries_The_Program()
    {
        // Any of the three positions can carry a program - the position is the program's
        // trigger - so the card opens wherever the kind is chosen and leaves with it.
        var (window, viewModel) = OpenEditorOn("LeftBumper");

        var panel = EditorPanelOf(window);
        var combo = panel.FindControl<ComboBox>("TargetKindCombo")!;
        var card = panel.FindControl<StackPanel>("ComplexShortcutRow")!;

        Assert.False(card.IsVisible);

        foreach (var tab in new[] { "GestureClickTab", "GestureDoubleClickTab", "GestureLongPressTab" })
        {
            SelectTab(window, viewModel, tab);

            Assert.Contains(MappingTargetKind.ComplexShortcut, combo.Items.Cast<MappingTargetKind>());

            ChooseTargetKind(window, MappingTargetKind.ComplexShortcut);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(MappingTargetKind.ComplexShortcut, combo.SelectedItem);
            Assert.True(card.IsVisible);

            ChooseTargetKind(window, MappingTargetKind.KeyPress);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.False(card.IsVisible);
        }

        // The example button fills a working program.
        SelectTab(window, viewModel, "GestureLongPressTab");
        ChooseTargetKind(window, MappingTargetKind.ComplexShortcut);

        var scriptBox = panel.FindControl<TextBox>("ComplexShortcutScriptBox")!;
        scriptBox.Text = "tap F5";
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Click(panel.FindControl<Button>("ComplexShortcutExampleButton")!, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("hold Alt\nevery 1s tap Tab", scriptBox.Text);

        // A stick has no gestures, so nothing can start a program there and the kind is off
        // its list.
        viewModel.Editor.SourceIsStick = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(MappingTargetKind.ComplexShortcut, combo.Items.Cast<MappingTargetKind>());
    }

    [AvaloniaFact]
    public void A_Valid_Script_Shows_Its_Plain_Language_Summary_And_Commits()
    {
        var (window, viewModel) = OpenEditorOn("LeftBumper");
        var list = window.FindControl<ItemsControl>("MappingsList")!;
        var before = list.ItemCount;

        SelectTab(window, viewModel, "GestureLongPressTab");
        ChooseTargetKind(window, MappingTargetKind.ComplexShortcut);

        var panel = EditorPanelOf(window);
        panel.FindControl<TextBox>("ComplexShortcutScriptBox")!.Text = "hold Alt\nevery 1s tap Tab";
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var summary = panel.FindControl<TextBlock>("ComplexShortcutSummaryText")!;
        var error = panel.FindControl<TextBlock>("ComplexShortcutErrorText")!;

        Assert.True(summary.IsVisible);
        Assert.False(error.IsVisible);
        Assert.Equal("持续按住 Alt；每 1 秒点按 Tab", summary.Text);

        Click(panel.FindControl<Button>("CommitButton")!, window);

        Assert.False(viewModel.IsEditing);
        Assert.Equal(before + 1, list.ItemCount);

        var committed = viewModel.ActiveProfile!.Mappings.First(
            mapping => mapping.Source is ButtonBinding { Button: GamepadButton.LeftBumper });

        // The program is the long press's own action; the click keeps its own target.
        var gesture = Assert.IsType<ButtonGesture>(Assert.IsType<ButtonBinding>(committed.Source).Gesture);
        Assert.Equal(
            new ComplexShortcutTarget { Script = "hold Alt\nevery 1s tap Tab" },
            gesture!.LongPressAction);
        Assert.Equal(new KeyPressTarget(GamepadKey.Enter), committed.Target);
    }

    [AvaloniaFact]
    public void An_Invalid_Script_Shows_Its_Chinese_Error_And_Blocks_The_Commit()
    {
        var (window, viewModel) = OpenEditorOn("LeftBumper");
        var list = window.FindControl<ItemsControl>("MappingsList")!;
        var before = list.ItemCount;

        SelectTab(window, viewModel, "GestureLongPressTab");
        ChooseTargetKind(window, MappingTargetKind.ComplexShortcut);

        var panel = EditorPanelOf(window);
        panel.FindControl<TextBox>("ComplexShortcutScriptBox")!.Text = "hold Alt\nfrobnicate";
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var summary = panel.FindControl<TextBlock>("ComplexShortcutSummaryText")!;
        var error = panel.FindControl<TextBlock>("ComplexShortcutErrorText")!;

        Assert.True(error.IsVisible);
        Assert.False(summary.IsVisible);
        Assert.Contains("第 2 行", error.Text);

        Click(panel.FindControl<Button>("CommitButton")!, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // The commit is refused: the editor stays open on the offending script, and
        // nothing reaches the profile, the list or the running engine.
        Assert.True(viewModel.IsEditing);
        Assert.Equal(before, list.ItemCount);
        Assert.DoesNotContain(
            viewModel.ActiveProfile!.Mappings,
            mapping => mapping.Target is ComplexShortcutTarget);
    }

    [AvaloniaFact]
    public void The_Script_Box_Is_Keyboard_Reachable_And_Capture_Keeps_Working()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        SelectTab(window, viewModel, "GestureLongPressTab");
        ChooseTargetKind(window, MappingTargetKind.ComplexShortcut);

        var scriptBox = EditorPanelOf(window).FindControl<TextBox>("ComplexShortcutScriptBox")!;

        window.Focus();
        Dispatcher.UIThread.RunJobs();
        scriptBox.Focus();
        Dispatcher.UIThread.RunJobs();

        // A plain focusable control, so keyboard and gamepad navigation reach it exactly
        // as they reach every other field in the card.
        Assert.True(scriptBox.Focusable);
        Assert.True(scriptBox.IsFocused);

        // Capture still works with the script box focused: the captured key becomes the
        // slot's target, and the hand-written program is left waiting on the slot.
        Click(EditorPanelOf(window).FindControl<Button>("CaptureKeyButton")!, window);
        Assert.True(viewModel.Editor.IsCapturingKey);

        window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        window.KeyRelease(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);

        Assert.False(viewModel.Editor.IsCapturingKey);
        Assert.Equal(GamepadKey.F5, viewModel.Editor.LongPressSlot.SelectedKey);
        Assert.Equal("hold Alt\nevery 1s tap Tab", viewModel.Editor.LongPressSlot.Script);
    }

    // ---- 输入文本 / 屏幕键盘 / 执行策略 / 执行顺序 / 不支持的映射 ----------------

    private static TextKeyboardKey FocusedTextKey(Window window)
        => Assert.IsType<TextKeyboardKey>(
            EditorPanelOf(window).FindControl<ItemsControl>("TextKeyboardGrid")!
                .GetLogicalDescendants().OfType<Button>()
                .First(button => button.Classes.Contains("focused")).DataContext);

    private static Button TextKeyButton(Window window, string label)
        => EditorPanelOf(window).FindControl<ItemsControl>("TextKeyboardGrid")!
            .GetLogicalDescendants().OfType<Button>()
            .First(button => (button.DataContext as TextKeyboardKey)?.Label == label);

    private static RadioButton[] TextKeyboardTabs(Window window)
        => EditorPanelOf(window).FindControl<ItemsControl>("TextKeyboardPageNav")!
            .GetLogicalDescendants().OfType<RadioButton>().ToArray();

    /// <summary>The A button on a keyboard: a press and a release, the pair a cap answers.</summary>
    private static void PressSpace(Window window)
    {
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// One host tick with the pad holding <paramref name="state"/>, dispatched through the
    /// window's own Sampled wiring - the path a real sample takes, so the editor's capture and
    /// the window's navigation each see it exactly once. A press is an edge, so a held button
    /// needs a neutral step between two presses of it.
    /// </summary>
    private static void PadStep(EngineHost host, FakeGamepadProvider provider, Window window, GamepadState state)
    {
        provider.SetState(DeviceId, state);
        host.TickOnce(SampleHelper.Tick);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Opens the editor on one element with the keyboard offered, ready for the pad.</summary>
    private static (MainWindow Window, MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider)
        OpenTextKeyboardEditor()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.LeftBumper));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.IsEditing);

        ChooseTargetKind(window, MappingTargetKind.Text);
        Assert.True(viewModel.Editor.ShowTextKeyboard);
        Assert.True(EditorPanelOf(window).FindControl<StackPanel>("TextKeyboardRow")!.IsVisible);

        return (window, viewModel, host, provider);
    }

    private static MappingRowViewModel RowOf(MainWindowViewModel viewModel, GamepadButton button)
        => viewModel.Mappings.First(row => row.Entry.Source is ButtonBinding binding && binding.Button == button);

    [AvaloniaFact]
    public void The_Text_Target_Opens_Its_Box_Counter_And_Keyboard()
    {
        var (window, viewModel) = OpenEditorOn("LeftBumper");
        var panel = EditorPanelOf(window);

        var textRow = panel.FindControl<StackPanel>("TextRow")!;
        var textBox = panel.FindControl<TextBox>("ActiveTextBox")!;
        var counter = panel.FindControl<TextBlock>("ActiveTextLengthText")!;
        var keyboard = panel.FindControl<StackPanel>("TextKeyboardRow")!;

        // Nothing about another kind needs a string, so neither surface is offered yet.
        Assert.False(textRow.IsVisible);
        Assert.False(keyboard.IsVisible);

        ChooseTargetKind(window, MappingTargetKind.Text);

        Assert.True(textRow.IsVisible);
        Assert.True(keyboard.IsVisible);

        // The bound is the Core's own, so a paste that runs long is trimmed rather than
        // refused - and the counter names it instead of leaving the trim to be discovered.
        Assert.Equal(GestureSlotEditor.TextMaxLength, textBox.MaxLength);
        Assert.Contains("已输入 0 / 256", counter.Text);

        // The box is the physical-keyboard way in; the keyboard below is the gamepad way
        // in. Both write the same draft, and the counter follows it.
        textBox.Text = "open folder";
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("open folder", viewModel.Editor.ActiveText);
        Assert.Equal(11, viewModel.Editor.ActiveTextLength);
        Assert.Contains("已输入 11 / 256", counter.Text);
    }

    [AvaloniaFact]
    public void The_On_Screen_Keyboard_Types_With_The_Gamepad_Alone()
    {
        var (window, viewModel) = OpenEditorOn("LeftBumper");
        var panel = EditorPanelOf(window);

        ChooseTargetKind(window, MappingTargetKind.Text);

        // The cursor starts on the first key of the first page, and it is the cap that
        // reads as focused: the cursor is view-model state, not real focus, so the panel
        // marks it the way it marks a selected key cap.
        Assert.Equal("a", FocusedTextKey(window).Label);
        Assert.Contains("focused", TextKeyButton(window, "a").Classes);

        // The keyboard owns the arrow keys while it holds focus, so the cursor is driven
        // from a cap the way a gamepad user would arrive at it.
        window.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(TextKeyButton(window, "a").Focus());
        Dispatcher.UIThread.RunJobs();

        // The D-pad moves the cursor, and the cap it lands on takes real focus with it, so
        // the focus ring and the cursor highlight never disagree about where the press
        // would land.
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("b", FocusedTextKey(window).Label);
        Assert.True(TextKeyButton(window, "b").IsFocused);

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("k", FocusedTextKey(window).Label);

        // A presses the key the cursor is on. Space rather than Enter: Enter is the
        // editor's own commit pair, which the window owns. A button answers Space on the
        // release, exactly as it answers a pointer press.
        PressSpace(window);
        Assert.Equal("k", viewModel.Editor.ActiveText);

        PressSpace(window);
        Assert.Equal("kk", viewModel.Editor.ActiveText);

        // The action row ends every page: the cursor reaches it by moving to the first
        // column and down to the last row, where 退格 deletes the character before it.
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("退格", FocusedTextKey(window).Label);

        PressSpace(window);
        Assert.Equal("k", viewModel.Editor.ActiveText);
    }

    [AvaloniaFact]
    public void The_Gamepad_Alone_Moves_The_Cursor_Presses_A_Key_And_Turns_Pages()
    {
        var (window, viewModel, host, provider) = OpenTextKeyboardEditor();
        var panel = EditorPanelOf(window);

        // A shown, active mapping tab with navigation on, so the pad answers the editor.
        Assert.True(viewModel.IsPhysicalInputInScope);
        Assert.True(viewModel.GamepadNavigationEnabled);

        // The cursor starts on the first key of the first page, and the cap it sits on is the
        // one that reads as focused: the cursor is view-model state, not real focus.
        Assert.Equal("a", FocusedTextKey(window).Label);

        // The D-pad moves the cursor, and the cap it lands on wears the highlight.
        PadStep(host, provider, window, GamepadState.Neutral);
        PadStep(host, provider, window, GamepadState.Create(GamepadButtonMask.DPadRight, 0, 0, 0, 0));
        Assert.Equal("b", FocusedTextKey(window).Label);
        Assert.Contains("focused", TextKeyButton(window, "b").Classes);

        PadStep(host, provider, window, GamepadState.Neutral);
        PadStep(host, provider, window, GamepadState.Create(GamepadButtonMask.DPadDown, 0, 0, 0, 0));
        Assert.Equal("k", FocusedTextKey(window).Label);

        // A presses the key the cursor is on, and the draft - and the box that reads it -
        // follow it.
        PadStep(host, provider, window, GamepadState.Neutral);
        PadStep(host, provider, window, GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));
        Assert.Equal("k", viewModel.Editor.ActiveText);
        Assert.Equal("k", panel.FindControl<TextBox>("ActiveTextBox")!.Text);

        // The press was spent on the keyboard, so it never also navigated: the editor is
        // still on the element it was opened for.
        Assert.Equal(GamepadButton.LeftBumper, viewModel.Editor.SelectedButton);

        // The bumpers turn the page. 小写 is the first page, so a left bumper wraps around to
        // the last one, and the page it shows opens on its own first key.
        PadStep(host, provider, window, GamepadState.Neutral);
        PadStep(host, provider, window, GamepadState.Create(GamepadButtonMask.LeftBumper, 0, 0, 0, 0));
        Assert.Equal("常用", viewModel.Editor.TextKeyboard.ActivePage.Name);
        Assert.Equal("https://", FocusedTextKey(window).Label);
        Assert.Equal(
            ["常用"],
            TextKeyboardTabs(window).Where(tab => tab.IsChecked == true).Select(tab => tab.Content as string));

        PadStep(host, provider, window, GamepadState.Neutral);
        PadStep(host, provider, window, GamepadState.Create(GamepadButtonMask.RightBumper, 0, 0, 0, 0));
        Assert.Equal("小写", viewModel.Editor.TextKeyboard.ActivePage.Name);
        Assert.Equal(
            ["小写"],
            TextKeyboardTabs(window).Where(tab => tab.IsChecked == true).Select(tab => tab.Content as string));

        // The fragment the wrapped-to page offered types whole, so the pad alone has entered
        // a URL fragment and a letter.
        PadStep(host, provider, window, GamepadState.Neutral);
        PadStep(host, provider, window, GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));
        Assert.Equal("ka", viewModel.Editor.ActiveText);
    }

    [AvaloniaFact]
    public void The_Keyboard_Answers_The_Pad_Only_While_The_Window_Is_In_Scope()
    {
        var (window, viewModel, host, provider) = OpenTextKeyboardEditor();

        Assert.True(viewModel.Editor.TextKeyboardGamepadInScope);

        // Leaving the mapping tab releases the pad, and coming back restores it - the same
        // lease every other editor press answers to.
        window.FindControl<TabControl>("MainTabs")!.SelectedIndex = 1;
        Assert.False(viewModel.Editor.TextKeyboardGamepadInScope);

        window.FindControl<TabControl>("MainTabs")!.SelectedIndex = 0;
        Assert.True(viewModel.Editor.TextKeyboardGamepadInScope);

        // Closing the window - the resident-daemon mode, where the tray owns the pad - leaves
        // the keyboard deaf to it even though the editor and its draft outlive the window.
        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.Editor.TextKeyboardGamepadInScope);

        PadStep(host, provider, window, GamepadState.Neutral);
        PadStep(host, provider, window, GamepadState.Create(GamepadButtonMask.DPadRight, 0, 0, 0, 0));
        Assert.Equal("a", viewModel.Editor.TextKeyboard.FocusedKey!.Label);
    }

    [AvaloniaFact]
    public void The_Keyboard_Page_Tabs_Switch_The_Grid_And_Reset_The_Cursor()
    {
        var (window, viewModel) = OpenEditorOn("LeftBumper");
        var panel = EditorPanelOf(window);

        ChooseTargetKind(window, MappingTargetKind.Text);

        var tabs = TextKeyboardTabs(window);

        Assert.Equal(
            ["小写", "大写", "数字", "符号", "常用"],
            tabs.Select(tab => tab.Content as string));
        Assert.Equal(
            ["小写"],
            tabs.Where(tab => tab.IsChecked == true).Select(tab => tab.Content as string));

        // Every page ends with the same action row, so a tab never has to be hunted for.
        Assert.Contains(TextKeyButton(window, "完成"), panel.GetLogicalDescendants().OfType<Button>());

        Click(tabs.First(tab => tab.Content as string == "常用"), window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("常用", viewModel.Editor.TextKeyboard.ActivePage.Name);

        // The tab that was pressed reads as checked and the rest do not, and the page's
        // own keys are on screen with the cursor back on the first one.
        Assert.Equal(
            ["常用"],
            tabs.Where(tab => tab.IsChecked == true).Select(tab => tab.Content as string));
        Assert.Equal("https://", FocusedTextKey(window).Label);

        // A press on a cap types that cap's own fragment, and leaves the cursor on it.
        Click(TextKeyButton(window, "www."), window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("www.", viewModel.Editor.ActiveText);
        Assert.Equal("www.", FocusedTextKey(window).Label);
    }

    [AvaloniaFact]
    public void The_Strategy_Pickers_Read_The_Active_Slot()
    {
        var (window, viewModel) = OpenEditorOn("LeftBumper");
        var panel = EditorPanelOf(window);

        var card = panel.FindControl<StackPanel>("ActionStrategyRow")!;
        var trigger = panel.FindControl<ComboBox>("ActionTriggerCombo")!;
        var execution = panel.FindControl<ComboBox>("ActionExecutionCombo")!;
        var repeat = panel.FindControl<ComboBox>("ActionRepeatCombo")!;

        // The card follows the active slot, so it waits for a program on that slot.
        Assert.False(card.IsVisible);

        ChooseTargetKind(window, MappingTargetKind.ComplexShortcut);
        Assert.True(card.IsVisible);

        // The click position answers two triggers, so its row is a picker.
        Assert.True(trigger.IsEnabled);
        Assert.Equal(2, trigger.Items.Cast<MappingEditorViewModel.ActionTriggerOption>().Count());
        Assert.Equal(3, execution.Items.Cast<MappingEditorViewModel.ActionExecutionOption>().Count());
        Assert.Equal(
            "该程序挂在单击上：按下按键时开始执行。",
            panel.FindControl<TextBlock>("ActionStrategyHint")!.Text);

        // 排队 needs a run that ends on its own, so it leaves the list beside 按住期间执行
        // rather than being offered and then refused.
        Assert.DoesNotContain(
            repeat.Items.Cast<MappingEditorViewModel.ActionRepeatOption>(),
            option => option.Value == ActionRepeat.Queue);

        execution.SelectedItem = execution.Items.Cast<MappingEditorViewModel.ActionExecutionOption>()
            .First(option => option.Value == ActionExecution.Once);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ActionExecution.Once, viewModel.Editor.ActiveActionExecution);
        Assert.Contains(
            repeat.Items.Cast<MappingEditorViewModel.ActionRepeatOption>(),
            option => option.Value == ActionRepeat.Queue);

        // The double-click and the long-press each answer exactly one trigger, so their
        // row is a readout rather than a picker - and the hint names the position instead.
        SelectTab(window, viewModel, "GestureDoubleClickTab");
        Assert.False(card.IsVisible);

        ChooseTargetKind(window, MappingTargetKind.ComplexShortcut);
        Assert.True(card.IsVisible);

        Assert.False(trigger.IsEnabled);
        Assert.Single(trigger.Items.Cast<MappingEditorViewModel.ActionTriggerOption>());
        Assert.Equal(
            "该程序挂在双击上：第二次按下时开始执行。",
            panel.FindControl<TextBlock>("ActionStrategyHint")!.Text);
    }

    [AvaloniaFact]
    public void The_Preview_Lists_The_Press_And_Release_Order()
    {
        var (window, viewModel) = OpenEditorOn("LeftBumper");
        var panel = EditorPanelOf(window);

        ChooseTargetKind(window, MappingTargetKind.ComplexShortcut);
        panel.FindControl<TextBox>("ComplexShortcutScriptBox")!.Text = "hold Alt\ntap Tab";
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // The compiled program, one line per output in the order the engine emits it, with
        // the held key released at the end.
        Assert.Equal(
            ["按下 Alt", "按下 Tab", "松开 Tab", "松开 Alt"],
            panel.FindControl<ItemsControl>("ActionPreviewList")!.Items.Cast<string>());

        // A script that does not parse leaves the preview empty: the parse error in the
        // card below is the single place that names the failure.
        panel.FindControl<TextBox>("ComplexShortcutScriptBox")!.Text = "hold Alt\nfrobnicate";
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(panel.FindControl<ItemsControl>("ActionPreviewList")!.Items.Cast<string>());
        Assert.True(panel.FindControl<TextBlock>("ComplexShortcutErrorText")!.IsVisible);
    }

    [AvaloniaFact]
    public void An_Unsupported_Target_Shows_Its_Banner_And_Keeps_The_Draft()
    {
        var (window, viewModel) = OpenEditorOn("LeftBumper");
        var panel = EditorPanelOf(window);

        var banner = panel.FindControl<TextBlock>("UnsupportedTargetBanner")!;
        Assert.False(banner.IsVisible);

        // A profile written by a newer build names a target this one cannot read.
        viewModel.Editor.LoadFrom(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.LeftBumper),
            Target = new UnknownTarget("wand", """{"$type":"wand","Wave":3}"""),
        });
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.Editor.ActiveTargetUnsupported);
        Assert.True(banner.IsVisible);
        Assert.Contains("原样保留", banner.Text);
        Assert.Contains("wand", viewModel.Editor.ActiveTargetUnsupportedDescription!);

        // The banner leads the target area, above the controls it explains.
        var targetLabel = panel.GetLogicalDescendants().OfType<TextBlock>()
            .First(text => text.Text == "目标动作");
        Assert.True(
            banner.TranslatePoint(new Point(0, 0), window)!.Value.Y
            < targetLabel.TranslatePoint(new Point(0, 0), window)!.Value.Y);

        // The keyboard is not offered for a target no page can type, and the text box is
        // not offered either - the banner is the whole answer here.
        Assert.False(panel.FindControl<StackPanel>("TextKeyboardRow")!.IsVisible);
    }

    [AvaloniaFact]
    public void A_Media_Key_And_A_Text_Target_Read_As_Chinese_In_The_Row()
    {
        var (window, viewModel) = OpenEditorOn("LeftBumper");

        // A media key: the row names what the key does, not the enum it came from.
        ChooseKey(window, GamepadKey.BrowserRefresh);
        Click(EditorPanelOf(window).FindControl<Button>("CommitButton")!, window);
        Assert.False(viewModel.IsEditing);

        Assert.Equal("刷新", RowOf(viewModel, GamepadButton.LeftBumper).TargetText);

        // A text target: the row names the string it types, so the press is legible
        // without opening the editor.
        Click(Model(window).FindControl<Control>("LeftBumper")!, window);
        Assert.True(viewModel.IsEditing);

        ChooseTargetKind(window, MappingTargetKind.Text);
        EditorPanelOf(window).FindControl<TextBox>("ActiveTextBox")!.Text = "hello";
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Click(EditorPanelOf(window).FindControl<Button>("CommitButton")!, window);
        Assert.False(viewModel.IsEditing);

        Assert.Equal("文本输出：hello", RowOf(viewModel, GamepadButton.LeftBumper).TargetText);
    }
}
