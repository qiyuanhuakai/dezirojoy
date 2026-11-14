using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The rules panel as the user meets it. The one thing the panel exists to do is explain why the
/// active profile is the active one, so that line is pinned here rather than left to the view
/// model's own tests: the reason has to reach the screen, the locked state has to be impossible
/// to miss, and a rule that can never match has to say so in the table instead of sitting there
/// looking like it does something.
/// </summary>
public sealed class ForegroundRulesPanelGuiTests : IDisposable
{
    private readonly string _directory;

    public ForegroundRulesPanelGuiTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-foreground-panel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private static ForegroundWatcher CreateWatcher(ForegroundContext context, ForegroundSettings settings)
    {
        var source = new StubForegroundSource(context);
        return new ForegroundWatcher(source, new StubForegroundTarget(), settings);
    }

    private static (Window Window, ForegroundRulesPanel Panel, ForegroundRulesViewModel ViewModel) Show(
        ForegroundRulesViewModel viewModel)
    {
        var panel = new ForegroundRulesPanel { DataContext = viewModel };
        // Tall enough to lay the whole surface out without scrolling: a control below the fold
        // is one a headless click cannot reach.
        var window = new Window
        {
            Width = 720,
            Height = 2400,
            Content = panel,
        };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        return (window, panel, viewModel);
    }

    private (Window Window, ForegroundRulesPanel Panel, ForegroundRulesViewModel ViewModel) Show(
        ForegroundContext context,
        ForegroundSettings? stored = null)
    {
        var store = new ForegroundRuleStore(Path.Combine(_directory, ForegroundRuleStore.FileName));
        if (stored is not null)
        {
            store.Save(stored);
        }

        var watcher = CreateWatcher(context, store.Load());
        return Show(new ForegroundRulesViewModel(watcher, store));
    }

    [AvaloniaFact]
    public void The_Panel_Explains_Why_The_Active_Profile_Is_The_Active_One()
    {
        var (window, panel, _) = Show(
            new ForegroundContext("Chrome.exe", false),
            new ForegroundSettings
            {
                DefaultSlot = 2,
                Rules = [new ForegroundRule { ProcessName = "chrome", Priority = 5, Slot = 3 }],
            });

        var reason = panel.FindControl<TextBlock>("ActiveReasonText")!;
        var profile = panel.FindControl<TextBlock>("ActiveProfileText")!;
        var process = panel.FindControl<TextBlock>("ForegroundProcessText")!;

        Assert.Equal("Chrome.exe", process.Text);
        Assert.Equal("预设 3", profile.Text);
        Assert.Contains("预设 3", reason.Text);
        Assert.Contains("chrome", reason.Text);
    }

    [AvaloniaFact]
    public void The_Locked_State_Is_On_Screen_And_Names_What_It_Froze()
    {
        var (window, panel, viewModel) = Show(new ForegroundContext("explorer", false));

        var lockState = panel.FindControl<TextBlock>("LockStateText")!;
        Assert.False(lockState.IsEffectivelyVisible);

        var lockButton = panel.FindControl<Button>("ToggleLockButton")!;
        Click(lockButton, window);

        Assert.True(viewModel.IsLocked);
        Assert.True(lockState.IsEffectivelyVisible);
        Assert.Contains("已锁定", lockState.Text);
        Assert.Contains("解锁", (string?)lockButton.Content);

        Click(lockButton, window);

        Assert.False(viewModel.IsLocked);
        Assert.False(lockState.IsEffectivelyVisible);
        Assert.Contains("锁定", (string?)lockButton.Content);
    }

    [AvaloniaFact]
    public void A_Rule_Without_A_Program_Name_Says_So_In_The_Table()
    {
        var (window, panel, viewModel) = Show(new ForegroundContext("explorer", false));

        viewModel.AddRuleCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var warning = panel.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "程序名不能为空；该规则永远不会命中。");

        Assert.True(warning.IsEffectivelyVisible);

