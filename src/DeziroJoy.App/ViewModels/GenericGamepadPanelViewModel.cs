using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// The data-driven gamepad panel: one item per control the device declares, arranged in the
/// Core's five fixed zones, with the display strategy the surface promises:
/// <para>
/// Bound controls are always on the panel. Controls the device has actually reported are on
/// the panel and read as live - the latch is one-way, so releasing a control never sends it
/// back to 更多输入. Controls that are declared but never detected wait in 更多输入, in their
/// fixed order, until 显示全部输入 brings them up. A control the user hid leaves the panel
/// and is restorable - hiding is display-only and never touches a mapping.
/// </para>
/// <para>
/// Live input paints the highlight, latches detection and, while the editor's button
/// capture is armed, marks the control being captured for. A stick carries two independent
/// switches: how it is drawn (appearance, which changes nothing about mappings) and how it
/// executes (mode, rewritten through <see cref="RequestStickModeChange"/>, which the window
/// owns). Nothing about the device picks either.
/// </para>
/// </summary>
public sealed partial class GenericGamepadPanelViewModel : ObservableObject
{
    public GenericGamepadPanelViewModel()
    {
        Zones =
        [
            DirectionItems,
            MainButtonItems,
            ShoulderItems,
            FunctionItems,
            ExtensionItems,
        ];
        MoreInputItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(MoreInputCount));
    }

    /// <summary>Every control, in the device's declaration order - the panel's source of truth.</summary>
    public ObservableCollection<GenericControlItem> Items { get; } = [];

    /// <summary>
    /// The zone collections the view arranges, in the Core's fixed zone order - the same
    /// order <see cref="GamepadControlSet.ZoneOrder"/> defines, so the panel and the Core
    /// cannot disagree about where a control sits.
    /// </summary>
    public IReadOnlyList<ObservableCollection<GenericControlItem>> Zones { get; }

    public ObservableCollection<GenericControlItem> DirectionItems { get; } = [];

    public ObservableCollection<GenericControlItem> MainButtonItems { get; } = [];

    public ObservableCollection<GenericControlItem> ShoulderItems { get; } = [];

    public ObservableCollection<GenericControlItem> FunctionItems { get; } = [];

    public ObservableCollection<GenericControlItem> ExtensionItems { get; } = [];

    /// <summary>Controls declared but never detected, waiting in 更多输入.</summary>
    public ObservableCollection<GenericControlItem> MoreInputItems { get; } = [];

    public int MoreInputCount => MoreInputItems.Count;

    public int HiddenCount { get; private set; }

    /// <summary>Whether anything is hidden right now - what the 恢复 affordance hangs off.</summary>
    public bool HasHidden => HiddenCount > 0;

    private GenericControlItem? _editorControl;

    /// <summary>
    /// The control the mapping editor is editing, pushed in by the window while the editor
    /// is open. The per-control actions that are not "open the settings" - hide, and for a
    /// stick the appearance and execution-mode switches - act on it from the bar below the
    /// zones instead of crowding every card: the cards stay one even grid of single buttons
    /// with no badge column to misalign them, and the actions stay reachable by pointer and
    /// by gamepad navigation alike, which a hover-revealed badge would not be.
    /// </summary>
    public GenericControlItem? EditorControl
    {
        get => _editorControl;
        private set
        {
            if (ReferenceEquals(_editorControl, value))
            {
                return;
            }

            _editorControl = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasEditorControl));
            OnPropertyChanged(nameof(EditorControlCanHide));
            OnPropertyChanged(nameof(EditorControlIsStick));
        }
    }

    /// <summary>Whether the editor has a control on this panel open right now.</summary>
    public bool HasEditorControl => _editorControl is not null;

    /// <summary>Whether the open control may be hidden - a bound one may not, nor may one already hidden.</summary>
    public bool EditorControlCanHide => _editorControl is { CanHide: true, IsUserHidden: false };

    /// <summary>Whether the open control is a stick, which is what the two switches apply to.</summary>
    public bool EditorControlIsStick => _editorControl is { IsStick: true };

    /// <summary>
    /// Points the bar's contextual actions at one control, or at none when the editor
    /// closes. The window calls this as the editor's source moves; the panel never guesses
    /// which control is being edited.
    /// </summary>
    public void SetEditorControl(GenericControlItem? item) => EditorControl = item;

    [ObservableProperty]
    private bool _showAllInputs;

    /// <summary>What the 显示全部输入 affordance says, and what pressing it will do.</summary>
    public string ShowAllLabel => ShowAllInputs ? "收起更多输入" : "显示全部输入";

    /// <summary>
    /// Whether the panel has anything to show at all. An empty declaration - a device whose
    /// platform has written no description - leaves the panel with no items, which is what
    /// keeps the hand-drawn model as the surface for those backends.
    /// </summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>
    /// Rewrites one stick's mappings between continuous and four-way. Set by the window, which
    /// owns the profile; returning false leaves the panel's mode where it was.
    /// </summary>
    public Func<GenericControlItem, GenericStickMode, Task<bool>>? RequestStickModeChange { get; set; }

    /// <summary>Set by the window: opens the editor for the control's source.</summary>
    public Action<GenericControlItem>? OpenControlRequested { get; set; }

    /// <summary>
    /// Opens the mapping settings for one control. The item's own command calls this, so a
    /// card press from the view and a programmatic open share one path.
    /// </summary>
    private void OpenControl(GenericControlItem item) => OpenControlRequested?.Invoke(item);

    /// <summary>
    /// Brings the panel to the device's declaration, reusing what it already holds. An item
    /// the declaration still names - same physical source, same name, kind and zone - keeps
    /// its detection latch, its hide, its stick's two switches and its capture mark, and
    /// with them the view's container: a daemon snapshot that re-describes the same device
    /// must not rebuild the panel under the user. Only a control the declaration gained,
    /// lost or re-named moves, and the order is the declaration's exactly.
    /// <paramref name="isBound"/> answers for each source whether the active profile
    /// already maps it, which is what decides the always-shown half of the display
    /// strategy.
    /// </summary>
    public void Load(IReadOnlyList<GamepadControlDescriptor> descriptors, Func<InputBinding, bool> isBound)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(isBound);

        SyncItems(descriptors, isBound, rederiveBound: true);
    }

    /// <summary>
    /// Brings the panel's items in step with a declaration that changed underneath it - a
    /// profile that just bound a source the device never declared, or dropped the last
    /// mapping for one. An item already on the panel keeps everything it holds: the
    /// detection latch, the hide, a stick's two switches. Only what the declaration gained
    /// or lost moves, so a commit never re-arranges the panel and never re-latches it.
    /// Identity is physical - see <see cref="SameSource"/> - so the comparison cannot be
    /// fooled by a direction or a gesture a mapping carries.
    /// </summary>
    public void SyncDeclarations(IReadOnlyList<GamepadControlDescriptor> descriptors, Func<InputBinding, bool> isBound)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(isBound);

        if (descriptors.Count == 0)
        {
            // An empty declaration is the disconnect's business - the caller empties the
            // panel there - never something a profile change gets to decide.
            return;
        }

        SyncItems(descriptors, isBound, rederiveBound: false);
    }

    /// <summary>
    /// The shared item sync behind <see cref="Load"/> and <see cref="SyncDeclarations"/>.
    /// The declaration is walked in order and each control's existing item is pulled to
    /// its position - an item already where it belongs never moves, so the view keeps its
    /// container - and whatever the declaration no longer names falls off the end.
    /// </summary>
    private void SyncItems(
        IReadOnlyList<GamepadControlDescriptor> descriptors,
        Func<InputBinding, bool> isBound,
        bool rederiveBound)
    {
        for (var index = 0; index < descriptors.Count; index++)
        {
            var descriptor = descriptors[index];
            var current = IndexOfControl(descriptor);
            if (current < 0)
            {
                Items.Insert(index, CreateItem(descriptor, isBound));
            }
            else if (current != index)
            {
                Items.Move(current, index);
            }
        }

        for (var i = Items.Count - 1; i >= descriptors.Count; i--)
        {
            Items.RemoveAt(i);
        }

        if (rederiveBound)
        {
            foreach (var item in Items)
            {
                item.IsBound = isBound(item.Source);
            }
        }

        OnPropertyChanged(nameof(HasItems));
        Rebucket();
    }

    /// <summary>Where the declaration's control already sits in the items, or -1.</summary>
    private int IndexOfControl(GamepadControlDescriptor descriptor)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (SameControl(Items[i], descriptor))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether one item is the declaration's own control: the same physical source, named
    /// the same way, of the same kind, in the same zone. A re-named control is a new item -
    /// the panel shows what the device says, not what it used to say.
    /// </summary>
    private static bool SameControl(GenericControlItem item, GamepadControlDescriptor descriptor)
        => SameSource(item.Source, descriptor.Source)
            && item.Label == descriptor.Label
            && item.Kind == descriptor.Kind
            && item.Zone == descriptor.Zone;

    /// <summary>
    /// One panel item from one declaration, wired to the panel's own commands: a card, its
    /// hide affordance and a stick's two switches all land in the same implementation
    /// whichever surface they are pressed from.
    /// </summary>
    private GenericControlItem CreateItem(GamepadControlDescriptor descriptor, Func<InputBinding, bool> isBound)
    {
        var item = new GenericControlItem(descriptor, isBound(descriptor.Source))
        {
            // A reload keeps whatever the panel's 显示全部输入 flag says, so promoting
            // the 更多输入 controls survives a device re-declaration.
            PanelShowsEverything = ShowAllInputs,
        };

        item.OpenRequested = _ => OpenControl(item);
        item.HideRequested = _ => Hide(item);
        item.StickModeRequested = candidate => SetStickMode(candidate);
        item.AppearanceRequested = SwitchAppearance;
        return item;
    }

    /// <summary>
    /// The item that stands for one physical control, or null when the panel has none.
    /// Identity is physical - see <see cref="SameSource"/> - so a direction or a gesture a
    /// mapping configures on the control still finds the card the panel drew for it, which
    /// is what the connector line and the capture mark both ask for.
    /// </summary>
    public GenericControlItem? ItemFor(InputBinding source)
        => Items.FirstOrDefault(item => SameSource(item.Source, source));

    /// <summary>
    /// Re-derives every item's bound state from the profile. A control that just gained its
    /// first mapping joins the always-shown half of the panel; one that lost its last moves
    /// to 更多输入 if the device has not detected it. The mappings themselves are untouched -
    /// this is the display strategy following the profile, not an edit.
    /// </summary>
    public void ApplyBindings(Func<InputBinding, bool> isBound)
    {
        ArgumentNullException.ThrowIfNull(isBound);

        foreach (var item in Items)
        {
            item.IsBound = isBound(item.Source);
        }

        Rebucket();
    }

    /// <summary>
    /// Puts every stick item's mode selector in step with the mappings the profile actually
    /// carries: four direction mappings read as 四方向, an omnidirectional one as 连续轴.
    /// The selector follows the mappings, so a reload from the daemon - or another client's
    /// edit - shows the mode really in effect rather than the one this window last picked.
    /// </summary>
    public void SyncStickModes(IReadOnlyList<MappingEntry> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        foreach (var item in Items)
        {
            if (item.Stick is not { } stick)
            {
                continue;
            }

            var fourWay = mappings.Any(entry =>
                entry.Source is StickBinding { Direction: { } } binding && binding.Stick == stick);
            item.StickMode = fourWay ? GenericStickMode.FourWay : GenericStickMode.Continuous;
        }
    }

    /// <summary>
    /// Paints the live highlight from one sample and latches detection. A control reads as
    /// held while its source is active - its button in the pressed mask, its stick deflected
    /// past the preview's enter deadzone (the same one the hand-drawn model uses, so the two
    /// surfaces agree on where a push starts to count), or its extra code in the sample's
    /// extra set - and the first time it does, the control is latched as detected. The latch
    /// is one-way: releasing the control, or a snapshot that no longer lists it, leaves the
    /// control on the panel; only <see cref="Clear"/> takes it back.
    /// </summary>
    public void Update(IGamepadState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        foreach (var item in Items)
        {
            var active = IsActive(item.Source, state);
            item.IsPressed = active;

            if (active)
            {
                item.LatchDetected();
            }
        }

        Rebucket();
    }

    /// <summary>
    /// Clears the live highlight and the detection latches - a disconnect, where every
    /// control the device had reported stops being reported and the panel starts fresh for
    /// the next device.
    /// </summary>
    public void Clear()
    {
        foreach (var item in Items)
        {
            item.IsPressed = false;
            item.IsDetected = false;
            item.IsCaptureTarget = false;
        }

        Rebucket();
    }

    /// <summary>
    /// Mirrors the editor's button capture: the control being captured for is marked, so the
    /// panel shows the target while the user reaches for the button. A null source - capture
    /// disarmed - clears every mark.
    /// </summary>
    public void UpdateCapture(bool isCapturing, InputBinding? target)
    {
        foreach (var item in Items)
        {
            item.IsCaptureTarget = isCapturing && target is not null && SameSource(target, item.Source);
        }
    }

    [RelayCommand]
    private void Hide(GenericControlItem item)
    {
        if (!item.CanHide)
        {
            return;
        }

        item.IsUserHidden = true;
        Rebucket();
    }

    /// <summary>Brings every user-hidden control back. Restoring is display-only, as hiding was.</summary>
    [RelayCommand]
    private void RestoreHidden()
    {
        var any = false;
        foreach (var item in Items)
        {
            if (item.IsUserHidden)
            {
                item.IsUserHidden = false;
                any = true;
            }
        }

        if (any)
        {
            Rebucket();
        }
    }

    [RelayCommand]
    private void ToggleShowAll()
    {
        ShowAllInputs = !ShowAllInputs;
        foreach (var item in Items)
        {
            item.PanelShowsEverything = ShowAllInputs;
            item.RefreshDisplayFlags();
        }

        OnPropertyChanged(nameof(ShowAllLabel));
        Rebucket();
    }

    [RelayCommand]
    private async Task SetStickMode(GenericControlItem item)
    {
        if (!item.IsStick)
        {
            return;
        }

        var next = item.StickMode == GenericStickMode.Continuous
            ? GenericStickMode.FourWay
            : GenericStickMode.Continuous;

        var previous = item.StickMode;
        item.StickMode = next;

        // The rewrite is the window's: a refusal - a daemon that said no, a profile that
        // failed to reload - puts the selector back where it was rather than leaving the
        // panel claiming a mode the mappings do not carry.
        if (RequestStickModeChange is { } request && !await request(item, next))
        {
            item.StickMode = previous;
        }
    }

    /// <summary>
    /// Switches how a control is drawn. Appearance only: no mapping is read or written, and
    /// the execution mode beside it does not move - the two switches answer different
    /// questions and this one answers neither of them.
    /// </summary>
    private void SwitchAppearance(GenericControlItem item)
    {
        if (!item.IsStick)
        {
            return;
        }

        item.StickAppearance = item.StickAppearance == GenericStickAppearance.Stick
            ? GenericStickAppearance.Dpad
            : GenericStickAppearance.Stick;
    }

    /// <summary>
    /// Re-buckets every item into its zone, the 更多输入 wait, or nowhere (hidden). Order
    /// within each bucket is the declaration order, so detection and hiding can never
    /// re-arrange the panel.
    /// <para>
    /// The move is incremental: a bucket whose membership a sample did not change is left
    /// untouched, so a live highlight paints on the card the user is already looking at
    /// rather than rebuilding every card at sample rate. Only the items whose state actually
    /// moved are removed and re-inserted, each at its declaration-order position.
    /// </para>
    /// </summary>
    private void Rebucket()
    {
        var hidden = 0;
        var buckets = new (ObservableCollection<GenericControlItem> Items, GamepadControlZone Zone)[]
        {
            (DirectionItems, GamepadControlZone.Directions),
            (MainButtonItems, GamepadControlZone.MainButtons),
            (ShoulderItems, GamepadControlZone.Shoulders),
            (FunctionItems, GamepadControlZone.Functions),
            (ExtensionItems, GamepadControlZone.Extensions),
        };

        var waiting = new List<GenericControlItem>();
        var placed = new List<GenericControlItem>[buckets.Length];
        for (var i = 0; i < placed.Length; i++)
        {
            placed[i] = [];
        }

        foreach (var item in Items)
        {
            if (item.IsUserHidden)
            {
                hidden++;
                continue;
            }

            if (item.IsInMoreInputs)
            {
                waiting.Add(item);
                continue;
            }

            if (!item.IsShown)
            {
                continue;
            }

            for (var i = 0; i < buckets.Length; i++)
            {
                if (item.Zone == buckets[i].Zone)
                {
                    placed[i].Add(item);
                    break;
                }
            }
        }

        for (var i = 0; i < buckets.Length; i++)
        {
            ApplyBucket(buckets[i].Items, placed[i]);
        }

        ApplyBucket(MoreInputItems, waiting);

        HiddenCount = hidden;
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasHidden));
        OnPropertyChanged(nameof(MoreInputCount));
    }

    /// <summary>
    /// Brings one bucket to the membership <paramref name="desired"/> names, in that order,
    /// touching only what differs: what should not be there is removed, and what should is
    /// moved or inserted at its position. An item that is already where it belongs keeps its
    /// place - and with it the view's container, the live highlight and the user's pointer.
    /// </summary>
    private static void ApplyBucket(
        ObservableCollection<GenericControlItem> target,
        List<GenericControlItem> desired)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var index = 0; index < desired.Count; index++)
        {
            var item = desired[index];
            var current = target.IndexOf(item);
            if (current < 0)
            {
                target.Insert(index, item);
            }
            else if (current != index)
            {
                target.Move(current, index);
            }
        }
    }

    private static bool IsActive(InputBinding source, IGamepadState state)
    {
        switch (source)
        {
            case ButtonBinding button:
                return button.Button == GamepadButton.Extra
                    ? state.ExtraButtons.Contains(button.Code)
                    : state.PressedButtons.Has(button.Button);
            case StickBinding stick:
                var (x, y) = stick.Stick == GamepadStick.Left
                    ? (state.LeftStickX, state.LeftStickY)
                    : (state.RightStickX, state.RightStickY);
                var magnitude = Math.Sqrt((x * x) + (y * y));
                return magnitude >= GamepadVisualizerViewModel.PreviewEnterDeadZone;
            default:
                return false;
        }
    }

    /// <summary>
    /// Physical identity, so a direction or a gesture on the same control still matches: two
    /// bindings of the same stick are the same control whatever direction each carries.
    /// </summary>
    private static bool SameSource(InputBinding left, InputBinding right)
    {
        if (left is ButtonBinding leftButton && right is ButtonBinding rightButton)
        {
            return leftButton.Button == rightButton.Button && leftButton.Code == rightButton.Code;
        }

        if (left is StickBinding leftStick && right is StickBinding rightStick)
        {
            return leftStick.Stick == rightStick.Stick;
        }

        return false;
    }
}
