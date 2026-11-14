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

    /// <summary>
    /// The slot drop-downs' source: a fixed list, so a test says exactly what the panel offers
    /// without depending on what the machine it runs on has stored. Replaceable, so a test can
    /// open a preset after the panel was built and watch the list re-read it.
    /// </summary>
    private sealed class FakePresetSlotSource : IPresetSlotSource
    {
        private readonly List<PresetSlotOption> _slots;

        public FakePresetSlotSource()
            : this(
            [
                new PresetSlotOption(1, "预设 1"),
                new PresetSlotOption(2, "布局默认"),
                new PresetSlotOption(3, "预设 3"),
            ])
        {
        }

        public FakePresetSlotSource(List<PresetSlotOption> slots) => _slots = slots;

        /// <summary>A store with nothing in it, which is what a device that was never configured looks like.</summary>
        public static FakePresetSlotSource Empty => new([]);

        public IReadOnlyList<PresetSlotOption> Slots() => _slots;

        public void ReplaceWith(params PresetSlotOption[] slots)
        {
            _slots.Clear();
            _slots.AddRange(slots);
        }
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
        ForegroundSettings? stored = null,
        IPresetSlotSource? presetSlots = null)
    {
        var store = new ForegroundRuleStore(Path.Combine(_directory, ForegroundRuleStore.FileName));
        if (stored is not null)
        {
            store.Save(stored);
        }

        var source = new FakeForegroundSource();
        var watcher = new ForegroundWatcher(source, new FakeForegroundTarget(), new ForegroundSettings());
        var viewModel = new ForegroundRulesViewModel(watcher, store, presetSlots ?? new FakePresetSlotSource());

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
        var viewModel = new ForegroundRulesViewModel(watcher, store, FakePresetSlotSource.Empty);

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
    public void Picking_A_Window_Clears_The_Rules_Validation_State()
    {
        // The reported bug: a rule added empty says its program name is missing, and the line
        // stays after the program is picked from the window picker - the row's own validation
        // is derived from the program name, so filling the name has to re-raise it, or the row
        // keeps reporting a rule that can never match while the rule reads perfectly well.
        var (viewModel, _, source) = Create();
        source.Windows = [new ForegroundWindow("game", "游戏窗口")];

        viewModel.AddRuleCommand.Execute(null);
        var rule = viewModel.Rules[0];
        var raised = new List<string>();
        rule.PropertyChanged += (_, args) => raised.Add(args.PropertyName!);

        Assert.False(rule.IsValid);
        Assert.NotNull(rule.ValidationMessage);

        rule.SelectedWindow = new ForegroundWindow("game", "游戏窗口");

        Assert.Equal("game", rule.ProcessName);
        Assert.True(rule.IsValid);
        Assert.Null(rule.ValidationMessage);
        Assert.Contains(nameof(ForegroundRuleItem.IsValid), raised);
        Assert.Contains(nameof(ForegroundRuleItem.ValidationMessage), raised);

        // And the other way round: emptying the name again brings the error back, because the
        // row re-validates on every change rather than only once.
        raised.Clear();
        rule.ProcessName = "   ";

        Assert.False(rule.IsValid);
        Assert.Contains(nameof(ForegroundRuleItem.IsValid), raised);
    }

    [Fact]
    public void A_Row_Of_The_Saved_Table_Starts_Collapsed_And_A_New_One_Open()
    {
        // The table is read far more often than it is written, so a saved row is a summary
        // line; the row being written is open, because that is where the user is standing.
        var (viewModel, _, _) = Create(new ForegroundSettings
        {
            Rules = [new ForegroundRule { ProcessName = "chrome", Slot = 3 }],
        });

        Assert.All(viewModel.Rules, rule => Assert.False(rule.IsExpanded));

        // Collapsing is a view state, not an edit: it never touches the table being staged.
        viewModel.Rules[0].IsExpanded = true;
        viewModel.Rules[0].IsExpanded = false;

        Assert.False(viewModel.HasUnsavedChanges);
        Assert.False(viewModel.CanSave);

        viewModel.AddRuleCommand.Execute(null);

        Assert.False(viewModel.Rules[0].IsExpanded, "the rows of the saved table stay collapsed");
        Assert.True(viewModel.Rules[1].IsExpanded);
    }

    [Fact]
    public void The_Summary_Names_The_Program_The_Action_And_The_Priority()
    {
        // The collapsed row is the table's whole content at a glance, so the line has to say
        // what the rule does and what it costs to be considered - the priority above all,
        // which is the field that decides which rule wins.
        var (viewModel, _, _) = Create(new ForegroundSettings
        {
            Rules = [new ForegroundRule { ProcessName = "chrome", Priority = 10, Slot = 1 }],
        });

        Assert.Equal("chrome → 预设 1 · 优先级 10", viewModel.Rules[0].Summary);

        viewModel.Rules[0].Slot = 4;
        viewModel.Rules[0].Priority = 20;

        Assert.Equal("chrome → 预设 4 · 优先级 20", viewModel.Rules[0].Summary);

        viewModel.Rules[0].Action = ForegroundAction.NativeGamepad;

        Assert.Equal("chrome → 原生手柄 · 优先级 20", viewModel.Rules[0].Summary);

        // A row with no program yet says so rather than showing an empty arrow.
        viewModel.Rules[0].ProcessName = string.Empty;

        Assert.Equal("未命名规则 · 优先级 20", viewModel.Rules[0].Summary);
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

    [Fact]
    public void The_Slot_Drop_Downs_Offer_The_Presets_The_Store_Holds()
    {
        // The reported change: both slot fields were number boxes, so the user counted to a preset
        // through up/down arrows. They are drop-downs now, and what they offer is the slots the
        // store holds, named - a rule names its preset rather than a number.
        var (viewModel, _, _) = Create();

        Assert.Equal([1, 2, 3], viewModel.SlotOptions.Select(option => option.Slot));
        Assert.Equal("2 · 布局默认", viewModel.SlotOptions[1].Display);
        Assert.Equal("预设 1", viewModel.SlotOptions[0].Display);
    }

    [Fact]
    public void Picking_A_Slot_Option_Writes_The_Rules_Slot()
    {
        var (viewModel, _, _) = Create(new ForegroundSettings
        {
            Rules = [new ForegroundRule { ProcessName = "chrome", Slot = 1 }],
        });

        var rule = viewModel.Rules.Single();
        rule.SelectedSlotOption = viewModel.SlotOptions.Single(option => option.Slot == 3);

        Assert.Equal(3, rule.Slot);
        Assert.Equal(3, rule.SelectedSlotOption!.Slot);
        Assert.Contains("预设 3", rule.Summary);
        Assert.True(viewModel.HasUnsavedChanges);

        // And back the other way: the row is pointed at the entry that spells its slot.
        rule.Slot = 2;

        Assert.Equal(2, rule.SelectedSlotOption!.Slot);
    }

    [Fact]
    public void The_Desktop_Defaults_Drop_Down_Selection_Follows_The_Slot()
    {
        var (viewModel, _, _) = Create(new ForegroundSettings { DefaultSlot = 2 });

        Assert.Equal(2, viewModel.DefaultSlotOption!.Slot);

        viewModel.DefaultSlotOption = viewModel.SlotOptions.Single(option => option.Slot == 3);

        Assert.Equal(3, viewModel.DefaultSlot);
        Assert.Equal(3, viewModel.DefaultSlotOption!.Slot);
        Assert.True(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void A_Slot_The_Table_Names_Stays_Offered_When_The_Store_Holds_Nothing_For_It()
    {
        // A rule that names a slot the store has no profile for must not turn into a blank
        // drop-down: the slot is the rule's own value, and the drop-down has to be able to show
        // the one the rule already names.
        var (viewModel, _, _) = Create(
            new ForegroundSettings { Rules = [new ForegroundRule { ProcessName = "chrome", Slot = 7 }] },
            FakePresetSlotSource.Empty);

        Assert.Equal([1, 2, 3, 4, 7], viewModel.SlotOptions.Select(option => option.Slot));
        Assert.Equal(7, viewModel.Rules.Single().SelectedSlotOption!.Slot);
    }

    [Fact]
    public void An_Empty_Slot_Source_Falls_Back_To_The_Initial_Slots()
    {
        // A store with nothing in it still has to offer a choice: an empty drop-down is a control
        // that looks broken rather than one that has nothing to say yet.
        var (viewModel, _, _) = Create(presetSlots: FakePresetSlotSource.Empty);

        Assert.Equal([1, 2, 3, 4], viewModel.SlotOptions.Select(option => option.Slot));
        Assert.All(viewModel.SlotOptions, option => Assert.Equal($"预设 {option.Slot}", option.Display));
    }

    [Fact]
    public void Refreshing_The_Slot_Options_Keeps_The_Rows_Pointed_At_Their_Slot()
    {
        // The entries are rebuilt - new objects - every time the list is re-read, so a row that
        // held on to the entry it was given would show a blank drop-down the moment a preset was
        // added in another tab.
        var source = new FakePresetSlotSource();
        var (viewModel, _, _) = Create(
            new ForegroundSettings { Rules = [new ForegroundRule { ProcessName = "chrome", Slot = 2 }] },
            source);

        Assert.Equal(2, viewModel.Rules.Single().SelectedSlotOption!.Slot);

        source.ReplaceWith(new PresetSlotOption(1, "预设 1"), new PresetSlotOption(2, "新布局"));
        viewModel.RefreshSlotOptionsCommand.Execute(null);

        Assert.Equal("2 · 新布局", viewModel.SlotOptions[1].Display);
        Assert.Equal(2, viewModel.Rules.Single().SelectedSlotOption!.Slot);
        Assert.Equal(viewModel.DefaultSlot, viewModel.DefaultSlotOption!.Slot);
    }

    [Fact]
    public void A_Cleared_Slot_Selection_Does_Not_Take_The_Slot_Away()
    {
        // A drop-down that cannot resolve the rule's slot to an offered entry clears its
        // selection, and that write-back must not empty the rule's slot: the slot is the model
        // and the option is only how the control reads it.
        var (viewModel, _, _) = Create(new ForegroundSettings
        {
            Rules = [new ForegroundRule { ProcessName = "chrome", Slot = 2 }],
        });

        var rule = viewModel.Rules.Single();
        rule.SelectedSlotOption = null;

        Assert.Equal(2, rule.Slot);
    }

    [Fact]
    public void The_Priority_Field_Takes_The_Number_It_Shows()
    {
        // The reported change: the priority was a number box, so typing one meant fighting the
        // up/down pair. It is a plain number field now, and the number it shows is the number the
        // rule carries.
        var (viewModel, _, _) = Create(new ForegroundSettings
        {
            Rules = [new ForegroundRule { ProcessName = "chrome", Priority = 4 }],
        });

        var rule = viewModel.Rules.Single();
        Assert.Equal("4", rule.PriorityText);

        rule.PriorityText = "12";
        Assert.Equal(12, rule.Priority);
        Assert.Equal("12", rule.PriorityText);
        Assert.Contains("优先级 12", rule.Summary);

        rule.PriorityText = "-3";
        Assert.Equal(-3, rule.Priority);

        // And the other way round: a rule loaded from the saved table shows the number it was
        // saved with, without the field having to be touched.
        rule.LoadFrom(new ForegroundRule { ProcessName = "chrome", Priority = 9 });
        Assert.Equal("9", rule.PriorityText);
    }

    [Fact]
    public void An_Empty_Priority_Field_Falls_Back_To_The_Default()
    {
        var (viewModel, _, _) = Create(new ForegroundSettings
        {
            Rules = [new ForegroundRule { ProcessName = "chrome", Priority = 4 }],
        });

        var rule = viewModel.Rules.Single();
        rule.PriorityText = string.Empty;

        Assert.Equal(ForegroundRuleItem.DefaultPriority, rule.Priority);
        Assert.Equal("0", rule.PriorityText);
    }

    [Fact]
    public void An_Unparseable_Priority_Leaves_The_Last_Good_Value()
    {
        // The field must not eat a character mid-edit: what was typed stays in the field, and the
        // rule keeps the number it last had until a parseable one is typed over it.
        var (viewModel, _, _) = Create(new ForegroundSettings
        {
            Rules = [new ForegroundRule { ProcessName = "chrome", Priority = 5 }],
        });

        var rule = viewModel.Rules.Single();
        rule.PriorityText = "1x";

        Assert.Equal(5, rule.Priority);
        Assert.Equal("1x", rule.PriorityText);

        // And a number typed over the mess is taken as soon as it parses.
        rule.PriorityText = "18";
        Assert.Equal(18, rule.Priority);
        Assert.Equal("18", rule.PriorityText);
    }

    [Fact]
    public void The_Priority_Field_Clamps_Rather_Than_Refusing()
    {
        var (viewModel, _, _) = Create(new ForegroundSettings
        {
            Rules = [new ForegroundRule { ProcessName = "chrome", Priority = 5 }],
        });

        var rule = viewModel.Rules.Single();

        rule.PriorityText = "400";
        Assert.Equal(ForegroundRuleItem.MaxPriority, rule.Priority);
        Assert.Equal("99", rule.PriorityText);

        rule.PriorityText = "-400";
        Assert.Equal(ForegroundRuleItem.MinPriority, rule.Priority);
        Assert.Equal("-99", rule.PriorityText);
    }
}
