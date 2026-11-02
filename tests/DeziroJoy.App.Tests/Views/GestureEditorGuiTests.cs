using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
        var list = EditorPanelOf(window).FindControl<ListBox>("TargetKeyList")!;
        list.SelectedItem = EditorPanelOf(window).DataContext is MappingEditorViewModel editor
            ? editor.KeyOptions.First(option => !option.IsHeader && option.Key == key)
            : throw new InvalidOperationException("the panel must be bound to the editor");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void ExpandKeyList(Window window)
    {
        var expander = EditorPanelOf(window).FindControl<Expander>("KeyPickerExpander")!;
        expander.IsExpanded = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
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

        // There is exactly one target-kind combo and one key list: the per-gesture action
        // dropdowns the old surface carried are gone, not merely hidden.
        Assert.Single(panel.GetLogicalDescendants().OfType<ComboBox>(), c => c.Name == "TargetKindCombo");
        Assert.Single(panel.GetLogicalDescendants().OfType<ListBox>(), c => c.Name == "TargetKeyList");
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
        var list = EditorPanelOf(window).FindControl<ListBox>("TargetKeyList")!;

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

        var headers = list.Items.OfType<KeyPickerItem>().Where(item => item.IsHeader).Select(item => item.Label).ToArray();
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

        var list = EditorPanelOf(window).FindControl<ListBox>("TargetKeyList")!;
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
        Assert.NotNull(list.SelectedItem);
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
}
