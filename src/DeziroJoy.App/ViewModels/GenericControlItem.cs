using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// How a stick control is DRAWN on the panel. Appearance and execution are two different
/// questions and the panel keeps them apart: this says which glyph the control wears, and
/// nothing about what a mapping for it does. A stick drawn as a cross still maps the
/// continuous axis, and a stick drawn as a stick still maps four directions if the user
/// chose that - the execution choice is <see cref="GenericStickMode"/>.
/// </summary>
public enum GenericStickAppearance
{
    /// <summary>The stick glyph: a round pad, the way an analog stick reads.</summary>
    Stick,

    /// <summary>The cross glyph: the d-pad shape, for a pad whose stick reads as directions.</summary>
    Dpad,
}

/// <summary>
/// How a stick control executes. The display never decides this and neither does the
/// device: the user picks, explicitly, per stick. <see cref="Continuous"/> is one
/// omnidirectional mapping - what a mouse-move or wheel target consumes;
/// <see cref="FourWay"/> is four direction mappings, one per cardinal, which is what a
/// digital target needs. Converting between them rewrites the mappings, it does not
/// reinterpret them, so nothing switches on its own because of a model name or an axis
/// resting near an edge.
/// </summary>
public enum GenericStickMode
{
    Continuous,
    FourWay,
}

/// <summary>
/// One control on the generic panel: its fixed identity from the device's declaration, its
/// live state, and the commands the surface offers - open the mapping settings for it,
/// switch how a stick is drawn, and switch how a stick executes. The display strategy is
/// derived, never stored separately:
/// <para>
/// A bound control is always shown - hiding is refused for it, because a mapping the user
/// made must stay reachable. A control the device has actually reported at least once is
/// shown and reads as live; the latch is deliberately one-way, so releasing the control -
/// or a later snapshot that no longer lists it - never sends it back to 更多输入. A control
/// that is declared but has never been detected waits in 更多输入, reachable through
/// 显示全部输入, in its fixed order. A user-hidden control leaves the panel entirely and is
/// restorable; hiding never touches the mapping behind it.
/// </para>
/// </summary>
public sealed partial class GenericControlItem : ObservableObject
{
    public GenericControlItem(GamepadControlDescriptor descriptor, bool isBound)
    {
        Kind = descriptor.Kind;
        Zone = descriptor.Zone;
        Label = descriptor.Label;
        Source = descriptor.Source;
        _isBound = isBound;

        // A stick starts on the stick glyph; a cross-drawn pad is the user's to pick.
        _stickAppearance = GenericStickAppearance.Stick;
    }

    /// <summary>What kind of input the device declares this control to be.</summary>
    public GamepadControlKind Kind { get; }

    /// <summary>Which fixed region of the panel the device's declaration puts it in.</summary>
    public GamepadControlZone Zone { get; }

    public string Label { get; }

    /// <summary>The source a mapping for this control carries - what a click opens the editor for.</summary>
    public InputBinding Source { get; }

    /// <summary>Whether this control is a two-axis stick, which is what the mode switch applies to.</summary>
    public bool IsStick => Kind == GamepadControlKind.Direction;

    /// <summary>The stick this item stands for, when it is one; null for the binary controls.</summary>
    public GamepadStick? Stick => Source is StickBinding stick ? stick.Stick : null;

    [ObservableProperty]
    private bool _isBound;

    /// <summary>
    /// Whether the device has actually reported this control at least once since the last
    /// clear. A one-way latch: a control nobody has touched yet stays in 更多输入 until a
    /// real sample says otherwise, and once a sample has said so nothing takes it back -
    /// releasing the control, or a snapshot that no longer lists it, is not evidence the
    /// control vanished.
    /// </summary>
    [ObservableProperty]
    private bool _isDetected;

    /// <summary>Whether the control is physically held in the latest sample - the live highlight.</summary>
    [ObservableProperty]
    private bool _isPressed;

    /// <summary>
    /// Whether the user hid this control. Display-only by construction: it moves the item out
    /// of the panel and a restore brings it back, and the mapping behind it is never touched.
    /// A bound control refuses to be hidden - see <see cref="CanHide"/>.
    /// </summary>
    [ObservableProperty]
    private bool _isUserHidden;

    /// <summary>Whether the editor's button capture is armed for this control right now.</summary>
    [ObservableProperty]
    private bool _isCaptureTarget;

    [ObservableProperty]
    private GenericStickAppearance _stickAppearance;

    [ObservableProperty]
    private GenericStickMode _stickMode = GenericStickMode.Continuous;

    /// <summary>How the control is drawn, as the appearance switch words it.</summary>
    public string StickAppearanceLabel => StickAppearance switch
    {
        GenericStickAppearance.Stick => "摇杆示意",
        GenericStickAppearance.Dpad => "十字方向示意",
        _ => StickAppearance.ToString(),
    };