        // And the save stays disarmed rather than writing a rule that can never match.
        Assert.False(panel.FindControl<Button>("SaveRulesButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public void A_Rule_Row_Can_Be_Added_And_Deleted_From_The_Panel()
    {
        var (window, panel, viewModel) = Show(new ForegroundContext("explorer", false));

        Click(panel.FindControl<Button>("AddRuleButton")!, window);

        var row = panel.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "RuleProcessBox");
        row.Text = "game";
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(viewModel.Rules);
        Assert.Equal("game", viewModel.Rules[0].ProcessName);

        Click(panel.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RuleDeleteButton"), window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(viewModel.Rules);
    }

    [AvaloniaFact]
    public void The_Slot_Editors_Write_The_Numbers_They_Show()
    {
        // The rule's slot and the desktop default both travel through a NumericUpDown, whose
        // Value is a decimal: a number the panel shows but never writes back would be a control
        // that looks like it does something.
        var (window, panel, viewModel) = Show(new ForegroundContext("explorer", false));

        viewModel.AddRuleCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var ruleSlot = panel.GetVisualDescendants().OfType<NumericUpDown>().Single(box => box.Name == "RuleSlotBox");
        ruleSlot.Value = 4;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(4, viewModel.Rules[0].Slot);

        var defaultSlot = panel.FindControl<NumericUpDown>("DefaultSlotBox")!;
        defaultSlot.Value = 3;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, viewModel.DefaultSlot);
        Assert.True(viewModel.HasUnsavedChanges);
    }

    [AvaloniaFact]
    public void The_Panel_Says_What_The_Platform_Can_See()
    {
        var (_, panel, _) = Show(new ForegroundContext("explorer", false));

        var note = panel.FindControl<TextBlock>("SourceNoteText")!;

        // A stub source is not a platform source, so the note is the honest one.
        Assert.Contains("不支持", note.Text);
    }

    [AvaloniaFact]
    public void A_Rule_Can_Pick_Its_Program_From_The_Open_Windows()
    {
        var store = new ForegroundRuleStore(Path.Combine(_directory, ForegroundRuleStore.FileName));
        var source = new StubForegroundSource(
            new ForegroundContext("explorer", false),
            [new ForegroundWindow("game", "游戏窗口"), new ForegroundWindow("chrome", null)]);
        var watcher = new ForegroundWatcher(source, new StubForegroundTarget(), store.Load());
        var (window, panel, viewModel) = Show(new ForegroundRulesViewModel(watcher, store));

        viewModel.AddRuleCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var picker = panel.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "RuleWindowPicker");

        // The picker offers what the platform enumerated - one entry per open window, titled
        // where the window has a title - and picking one fills the rule's program name.
        Assert.Equal(2, picker.Items.Cast<ForegroundWindow>().Count());
        Assert.Contains(
            picker.Items.Cast<ForegroundWindow>(),
            window => window is { ProcessName: "game", Title: "游戏窗口" });

        picker.SelectedItem = picker.Items.Cast<ForegroundWindow>().Single(window => window.ProcessName == "game");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("game", viewModel.Rules[0].ProcessName);
        Assert.True(viewModel.HasUnsavedChanges);
    }

    [AvaloniaFact]
    public void Opening_The_Picker_Re_Reads_The_Windows_That_Are_Open()
    {
        // The list is only looked at when a picker opens, so that is when it is read: a program
        // opened after the panel was built has to show up in it, not the list the panel was
        // built with.
        var store = new ForegroundRuleStore(Path.Combine(_directory, ForegroundRuleStore.FileName));
        var source = new StubForegroundSource(new ForegroundContext("explorer", false));
        var watcher = new ForegroundWatcher(source, new StubForegroundTarget(), store.Load());
        var (window, panel, viewModel) = Show(new ForegroundRulesViewModel(watcher, store));

        viewModel.AddRuleCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var picker = panel.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "RuleWindowPicker");
        Assert.Empty(picker.Items.Cast<ForegroundWindow>());

