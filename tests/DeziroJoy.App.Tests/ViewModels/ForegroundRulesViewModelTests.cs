using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The rules surface over a live watcher. The panel is a view over the running state, so what
/// these tests pin is the contract between the two: staged edits land on save and not before,
/// the active-state lines quote the rule table rather than a second copy of it, and the lock is
/// the one control that acts immediately.
/// </summary>
public sealed class ForegroundRulesViewModelTests : IDisposable
{
    private sealed class FakeForegroundSource : IForegroundSource
    {
        public ForegroundContext Context { get; set; } = ForegroundContext.Empty;

        public IReadOnlyList<ForegroundWindow> Windows { get; set; } = [];

        public ForegroundContext Read() => Context;

        public IReadOnlyList<ForegroundWindow> ListWindows() => Windows;
    }

    private sealed class FakeForegroundTarget : IForegroundTarget
    {
        public int ActiveSlot { get; private set; } = 1;

        public bool IsPaused { get; private set; }

        public void SwitchProfile(int slot) => ActiveSlot = slot;

        public void SetPaused(bool paused) => IsPaused = paused;
    }

    private readonly string _directory;

    public ForegroundRulesViewModelTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-foreground-rules-vm", Guid.NewGuid().ToString("N"));
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

    private (ForegroundRulesViewModel ViewModel, ForegroundWatcher Watcher, FakeForegroundSource Source) Create(
        ForegroundSettings? stored = null)
    {
        var store = new ForegroundRuleStore(Path.Combine(_directory, ForegroundRuleStore.FileName));
        if (stored is not null)
        {
            store.Save(stored);
        }

        var source = new FakeForegroundSource();
        var watcher = new ForegroundWatcher(source, new FakeForegroundTarget(), new ForegroundSettings());
        var viewModel = new ForegroundRulesViewModel(watcher, store);

        return (viewModel, watcher, source);
    }

    [Fact]
    public void The_Stored_Table_Is_What_Runs()
    {
        var (viewModel, watcher, _) = Create(new ForegroundSettings
        {
            DefaultSlot = 2,
            Rules = [new ForegroundRule { ProcessName = "chrome", Priority = 4, Slot = 3 }],
        });

        Assert.Single(viewModel.Rules);
        Assert.Equal("chrome", viewModel.Rules[0].ProcessName);
        Assert.Equal(4, viewModel.Rules[0].Priority);
        Assert.Equal(2, viewModel.DefaultSlot);
        Assert.Equal(3, watcher.Settings.Rules.Single().Slot);
        Assert.False(viewModel.HasUnsavedChanges, "a freshly loaded table has nothing to save");
    }

    [Fact]
    public void An_Edit_Is_Staged_Until_Save()
    {
        var (viewModel, watcher, _) = Create(new ForegroundSettings
        {
            Rules = [new ForegroundRule { ProcessName = "chrome", Slot = 3 }],
        });

        viewModel.Rules[0].ProcessName = "firefox";

        Assert.True(viewModel.HasUnsavedChanges);
        Assert.True(viewModel.CanSave);
        // An edit must not apply per keystroke: half a typed program name matches nothing, and
        // a profile that flickered to the desktop default and back would be worse than useless.
        Assert.Equal("chrome", watcher.Settings.Rules.Single().ProcessName);

        viewModel.SaveCommand.Execute(null);

        Assert.Equal("firefox", watcher.Settings.Rules.Single().ProcessName);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Contains("已保存", viewModel.StatusMessage);
    }

    [Fact]
    public void Save_Writes_The_Table_Through_So_A_Restart_Resumes_It()
    {
        var (viewModel, _, _) = Create(new ForegroundSettings
        {
            Rules = [new ForegroundRule { ProcessName = "chrome", Slot = 3 }],
        });

        viewModel.Rules[0].Slot = 5;
        viewModel.SaveCommand.Execute(null);

        var reloaded = new ForegroundRuleStore(Path.Combine(_directory, ForegroundRuleStore.FileName)).Load();
        Assert.Equal(5, reloaded.Rules.Single().Slot);
    }

