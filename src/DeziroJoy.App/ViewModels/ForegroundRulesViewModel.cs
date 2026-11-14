using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.App.Services;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// The foreground rules surface: which program runs which profile, which programs get the pad
/// themselves, what the desktop falls back to, and - the part the user actually asks about -
/// why the profile that is running right now is the one running. Every answer comes from the
/// live <see cref="ForegroundWatcher"/>; nothing here is a second copy of the state.
/// <para>
/// Edits are staged in this view model and land on 保存, which writes the table and applies it.
/// Staging rather than applying per keystroke is deliberate: half a typed program name matches
/// nothing, and a profile that flickered to the desktop default and back on every character
/// would be worse than useless. The manual lock is the one control that acts immediately - it
/// is what a user reaches for when a rule is fighting them.
/// </para>
/// </summary>
public partial class ForegroundRulesViewModel : ObservableObject, IDisposable
{
    private readonly ForegroundWatcher _watcher;

    private readonly ForegroundRuleStore _store;

    private bool _disposed;

    public ForegroundRulesViewModel(ForegroundWatcher watcher, ForegroundRuleStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(watcher);

        _watcher = watcher;
        _store = store ?? new ForegroundRuleStore();

        _watcher.StateChanged += OnWatcherStateChanged;

        LoadFromStore();
        SyncFromWatcher();
        RefreshWindowList();
    }

    public ObservableCollection<ForegroundRuleItem> Rules { get; } = [];

    /// <summary>
    /// The windows the picker offers: what the platform enumerates as open right now, one entry
    /// per program, the program in front first and the rest alphabetical. Several windows of one
    /// program are one program to a rule, so each name appears once. Refreshed when a picker
    /// opens rather than on the panel's poll - the list is only looked at then, and an
    /// enumeration nobody sees would be work for nothing.
    /// </summary>
    public ObservableCollection<ForegroundWindow> WindowChoices { get; } = [];

    /// <summary>Whether the picker has anything to offer, which is what the panel's hint hides on.</summary>
    public bool HasWindowChoices => WindowChoices.Count > 0;

    /// <summary>Whether the whole table runs. Off keeps the table and stops it applying anything.</summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>The profile the desktop falls back to when no rule matches.</summary>
    [ObservableProperty]
    private int _defaultSlot = ForegroundRule.DefaultSlot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    public bool HasStatusMessage => StatusMessage.Length > 0;

    /// <summary>What the platform can see, and the Wayland limitation where it applies.</summary>
    public string SourceNote => _watcher.SourceNote;

    /// <summary>The program in front as the last poll read it, or null when there is none.</summary>
    public string? ForegroundProcessName => _watcher.ForegroundProcessName;

    public bool HasForegroundProcess => !string.IsNullOrEmpty(ForegroundProcessName);

    public string ForegroundProcessText => HasForegroundProcess
        ? ForegroundProcessName!
        : "无法读取（桌面，或当前平台不支持）";

    /// <summary>
    /// Why the profile that is running is the one running - straight from the rule table. Locked
    /// is quoted too, because a locked profile is one the user chose and the panel has to say so
    /// rather than explaining a rule that is not in charge; while the rules want something else,
    /// that is named as well, so unlocking is never a surprise.
    /// </summary>
    public string ActiveReason
    {
        get
        {
            var applied = _watcher.AppliedDecision;
            var running = applied?.Reason ?? "尚未读取前台程序。";

            if (!_watcher.IsLocked)
            {
                return running;
            }

            var evaluated = _watcher.LastDecision;

            return Equals(applied, evaluated)
                ? $"已锁定：{running}"
                : $"已锁定：{running}（若解锁：{evaluated?.Reason}）";
        }
    }