        source.Windows = [new ForegroundWindow("game", "游戏窗口")];
        Click(picker, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(picker.IsDropDownOpen, "the click opens the drop-down, which is what re-reads the list");
        var offered = picker.Items.Cast<ForegroundWindow>().ToList();
        Assert.Equal("game", Assert.Single(offered).ProcessName);
    }

    [AvaloniaFact]
    public void A_Platform_That_Cannot_Enumerate_Windows_Says_So_Above_The_Table()
    {
        var (window, panel, viewModel) = Show(new ForegroundContext("explorer", false));

        var hint = panel.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "当前平台无法列出已打开的窗口，请手动输入进程名。");

        // An empty drop-down has to say why it is empty: a picker with nothing in it and no
        // explanation reads as a broken one rather than as a platform limitation.
        Assert.True(hint.IsEffectivelyVisible);
        Assert.False(viewModel.HasWindowChoices);

        viewModel.AddRuleCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(panel.GetVisualDescendants().OfType<ComboBox>()
            .Single(box => box.Name == "RuleWindowPicker")
            .Items.Cast<ForegroundWindow>());
    }

    [AvaloniaFact]
    public void Saving_From_The_Panel_Applies_The_Table_And_Reports_It()
    {
        var (window, panel, viewModel) = Show(new ForegroundContext("explorer", false));

        viewModel.AddRuleCommand.Execute(null);
        viewModel.Rules[0].ProcessName = "game";
        viewModel.Rules[0].Action = ForegroundAction.NativeGamepad;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var save = panel.FindControl<Button>("SaveRulesButton")!;
        Assert.Same(viewModel.SaveCommand, save.Command);

        // The save button is armed by the edit, not by the load: a row that changed without
        // telling the view model would leave the button disabled on a table that has something
        // to save.
        Assert.True(save.IsEnabled);

        Click(save, window);

        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Contains("已保存", viewModel.StatusMessage);
        Assert.True(panel.FindControl<TextBlock>("RulesStatusText")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Picking_A_Window_Clears_The_Rules_That_Can_Never_Match_Warning()
    {
        // The reported bug: a rule added empty says its program name is missing, and the line
        // stayed after the program was picked from the window picker - the row's validation is
        // derived from the program name, so filling the name has to re-raise it. A row that
        // keeps insisting a perfectly good rule can never match is worse than no line at all.
        var store = new ForegroundRuleStore(Path.Combine(_directory, ForegroundRuleStore.FileName));
        var source = new StubForegroundSource(
            new ForegroundContext("explorer", false),
            [new ForegroundWindow("game", "游戏窗口")]);
        var watcher = new ForegroundWatcher(source, new StubForegroundTarget(), store.Load());
        var (window, panel, viewModel) = Show(new ForegroundRulesViewModel(watcher, store));

        viewModel.AddRuleCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var warning = panel.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "程序名不能为空；该规则永远不会命中。");
        Assert.True(warning.IsEffectivelyVisible);

        var picker = panel.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "RuleWindowPicker");
        picker.SelectedItem = picker.Items.Cast<ForegroundWindow>().Single(window => window.ProcessName == "game");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("game", viewModel.Rules[0].ProcessName);
        Assert.False(warning.IsEffectivelyVisible, "the row's error clears the moment the rule can match");
        Assert.True(panel.FindControl<Button>("SaveRulesButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public void A_Saved_Rule_Row_Collapses_To_A_Summary_And_Opens_On_Demand()
    {
        // The table is read far more often than it is written, so a rule from the saved table is
        // one summary line; the editing fields sit under it only while the row is open. The
        // enable and delete controls stay on the summary line, because neither needs the row
        // open - a collapsed row is never a dead end.
        var (window, panel, viewModel) = Show(
            new ForegroundContext("explorer", false),
            new ForegroundSettings
            {
                Rules = [new ForegroundRule { ProcessName = "chrome", Priority = 10, Slot = 2 }],
            });

        var rule = viewModel.Rules.Single();
        var row = panel.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("ruleRow"));
        var toggle = panel.GetVisualDescendants().OfType<ToggleButton>().Single(button => button.Name == "RuleExpandButton");
        var summary = panel.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "RuleSummaryText");
        var processBox = panel.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "RuleProcessBox");
        var delete = panel.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RuleDeleteButton");
        var closedHeight = row.Bounds.Height;

        Assert.False(rule.IsExpanded);
        Assert.Equal("chrome → 预设 2 · 优先级 10", summary.Text);
        Assert.False(processBox.IsEffectivelyVisible, "a closed row shows its summary, not its editor");
        Assert.True(delete.IsEffectivelyVisible, "delete works on a closed row");

        Click(toggle, window);

        Assert.True(rule.IsExpanded);
        Assert.True(processBox.IsEffectivelyVisible, "an open row shows its editor");
        // The whole point of the collapse: the row the user reads is one line tall, not the
        // form it takes to write one.
        Assert.True(row.Bounds.Height > closedHeight * 2, "an open row carries the editor, so it is far taller than the summary line");

        Click(toggle, window);

        Assert.False(rule.IsExpanded);
        Assert.False(processBox.IsEffectivelyVisible);

        // The row is keyboard reachable: the twisty takes focus and Space opens the row, which
        // is what a gamepad or a keyboard user has to be able to do with every row.
        window.Focus();
        Assert.True(toggle.Focus());
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(rule.IsExpanded);
        Assert.True(processBox.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_Number_Box_Keeps_One_Rounded_Well_Around_Field_And_Spinner()
    {
        // The square-edged look the user reported: Fluent's number box draws a frame around the
        // field and a frame around each spinner button, so the up/down pair reads as two boxes
        // bolted onto a third. The restyle keeps one well, and the pair's buttons carry no
        // frame of their own to square the corners off again.
        var (window, panel, viewModel) = Show(new ForegroundContext("explorer", false));

        Assert.True(
            Application.Current!.TryFindResource("CornerRadiusMd", out var token),
            "the corner-radius token has to resolve for the well to be the app's own shape");
        var radius = Assert.IsType<CornerRadius>(token);

        var box = panel.FindControl<NumericUpDown>("DefaultSlotBox")!;
        var spinner = box.GetVisualDescendants().OfType<ButtonSpinner>().Single();
        var buttons = spinner.GetVisualDescendants().OfType<RepeatButton>().ToList();

        Assert.Equal(radius, box.CornerRadius);
        Assert.Equal(box.CornerRadius, spinner.CornerRadius);
        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, button => Assert.Equal(new Thickness(0), button.BorderThickness));

        // And the box still writes the number it shows through the restyled well.
        box.Value = 5;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(5, viewModel.DefaultSlot);
    }

    private static void Click(Control target, Window window)
    {
        window.UpdateLayout();
        target.BringIntoView();
        window.UpdateLayout();
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class StubForegroundSource(ForegroundContext context, IReadOnlyList<ForegroundWindow>? windows = null)
        : IForegroundSource
    {
        public ForegroundContext Context { get; } = context;

        /// <summary>
        /// What the platform reports as open. Settable so a test can open a program after the
        /// panel was built and watch the picker re-read it as the drop-down opens.
        /// </summary>
        public IReadOnlyList<ForegroundWindow> Windows { get; set; } = windows ?? [];

        public ForegroundContext Read() => Context;

        public IReadOnlyList<ForegroundWindow> ListWindows() => Windows;
    }

    private sealed class StubForegroundTarget : IForegroundTarget
    {
        public int ActiveSlot { get; private set; } = 1;

        public bool IsPaused { get; private set; }

        public void SwitchProfile(int slot) => ActiveSlot = slot;

        public void SetPaused(bool paused) => IsPaused = paused;
    }
}