    [Fact]
    public void A_Rule_Without_A_Program_Name_Blocks_The_Save_And_Says_Why()
    {
        var (viewModel, watcher, _) = Create();

        viewModel.AddRuleCommand.Execute(null);
        Assert.Single(viewModel.Rules);

        Assert.True(viewModel.HasInvalidRules);
        Assert.False(viewModel.CanSave);

        viewModel.SaveCommand.Execute(null);

        Assert.Contains("程序名", viewModel.StatusMessage);
        Assert.Empty(watcher.Settings.Rules);
    }

    [Fact]
    public void Adding_And_Deleting_A_Rule_Reaches_The_Table()
    {
        var (viewModel, watcher, _) = Create();

        viewModel.AddRuleCommand.Execute(null);
        viewModel.Rules[0].ProcessName = "game";
        viewModel.Rules[0].Action = ForegroundAction.NativeGamepad;
        viewModel.SaveCommand.Execute(null);

        Assert.Equal(ForegroundAction.NativeGamepad, watcher.Settings.Rules.Single().Action);

        viewModel.DeleteRuleCommand.Execute(viewModel.Rules[0]);
        viewModel.SaveCommand.Execute(null);

        Assert.Empty(watcher.Settings.Rules);
        Assert.Empty(viewModel.Rules);
    }

    [Fact]
    public void The_Active_State_Quotes_The_Rule_Table()
    {
        var (viewModel, watcher, source) = Create(new ForegroundSettings
        {
            DefaultSlot = 2,
            Rules =
            [
                new ForegroundRule { ProcessName = "chrome", Slot = 3 },
                new ForegroundRule { ProcessName = "game", Action = ForegroundAction.NativeGamepad },
            ],
        });

        source.Context = new ForegroundContext("chrome", false);
        viewModel.RefreshCommand.Execute(null);

        Assert.Equal("预设 3", viewModel.ActiveProfileText);
        Assert.Contains("预设 3", viewModel.ActiveReason);
        Assert.True(viewModel.HasForegroundProcess);
        Assert.Equal("chrome", viewModel.ForegroundProcessText);

        source.Context = new ForegroundContext("game", false);
        viewModel.RefreshCommand.Execute(null);

        Assert.Contains("已暂停", viewModel.ActiveProfileText);
        Assert.Contains("原生手柄", viewModel.ActiveReason);

        source.Context = new ForegroundContext("explorer", false);
        viewModel.RefreshCommand.Execute(null);

        Assert.Contains("桌面默认", viewModel.ActiveProfileText);
        Assert.Contains("预设 2", viewModel.ActiveReason);

        source.Context = ForegroundContext.Empty;
        viewModel.RefreshCommand.Execute(null);

        Assert.False(viewModel.HasForegroundProcess);
        Assert.Contains("无法读取", viewModel.ForegroundProcessText);
        Assert.Contains("桌面", viewModel.ActiveReason);
    }

    [Fact]
    public void The_Lock_Acts_Immediately_And_Says_What_It_Froze()
    {
        var (viewModel, watcher, source) = Create(new ForegroundSettings
        {
            Rules = [new ForegroundRule { ProcessName = "chrome", Slot = 3 }],
        });

        source.Context = new ForegroundContext("explorer", false);
        viewModel.RefreshCommand.Execute(null);
        Assert.False(viewModel.IsLocked);

        viewModel.ToggleLockCommand.Execute(null);

        Assert.True(viewModel.IsLocked);
        Assert.True(watcher.IsLocked);
        Assert.Contains("已锁定", viewModel.LockStateText);
        Assert.Contains("解锁", viewModel.LockButtonText);

        // The rule that would have taken over stands down: the profile in effect is still the
        // desktop default, and the panel says so instead of quoting the rule that lost.
        source.Context = new ForegroundContext("chrome", false);
        viewModel.RefreshCommand.Execute(null);

        Assert.Equal("桌面默认：预设 1", viewModel.ActiveProfileText);
        Assert.Contains("已锁定", viewModel.ActiveReason);
        Assert.Contains("预设 3", viewModel.ActiveReason, StringComparison.Ordinal);

        viewModel.ToggleLockCommand.Execute(null);

        Assert.False(viewModel.IsLocked);
        Assert.Contains("未锁定", viewModel.LockStateText);
        Assert.Equal("预设 3", viewModel.ActiveProfileText);
    }