    /// <summary>
    /// What is running right now - a profile slot, or the native-gamepad pause. The applied
    /// decision rather than the evaluated one: while the profile is locked, the rules' answer is
    /// not what runs, and a panel that quoted it would be lying about the very thing it explains.
    /// </summary>
    public string ActiveProfileText
    {
        get
        {
            var decision = _watcher.AppliedDecision;

            return decision?.Kind switch
            {
                ForegroundDecisionKind.Paused => "已暂停键鼠映射（原生手柄输入）",
                ForegroundDecisionKind.Profile => $"预设 {decision.Slot}",
                _ => $"桌面默认：预设 {decision?.Slot ?? DefaultSlot}",
            };
        }
    }

    /// <summary>
    /// The locked state, in the words the panel shows. Locked is a state the user must never have
    /// to guess about: it names what is frozen and what is standing down.
    /// </summary>
    public string LockStateText => _watcher.IsLocked
        ? "已锁定：前台规则暂停生效，保持当前配置"
        : "未锁定：前台规则正在生效";

    public bool IsLocked => _watcher.IsLocked;

    /// <summary>The lock button's own label, so it never reads as a second switch.</summary>
    public string LockButtonText => _watcher.IsLocked ? "解锁（交还前台规则）" : "锁定当前配置";

    /// <summary>Whether the staged table differs from the one that is running.</summary>
    public bool HasUnsavedChanges => DiffersFromWatcher();

    /// <summary>Whether the staged table can be saved at all: every rule must name a program.</summary>
    public bool HasInvalidRules => Rules.Any(rule => !rule.IsValid);

    public bool CanSave => !HasInvalidRules && HasUnsavedChanges;

    /// <summary>Reads the foreground and re-evaluates the table without waiting for the timer.</summary>
    [RelayCommand]
    private void Refresh()
    {
        _watcher.Refresh();
        SyncFromWatcher();
    }

