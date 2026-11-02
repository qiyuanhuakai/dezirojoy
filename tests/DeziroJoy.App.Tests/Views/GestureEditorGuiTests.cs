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

        // The expanded grid shows one section per category, in the picker's own order.
        var headers = EditorPanelOf(window).GetLogicalDescendants().OfType<TextBlock>()
            .Where(text => text.Classes.Contains("pickerHeaderLabel"))
            .Select(text => text.Text ?? string.Empty)
            .ToArray();

        Assert.Equal(
            ["面部按键", "肩键与扳机", "摇杆", "十字键", "系统键", "扩展按键"],
            headers);
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

    // ---- The expanded picker is a full-width four-column grid ----------------

    [AvaloniaFact]
    public void The_Expanded_Picker_Is_A_Four_Column_Grid_Across_The_Full_Card_Width()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        ExpandKeyList(window);

        var panel = EditorPanelOf(window);
        var grids = panel.GetVisualDescendants().OfType<UniformGrid>().ToArray();

        Assert.NotEmpty(grids);
        Assert.All(grids, grid => Assert.Equal(4, grid.Columns));

        // The grid takes the editor card's width: not a narrow centered popup, and not a
        // column beside the rest of the editor.
        var grid = grids[0];

        Assert.True(grid.Bounds.Width > 400, $"the grid is only {grid.Bounds.Width}px wide");
        Assert.True(
            grid.Bounds.Width >= panel.Bounds.Width * 0.9,
            $"the grid ({grid.Bounds.Width}px) must fill the card ({panel.Bounds.Width}px)");

        // Four caps share the first row, each a comfortable hit target. The grid's own
        // children are the per-item containers, so the row is read from them; the caps
        // themselves are reached through the panel's logical tree.
        var cells = grid.Children.Cast<Control>().ToArray();
        var firstRow = cells.Where(cell => Math.Abs(cell.Bounds.Y - cells[0].Bounds.Y) < 0.5).ToArray();

        Assert.Equal(4, firstRow.Length);

        var caps = panel.GetLogicalDescendants().OfType<Button>()
            .Where(button => button.DataContext is KeyPickerItem)
            .Take(4)
            .ToArray();

        Assert.All(caps, cap => Assert.True(cap.Bounds.Height >= 32, $"a key cap is only {cap.Bounds.Height}px tall"));
        Assert.All(caps, cap => Assert.True(cap.Bounds.Width >= 100, $"a key cap is only {cap.Bounds.Width}px wide"));
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
    public void Selecting_A_Key_Realizes_Every_Cap_Without_Scrolling_The_Picker()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        ExpandKeyList(window);

        // Every cap is realized at once: the picker virtualizes nothing, so a key far down
        // the list is as reachable as the first one and nothing scrolls under the selection.
        var caps = EditorPanelOf(window).GetLogicalDescendants().OfType<Button>()
            .Where(button => button.DataContext is KeyPickerItem)
            .ToArray();

        Assert.Equal(Enum.GetValues<GamepadKey>().Length, caps.Length);

        ChooseKey(window, GamepadKey.F13);

        Assert.Equal(GamepadKey.F13, viewModel.Editor.ActiveSlot.SelectedKey);
        Assert.Contains("selected", KeyCapButton(window, GamepadKey.F13).Classes);
    }

    [AvaloniaFact]
    public void The_Key_Caps_Are_Keyboard_Focusable_And_Enter_Selects()
    {
        var (window, viewModel) = OpenEditorOn("ButtonA");

        ExpandKeyList(window);

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
}