    [Fact]
    public void Turning_The_Table_Off_Is_Staged_Like_Any_Other_Edit()
    {
        var (viewModel, watcher, source) = Create(new ForegroundSettings
        {
            DefaultSlot = 2,
            Rules = [new ForegroundRule { ProcessName = "chrome", Slot = 3 }],
        });

        viewModel.IsEnabled = false;

        Assert.True(viewModel.HasUnsavedChanges);
        Assert.True(watcher.Settings.Enabled, "the toggle is staged, not applied on the click");

        viewModel.SaveCommand.Execute(null);

        Assert.False(watcher.Settings.Enabled);

        source.Context = new ForegroundContext("chrome", false);
        viewModel.RefreshCommand.Execute(null);

        Assert.Contains("桌面默认", viewModel.ActiveProfileText);
        Assert.Contains("停用", viewModel.ActiveReason);
    }

    [Fact]
    public void Reverting_Puts_The_Saved_Table_Back()
    {
        var (viewModel, watcher, _) = Create(new ForegroundSettings
        {
            Rules = [new ForegroundRule { ProcessName = "chrome", Slot = 3 }],
        });

        viewModel.Rules[0].Slot = 5;
        Assert.True(viewModel.HasUnsavedChanges);

        viewModel.RevertCommand.Execute(null);

        Assert.Equal(3, viewModel.Rules[0].Slot);
        Assert.Equal(3, watcher.Settings.Rules.Single().Slot);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Contains("已恢复", viewModel.StatusMessage);
    }

    [Fact]
    public void A_Damaged_File_Says_So_And_Runs_The_Defaults()
    {
        var store = new ForegroundRuleStore(Path.Combine(_directory, ForegroundRuleStore.FileName));
        File.WriteAllText(store.FilePath, "{ not json");

        var watcher = new ForegroundWatcher(new FakeForegroundSource(), new FakeForegroundTarget());
        var viewModel = new ForegroundRulesViewModel(watcher, store);

        Assert.Empty(viewModel.Rules);
        Assert.Contains("无法解析", viewModel.StatusMessage);
        Assert.Equal(ForegroundRule.DefaultSlot, viewModel.DefaultSlot);
    }

    [Fact]
    public void The_Panel_Says_What_The_Platform_Can_See()
    {
        var (viewModel, _, _) = Create();

        // A fake source is not a platform source, so the note is the honest one.
        Assert.Contains("不支持", viewModel.SourceNote);
    }

    [Fact]
    public void The_Action_Picker_Keeps_The_Index_And_The_Enum_In_Step()
    {
        var (viewModel, _, _) = Create();
        viewModel.AddRuleCommand.Execute(null);
        var rule = viewModel.Rules[0];

        rule.ActionIndex = 1;
        Assert.Equal(ForegroundAction.NativeGamepad, rule.Action);
        Assert.True(rule.IsNativeGamepadAction);
        Assert.False(rule.IsProfileAction);
        Assert.Contains("原生手柄", rule.Describe);

        rule.ActionIndex = 0;
        Assert.Equal(ForegroundAction.Profile, rule.Action);
        Assert.Contains("预设", rule.Describe);
    }

    [Fact]
    public void Picking_A_Window_Fills_The_Rules_Process_Name()
    {
        // The whole point of the picker: a rule names its program by pointing at an open
        // window instead of typing it. The pick is an edit like any other - staged until 保存 -
        // and the selection clears, so the same window can be picked for a second rule.
        var (viewModel, _, source) = Create();
        source.Windows = [new ForegroundWindow("game", "游戏窗口")];

        viewModel.AddRuleCommand.Execute(null);
        var rule = viewModel.Rules[0];

        rule.SelectedWindow = new ForegroundWindow("game", "游戏窗口");

        Assert.Equal("game", rule.ProcessName);
        Assert.Null(rule.SelectedWindow);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.False(viewModel.HasInvalidRules);
    }

    [Fact]
    public void The_Window_List_Names_Each_Program_Once_With_The_Foreground_First()
    {
        // Several windows of one program are one program to a rule, so the list carries each
        // name once - and the program in front leads it, because the rule being written is
        // most often about the program the user just came from.
        var (viewModel, _, source) = Create();
        source.Context = new ForegroundContext("explorer", false);
        viewModel.RefreshCommand.Execute(null);

        source.Windows =
        [
            new ForegroundWindow("chrome", "标签页"),
            new ForegroundWindow("CHROME", "另一个窗口"),
            new ForegroundWindow("explorer", null),
            new ForegroundWindow("game", "游戏"),
        ];
        viewModel.RefreshWindowListCommand.Execute(null);

        Assert.Equal(["explorer", "chrome", "game"], viewModel.WindowChoices.Select(window => window.ProcessName));
        Assert.True(viewModel.HasWindowChoices);
        Assert.Equal("标签页", viewModel.WindowChoices[1].Title);
    }