    /// <summary>
    /// Re-reads the open windows for the picker. Runs when a picker opens - the panel calls it
    /// from the drop-down - and once when this view model is built, so the list is never stale
    /// by more than the time the panel was open. The enumeration is the platform's to answer:
    /// a source that cannot list anything leaves the list empty, which the panel says rather
    /// than leaving a drop-down with nothing in it.
    /// </summary>
    [RelayCommand]
    private void RefreshWindowList()
    {
        // The same notion of "the same program" the rule table matches on, so a platform that
        // reports the foreground as "Chrome.exe" still leads the list to "chrome".
        var foreground = ForegroundRule.NormalizeProcessName(ForegroundProcessName);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var choices = _watcher.ListWindows()
            .Where(window => !string.IsNullOrWhiteSpace(window.ProcessName))
            .OrderBy(window => string.Equals(
                ForegroundRule.NormalizeProcessName(window.ProcessName), foreground, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(window => window.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Where(window => seen.Add(window.ProcessName))
            .ToList();

        WindowChoices.Clear();

        foreach (var choice in choices)
        {
            WindowChoices.Add(choice);
        }

        OnPropertyChanged(nameof(HasWindowChoices));
    }

    /// <summary>
    /// Locks or unlocks the profile in effect. The only control that acts immediately: a user
    /// fighting a rule over the profile should not have to save anything to win.
    /// </summary>
    [RelayCommand]
    private void ToggleLock()
    {
        _watcher.SetLocked(!_watcher.IsLocked);
        SyncFromWatcher();
    }

    [RelayCommand]
    private void AddRule()
    {
        var rule = new ForegroundRuleItem(new ForegroundRule
        {
            ProcessName = string.Empty,
            Priority = Rules.Count == 0 ? 0 : Rules.Max(rule => rule.Priority) + 1,
        })
        {
            // A rule added here starts open: the user is standing in it with the intent to
            // fill it in, and a summary line that has nothing to summarise yet would be a
            // dead end. The rows that come from the saved table stay collapsed on load.
            IsExpanded = true,
        };

        // Subscribed on the way in: a row that changes without telling this view model leaves
        // the derived state - the save button's enabled state above all - stale.
        Add(rule);
        RefreshDerived();
    }

    [RelayCommand]
    private void DeleteRule(ForegroundRuleItem rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        Rules.Remove(rule);
        rule.PropertyChanged -= OnRuleItemPropertyChanged;
        RefreshDerived();
    }

    private void Add(ForegroundRuleItem rule)
    {
        rule.PropertyChanged += OnRuleItemPropertyChanged;
        Rules.Add(rule);
    }

    /// <summary>Writes the staged table out and applies it at once.</summary>
    [RelayCommand]
    private void Save()
    {
        if (HasInvalidRules)
        {
            StatusMessage = "有规则没有填写程序名，请先补全或删除。";
            return;
        }

        var settings = BuildSettings();

        try
        {
            _store.Save(settings);
        }
        catch (IOException exception)
        {
            StatusMessage = $"保存失败：{exception.Message}";
            return;
        }
        catch (UnauthorizedAccessException exception)
        {
            StatusMessage = $"保存失败：{exception.Message}";
            return;
        }

        _watcher.Update(settings);
        StatusMessage = "已保存并生效。";
        SyncFromWatcher();
    }

    /// <summary>Puts the stored table back on screen and applies it, discarding staged edits.</summary>
    [RelayCommand]
    private void Revert()
    {
        LoadFromStore();
        StatusMessage = "已恢复到已保存的规则。";
        SyncFromWatcher();
    }

    private void LoadFromStore()
    {
        var settings = _store.Load();

        if (_store.LastError is { } error)
        {
            StatusMessage = error;
        }

        foreach (var rule in Rules)
        {
            rule.PropertyChanged -= OnRuleItemPropertyChanged;
        }

        Rules.Clear();

        foreach (var rule in settings.Rules)
        {
            Add(new ForegroundRuleItem(rule));
        }

        IsEnabled = settings.Enabled;
        DefaultSlot = settings.DefaultSlot;

        // The stored table is what runs: push it in so a restart resumes the rules the user left.
        _watcher.Update(settings);
        RefreshDerived();
    }

    private void OnWatcherStateChanged() => SyncFromWatcher();

    private void OnRuleItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshDerived();

    private void SyncFromWatcher()
    {
        OnPropertyChanged(nameof(SourceNote));
        OnPropertyChanged(nameof(ForegroundProcessName));
        OnPropertyChanged(nameof(HasForegroundProcess));
        OnPropertyChanged(nameof(ForegroundProcessText));
        OnPropertyChanged(nameof(ActiveReason));
        OnPropertyChanged(nameof(ActiveProfileText));
        OnPropertyChanged(nameof(LockStateText));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(LockButtonText));
        RefreshDerived();
    }

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(HasInvalidRules));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private ForegroundSettings BuildSettings() => new()
    {
        Rules = [.. Rules.Select(rule => rule.ToRule())],
        DefaultSlot = DefaultSlot,
        Enabled = IsEnabled,
    };

    /// <summary>
    /// Whether the staged table is the one running. Compared rule by rule rather than by
    /// serializing both sides: the table is tiny, and the identities must line up anyway for a
    /// "nothing to save" answer to mean anything.
    /// </summary>
    private bool DiffersFromWatcher()
    {
        var running = _watcher.Settings;

        if (running.Enabled != IsEnabled || running.DefaultSlot != DefaultSlot)
        {
            return true;
        }

        var staged = BuildSettings().Rules;

        if (staged.Count != running.Rules.Count)
        {
            return true;
        }

        for (var i = 0; i < staged.Count; i++)
        {
            if (!Equals(staged[i], running.Rules[i]))
            {
                return true;
            }
        }

        return false;
    }

    partial void OnDefaultSlotChanged(int value)
    {
        // Slots are numbered 1 and up. Re-assigning re-enters this hook in range, so the derived
        // state is refreshed once with the clamped value.
        if (value < ForegroundRule.DefaultSlot)
        {
            DefaultSlot = ForegroundRule.DefaultSlot;
            return;
        }

        RefreshDerived();
    }

    partial void OnIsEnabledChanged(bool value) => RefreshDerived();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _watcher.StateChanged -= OnWatcherStateChanged;

        foreach (var rule in Rules)
        {
            rule.PropertyChanged -= OnRuleItemPropertyChanged;
        }

        GC.SuppressFinalize(this);
    }
}