    /// <summary>Whether the card draws the round stick glyph.</summary>
    public bool IsStickGlyph => StickAppearance == GenericStickAppearance.Stick;

    /// <summary>Whether the card draws the cross glyph.</summary>
    public bool IsDpadGlyph => StickAppearance == GenericStickAppearance.Dpad;

    /// <summary>How the stick executes, as the mode selector words it.</summary>
    public string StickModeLabel => StickMode switch
    {
        GenericStickMode.Continuous => "连续轴",
        GenericStickMode.FourWay => "四方向",
        _ => StickMode.ToString(),
    };

    /// <summary>
    /// Set by the panel: whether 显示全部输入 is on, which is what promotes the 更多输入
    /// controls onto the panel. A plain property because the panel owns the flag and pushes
    /// it to every item at once rather than letting each item hold a second copy of it.
    /// </summary>
    public bool PanelShowsEverything { get; internal set; }

    /// <summary>
    /// Whether this control takes a slot on the panel proper. Bound controls are always
    /// shown; detected ones are shown; everything else waits in 更多输入 unless the panel is
    /// showing everything. A user-hidden control is off the panel either way.
    /// </summary>
    public bool IsShown => !IsUserHidden && (IsBound || IsDetected || PanelShowsEverything);

    /// <summary>
    /// Whether this control waits in 更多输入: declared, but never detected and not bound,
    /// and not hidden. 显示全部输入 moves these onto the panel instead of collapsing them.
    /// </summary>
    public bool IsInMoreInputs => !IsUserHidden && !IsBound && !IsDetected && !PanelShowsEverything;

    /// <summary>Bound controls cannot be hidden: a mapping the user made stays reachable.</summary>
    public bool CanHide => !IsBound;

    /// <summary>
    /// Whether the control reads as live: detected at least once, or held right now. This is
    /// the highlight the panel paints - the device's own answer about the control, not a
    /// guess from the layout.
    /// </summary>
    public bool IsLive => IsDetected || IsPressed;

    /// <summary>Set by the panel: opens the mapping settings for this control.</summary>
    public Action<GenericControlItem>? OpenRequested { get; internal set; }

    /// <summary>Set by the panel: hides this control - display only, never a mapping.</summary>
    public Action<GenericControlItem>? HideRequested { get; internal set; }

    /// <summary>Set by the panel: switches this stick's execution mode.</summary>
    public Func<GenericControlItem, Task>? StickModeRequested { get; internal set; }

    /// <summary>Set by the panel: switches how this control is drawn.</summary>
    public Action<GenericControlItem>? AppearanceRequested { get; internal set; }

    /// <summary>Opens the mapping settings for this control, as a click on its card does.</summary>
    [RelayCommand]
    private void Open() => OpenRequested?.Invoke(this);

    /// <summary>Hides this control from the panel; a bound control's command is a no-op.</summary>
    [RelayCommand]
    private void Hide() => HideRequested?.Invoke(this);

    /// <summary>Switches this stick between the continuous axis and the four directions.</summary>
    [RelayCommand]
    private Task SwitchStickMode() => StickModeRequested?.Invoke(this) ?? Task.CompletedTask;

    /// <summary>
    /// Switches how this control is drawn - the stick glyph or the cross. Appearance only:
    /// no mapping is read or written, and the execution mode beside it does not move.
    /// </summary>
    [RelayCommand]
    private void SwitchAppearance() => AppearanceRequested?.Invoke(this);

    /// <summary>
    /// Latches the control as detected. One-way by design: only a disconnect clears it,
    /// because "never pressed yet" and "no longer there" are different facts and only the
    /// device going away proves the second.
    /// </summary>
    internal void LatchDetected()
    {
        if (IsDetected)
        {
            return;
        }

        IsDetected = true;
    }

    partial void OnIsDetectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(IsInMoreInputs));
        OnPropertyChanged(nameof(IsLive));
    }

    partial void OnIsPressedChanged(bool value) => OnPropertyChanged(nameof(IsLive));

    partial void OnIsUserHiddenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(IsInMoreInputs));
    }

    /// <summary>
    /// Re-raises the derived display flags. The panel calls this when it pushes its
    /// 显示全部输入 flag to every item, because the flag is the panel's and each item holds
    /// only a copy of it.
    /// </summary>
    internal void RefreshDisplayFlags()
    {
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(IsInMoreInputs));
    }

    partial void OnStickAppearanceChanged(GenericStickAppearance value)
    {
        OnPropertyChanged(nameof(StickAppearanceLabel));
        OnPropertyChanged(nameof(IsStickGlyph));
        OnPropertyChanged(nameof(IsDpadGlyph));
    }

    partial void OnStickModeChanged(GenericStickMode value)
        => OnPropertyChanged(nameof(StickModeLabel));
}