    [Fact]
    public void The_Foreground_Program_Leads_The_List_Even_With_A_Suffix_It_Does_Not_Carry()
    {
        // "The program in front" is decided the way the rule table decides it - by the name
        // normalised, suffix and case aside - so a platform that reports "Chrome.exe" still
        // leads the list to the "chrome" entry the picker would fill.
        var (viewModel, _, source) = Create();
        source.Context = new ForegroundContext("Chrome.exe", false);
        viewModel.RefreshCommand.Execute(null);

        source.Windows = [new ForegroundWindow("game", "游戏"), new ForegroundWindow("chrome", "标签页")];
        viewModel.RefreshWindowListCommand.Execute(null);

        Assert.Equal(["chrome", "game"], viewModel.WindowChoices.Select(window => window.ProcessName));
    }

    [Fact]
    public void A_Platform_That_Cannot_Enumerate_Windows_Leaves_The_Picker_Empty()
    {
        var (viewModel, _, _) = Create();

        viewModel.RefreshWindowListCommand.Execute(null);

        Assert.Empty(viewModel.WindowChoices);
        Assert.False(viewModel.HasWindowChoices);
    }

    [Fact]
    public void The_Window_List_Skips_A_Window_The_Source_Cannot_Name()
    {
        // A window with no program name is one the picker cannot fill a rule from, so it is not
        // offered - an entry that names nothing would be a choice that writes nothing.
        var (viewModel, _, source) = Create();
        source.Windows =
        [
            new ForegroundWindow(string.Empty, "无名窗口"),
            new ForegroundWindow("game", "游戏窗口"),
            new ForegroundWindow("   ", "空白名"),
        ];

        viewModel.RefreshWindowListCommand.Execute(null);

        Assert.Equal(["game"], viewModel.WindowChoices.Select(window => window.ProcessName));
    }

    [Fact]
    public void A_Window_With_No_Title_Still_Fills_The_Rules_Process_Name()
    {
        // Matching is by process name alone - the one part every platform can answer - so a
        // window with no title still lists, labelled by its process, and still fills the rule.
        var (viewModel, _, source) = Create();
        source.Windows = [new ForegroundWindow("game", null)];

        viewModel.AddRuleCommand.Execute(null);
        viewModel.RefreshWindowListCommand.Execute(null);

        var choice = Assert.Single(viewModel.WindowChoices);
        Assert.Null(choice.Title);
        Assert.Equal("game", choice.Display);

        viewModel.Rules[0].SelectedWindow = choice;

        Assert.Equal("game", viewModel.Rules[0].ProcessName);
        Assert.False(viewModel.HasInvalidRules);
        Assert.True(viewModel.CanSave);
    }

    [Fact]
    public void The_Same_Window_Can_Be_Picked_For_A_Second_Rule()
    {
        // The selection clears after a pick, so the placeholder returns and the same window can
        // be picked again - a user writing two rules about one program picks it twice.
        var (viewModel, _, source) = Create();
        source.Windows = [new ForegroundWindow("game", "游戏窗口")];

        viewModel.AddRuleCommand.Execute(null);
        viewModel.AddRuleCommand.Execute(null);
        viewModel.RefreshWindowListCommand.Execute(null);

        var choice = Assert.Single(viewModel.WindowChoices);

        viewModel.Rules[0].SelectedWindow = choice;
        viewModel.Rules[1].SelectedWindow = choice;

        Assert.All(viewModel.Rules, rule => Assert.Equal("game", rule.ProcessName));
        Assert.All(viewModel.Rules, rule => Assert.Null(rule.SelectedWindow));
        Assert.True(viewModel.CanSave);

        // And the typed-over case: a window picked after the name was edited by hand still wins,
        // because the pick is the user saying which program they meant.
        viewModel.Rules[0].ProcessName = "somethingelse";
        viewModel.Rules[0].SelectedWindow = choice;

        Assert.Equal("game", viewModel.Rules[0].ProcessName);
    }
}
