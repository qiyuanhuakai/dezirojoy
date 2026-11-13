using CommunityToolkit.Mvvm.ComponentModel;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// What one gesture slot does. The three states a gesture action can be in: no function of
/// its own (the legacy null, which re-fires the click), 无功能, or a real target.
/// </summary>
public enum GestureSlotFunction
{
    /// <summary>重复单击的功能: no function of its own; the gesture re-fires the click.</summary>
    RepeatClick,

    /// <summary>无功能: the gesture resolves and emits nothing.</summary>
    None,

    /// <summary>A real target built from the shared editable fields.</summary>
    Target,
}

/// <summary>
/// The draft for one gesture slot. The click, the double-click and the long-press are equal
/// peers, so every slot is edited through the same fields: a target kind, a key, modifiers,
/// a mouse button and the two continuous settings, plus 无功能 and the legacy
/// repeat-the-click state. Three independent drafts live in the editor, so switching between
/// slots never loses an edit.
/// </summary>
/// <remarks>
/// A null action and a <see cref="NoneTarget"/> are different states and both round-trip:
/// null is the legacy shape every profile written before gesture actions existed has, and
/// 无功能 is an explicit choice that must survive a save and a reload unchanged.
/// </remarks>
public partial class GestureSlotEditor : ObservableObject
{
    /// <summary>
    /// The script a fresh 复杂快捷键 draft starts from: the mission's own example, which
    /// parses, so a newly chosen kind opens on a working program instead of an empty box.
    /// </summary>
    public const string ExampleScript = "hold Alt\nevery 1s tap Tab";

    /// <summary>
    /// The longest string a 输入文本 draft may carry, the Core's own bound. The editor
    /// clamps to it rather than refusing, so a paste that is too long trims instead of
    /// failing the whole draft.
    /// </summary>
    public const int TextMaxLength = TextTarget.MaxLength;

    /// <summary>
    /// The layer a fresh Layer 键 draft enters. Layer 0 is the base layer every mapping
    /// already runs, so a layer key names 1 or above.
    /// </summary>
    public const int FirstLayerId = 1;

    [ObservableProperty]
    private MappingTargetKind _targetKind = MappingTargetKind.KeyPress;

    /// <summary>
    /// The string a <see cref="MappingTargetKind.Text"/> target types on the press edge.
    /// Short by design - a search query, a file name, a URL - and bounded by
    /// <see cref="TextMaxLength"/>.
    /// </summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>
    /// The DSL source of a <see cref="MappingTargetKind.ComplexShortcut"/> target, one step
    /// per line. The text is the single source of truth: the editor validates it live and
    /// the engine compiles it when the mapping is built, so the draft carries nothing else.
    /// </summary>
    [ObservableProperty]
    private string _script = ExampleScript;

    /// <summary>
    /// What starts the program. The slot it sits in decides which triggers are offered:
    /// the click reads 单击/松开, the double-click 双击, the long press 长按 - a program only
    /// ever runs from the position that carries it.
    /// </summary>
    [ObservableProperty]
    private ActionTrigger _trigger = ComplexShortcutTarget.DefaultTrigger;

    /// <summary>How long the program keeps running once it has started.</summary>
    [ObservableProperty]
    private ActionExecution _execution = ComplexShortcutTarget.DefaultExecution;

    /// <summary>What a second trigger does while the program is already running.</summary>
    [ObservableProperty]
    private ActionRepeat _repeat = ComplexShortcutTarget.DefaultRepeat;

    [ObservableProperty]
    private GamepadKey _selectedKey = GamepadKey.Enter;

    [ObservableProperty]
    private bool _modifierCtrl;

    [ObservableProperty]
    private bool _modifierShift;

    [ObservableProperty]
    private bool _modifierAlt;

    [ObservableProperty]
    private bool _modifierWin;

    [ObservableProperty]
    private MouseButton _selectedMouseButton = MouseButton.Left;

    [ObservableProperty]
    private MouseStickSettings _mouseSettings = new();

    [ObservableProperty]
    private ScrollStickSettings _scrollSettings = new();

    /// <summary>
    /// Which special behavior a <see cref="MappingTargetKind.SpecialKey"/> target triggers.
    /// The defaults are the Core record's own, so a freshly chosen special key builds the
    /// same target a profile written with the defaults carries.
    /// </summary>
    [ObservableProperty]
    private SpecialKeyKind _specialKind = SpecialKeyKind.Precision;

    [ObservableProperty]
    private PrecisionMode _specialPrecisionMode = PrecisionMode.Toggle;

    [ObservableProperty]
    private LeaderSwitchMode _specialSwitchMode = LeaderSwitchMode.Press;

    [ObservableProperty]
    private int _specialSlotA = 1;

    [ObservableProperty]
    private int _specialSlotB = 2;

    /// <summary>
    /// Which temporary layer a <see cref="SpecialKeyKind.Layer"/> target enters. 0 - the
    /// default of the Core record - is the base layer, which a layer key must not name, so a
    /// draft starts on the first layer instead. It is meaningless for the other kinds.
    /// </summary>
    [ObservableProperty]
    private int _specialLayerId = FirstLayerId;

    /// <summary>
    /// Which of the three states the slot is in. Editing any target field gives the slot a
    /// function of its own, so a slot that was re-firing the click becomes a real target.
    /// </summary>
    [ObservableProperty]
    private GestureSlotFunction _function = GestureSlotFunction.Target;

    /// <summary>无功能: the slot resolves and emits nothing. Explicit, never a silent null.</summary>
    public bool IsNone
    {
        get => Function == GestureSlotFunction.None;
        set => Function = value ? GestureSlotFunction.None : GestureSlotFunction.Target;
    }

    private UnknownTarget? _unsupported;

    /// <summary>
    /// A target this build does not know, kept exactly as the profile carries it. A profile
    /// written by a newer build names a <c>$type</c> no type here claims; showing it as an
    /// ordinary key press would be a lie, and dropping it on the next save would lose what
    /// the other build put there. The draft holds it and builds it back out untouched until
    /// the user picks something else, which is the one thing that replaces it.
    /// </summary>
    /// <remarks>
    /// The change is announced rather than written silently. A load can land an unknown
    /// target on a draft whose every other field already sits where it would, so without
    /// the notification nothing downstream learns the draft has become one this build
    /// cannot read - the editor's unsupported banner included.
    /// </remarks>
    public UnknownTarget? Unsupported
    {
        get => _unsupported;
        private set
        {
            if (!Equals(value, _unsupported))
            {
                _unsupported = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// How a loaded unknown target reads, for the header and the row that shows the draft:
    /// an unsupported mapping that names its discriminator, never a debug dump.
    /// </summary>
    public string? UnsupportedDescription => Unsupported?.ToString();

    /// <summary>重复单击的功能: the slot has no function of its own and re-fires the click.</summary>
    public bool RepeatsClick
    {
        get => Function == GestureSlotFunction.RepeatClick;
        set => Function = value ? GestureSlotFunction.RepeatClick : GestureSlotFunction.Target;
    }

    public KeyModifiers Modifiers
    {
        get
        {
            var modifiers = KeyModifiers.None;
            if (ModifierCtrl)
            {
                modifiers |= KeyModifiers.Ctrl;
            }

            if (ModifierShift)
            {
                modifiers |= KeyModifiers.Shift;
            }

            if (ModifierAlt)
            {
                modifiers |= KeyModifiers.Alt;
            }

            if (ModifierWin)
            {
                modifiers |= KeyModifiers.Win;
            }

            return modifiers;
        }
    }

    /// <summary>
    /// The target the fields describe, 无功能 included. Never null: a slot re-firing the
    /// click has no target of its own, and its callers ask <see cref="BuildAction"/> instead.
    /// A draft loaded from an unknown target builds that target back out verbatim, so a
    /// commit never rewrites what this build cannot read.
    /// </summary>
    public MappingTarget BuildTarget()
    {
        if (Unsupported is { } unsupported)
        {
            return unsupported;
        }

        return Function == GestureSlotFunction.None
            ? new NoneTarget()
            : TargetKind switch
            {
                MappingTargetKind.KeyPress => new KeyPressTarget(SelectedKey),
                MappingTargetKind.KeyCombo => new KeyComboTarget(Modifiers, SelectedKey),
                MappingTargetKind.MouseButton => new MouseButtonTarget(SelectedMouseButton),
                MappingTargetKind.MouseMove => new MouseMoveTarget(MouseSettings),
                MappingTargetKind.WheelScroll => new WheelScrollTarget(ScrollSettings),
                MappingTargetKind.Text => new TextTarget(ClampText(Text)),
                MappingTargetKind.SpecialKey => new SpecialKeyTarget(
                    SpecialKind,
                    SpecialPrecisionMode,
                    SpecialSwitchMode,
                    SpecialSlotA,
                    SpecialSlotB,

                    // The layer id is a layer key's own: the other kinds keep the Core default,
                    // so a precision or a leader key still builds exactly the target a profile
                    // written with the defaults carries.
                    LayerId: SpecialKind == SpecialKeyKind.Layer ? SpecialLayerId : 0),
                MappingTargetKind.ComplexShortcut => new ComplexShortcutTarget
                {
                    Script = Script,
                    Trigger = Trigger,
                    Execution = Execution,
                    Repeat = Repeat,
                },
                _ => new KeyPressTarget(SelectedKey),
            };
    }

    /// <summary>A draft may not carry a string longer than the Core's own bound.</summary>
    private static string ClampText(string text)
        => text.Length <= TextMaxLength ? text : text[..TextMaxLength];

    /// <summary>
    /// What the slot emits, or null when it re-fires the click - the behaviour every profile
    /// written before gesture actions existed already has.
    /// </summary>
    public MappingTarget? BuildAction() => Function == GestureSlotFunction.RepeatClick
        ? null
        : BuildTarget();

    /// <summary>
    /// Fills the draft from a loaded action. Null is the legacy repeat-the-click state; a
    /// <see cref="NoneTarget"/> is the explicit 无功能 state; an <see cref="UnknownTarget"/>
    /// is held as it stands; every other target fills the matching fields. The kind is
    /// written before the key and the modifiers so loading a combo cannot have its
    /// modifiers cleared by the kind change.
    /// </summary>
    public void LoadAction(MappingTarget? action)
    {
        // Whatever the draft held before is replaced wholesale; the unknown-target case
        // below puts one back.
        Unsupported = null;

        switch (action)
        {
            case null:
                Function = GestureSlotFunction.RepeatClick;
                break;
            case NoneTarget:
                // 无功能 leaves the slot with no function beyond it; the kind falls back to
                // the plain key press so no stale combo modifier can resurface when the
                // box is unchecked again.
                Function = GestureSlotFunction.None;
                TargetKind = MappingTargetKind.KeyPress;
                break;
            case UnknownTarget unknown:
                // Held, not replaced: the draft keeps whatever the other build wrote and
                // commits it back byte for byte. Any edit below clears it.
                Unsupported = unknown;
                Function = GestureSlotFunction.Target;
                break;
            case KeyPressTarget key:
                Function = GestureSlotFunction.Target;
                TargetKind = MappingTargetKind.KeyPress;
                SelectedKey = key.Key;
                break;
            case KeyComboTarget combo:
                Function = GestureSlotFunction.Target;
                TargetKind = MappingTargetKind.KeyCombo;
                SelectedKey = combo.Key;
                ModifierCtrl = combo.Modifiers.HasFlag(KeyModifiers.Ctrl);
                ModifierShift = combo.Modifiers.HasFlag(KeyModifiers.Shift);
                ModifierAlt = combo.Modifiers.HasFlag(KeyModifiers.Alt);
                ModifierWin = combo.Modifiers.HasFlag(KeyModifiers.Win);
                break;
            case MouseButtonTarget mouseButton:
                Function = GestureSlotFunction.Target;
                TargetKind = MappingTargetKind.MouseButton;
                SelectedMouseButton = mouseButton.Button;
                break;
            case MouseMoveTarget mouseMove:
                Function = GestureSlotFunction.Target;
                TargetKind = MappingTargetKind.MouseMove;
                MouseSettings = mouseMove.Settings;
                break;
            case WheelScrollTarget wheelScroll:
                Function = GestureSlotFunction.Target;
                TargetKind = MappingTargetKind.WheelScroll;
                ScrollSettings = wheelScroll.Settings;
                break;
            case TextTarget text:
                Function = GestureSlotFunction.Target;
                TargetKind = MappingTargetKind.Text;
                Text = text.Text;
                break;
            case SpecialKeyTarget special:
                Function = GestureSlotFunction.Target;
                TargetKind = MappingTargetKind.SpecialKey;
                SpecialPrecisionMode = special.PrecisionMode;
                SpecialSwitchMode = special.SwitchMode;
                SpecialSlotA = special.SlotA;
                SpecialSlotB = special.SlotB;
                SpecialLayerId = special.LayerId;

                // The kind lands last on purpose: a layer leader is Hold-only, so choosing
                // the kind repairs a Press-mode layer key a hand-edited profile may carry,
                // and a switch mode written above must not put it back.
                SpecialKind = special.Kind;
                break;
            case ComplexShortcutTarget complex:
                Function = GestureSlotFunction.Target;
                TargetKind = MappingTargetKind.ComplexShortcut;
                Script = complex.Script;
                Trigger = complex.Trigger;
                Execution = complex.Execution;
                Repeat = complex.Repeat;
                break;
        }
    }

    /// <summary>Back to a pristine draft: every field at its default, a real target.</summary>
    public void Reset()
    {
        Unsupported = null;
        Function = GestureSlotFunction.Target;
        TargetKind = MappingTargetKind.KeyPress;
        SelectedKey = GamepadKey.Enter;
        Text = string.Empty;
        Script = ExampleScript;
        Trigger = ComplexShortcutTarget.DefaultTrigger;
        Execution = ComplexShortcutTarget.DefaultExecution;
        Repeat = ComplexShortcutTarget.DefaultRepeat;
        ModifierCtrl = false;
        ModifierShift = false;
        ModifierAlt = false;
        ModifierWin = false;
        SelectedMouseButton = MouseButton.Left;
        MouseSettings = new MouseStickSettings();
        ScrollSettings = new ScrollStickSettings();
        SpecialKind = SpecialKeyKind.Precision;
        SpecialPrecisionMode = PrecisionMode.Toggle;
        SpecialSwitchMode = LeaderSwitchMode.Press;
        SpecialSlotA = 1;
        SpecialSlotB = 2;
        SpecialLayerId = FirstLayerId;
    }

    partial void OnTargetKindChanged(MappingTargetKind value)
    {
        ClaimFunction();

        // A combo is the only shape that reads the modifier flags, so leaving it must clear
        // them; otherwise a stale Ctrl would resurface the next time a combo is chosen.
        if (value != MappingTargetKind.KeyCombo)
        {
            ModifierCtrl = false;
            ModifierShift = false;
            ModifierAlt = false;
            ModifierWin = false;
        }
    }

    partial void OnSelectedKeyChanged(GamepadKey value) => ClaimFunction();

    partial void OnTextChanged(string value) => ClaimFunction();

    partial void OnTriggerChanged(ActionTrigger value) => ClaimFunction();

    partial void OnExecutionChanged(ActionExecution value) => ClaimFunction();

    partial void OnRepeatChanged(ActionRepeat value) => ClaimFunction();

    partial void OnModifierCtrlChanged(bool value) => ClaimFunction();

    partial void OnModifierShiftChanged(bool value) => ClaimFunction();

    partial void OnModifierAltChanged(bool value) => ClaimFunction();

    partial void OnModifierWinChanged(bool value) => ClaimFunction();

    partial void OnSelectedMouseButtonChanged(MouseButton value) => ClaimFunction();

    partial void OnMouseSettingsChanged(MouseStickSettings value) => ClaimFunction();

    partial void OnScrollSettingsChanged(ScrollStickSettings value) => ClaimFunction();

    partial void OnSpecialKindChanged(SpecialKeyKind value)
    {
        ClaimFunction();

        // A temporary layer lives exactly as long as its leader is held, and the engine
        // rejects a layer key that switches on a press edge, so the kind pins the switch
        // mode to 按住 instead of leaving a draft that could never run.
        if (value == SpecialKeyKind.Layer)
        {
            SpecialSwitchMode = LeaderSwitchMode.Hold;
        }
    }

    partial void OnSpecialPrecisionModeChanged(PrecisionMode value) => ClaimFunction();

    partial void OnSpecialSwitchModeChanged(LeaderSwitchMode value) => ClaimFunction();

    partial void OnSpecialSlotAChanged(int value) => ClaimFunction();

    partial void OnSpecialSlotBChanged(int value) => ClaimFunction();

    partial void OnSpecialLayerIdChanged(int value) => ClaimFunction();

    /// <summary>
    /// Editing any field is a choice of target, so a slot that was re-firing the click takes
    /// on a function of its own. 无功能 is never overridden this way: it is explicit. An edit
    /// is also what replaces a target this build does not know - the draft keeps it until
    /// the user chooses something else, and then it is gone.
    /// </summary>
    private void ClaimFunction()
    {
        Unsupported = null;

        if (Function == GestureSlotFunction.RepeatClick)
        {
            Function = GestureSlotFunction.Target;
        }
    }
}

/// <summary>What a key on the on-screen keyboard does when it is pressed.</summary>
public enum TextKeyboardAction
{
    /// <summary>Types the key's own text - one character, or a whole fragment such as "https://".</summary>
    Insert,

    /// <summary>Deletes the character before the cursor.</summary>
    Backspace,

    /// <summary>Types a space.</summary>
    Space,

    /// <summary>Empties the whole draft.</summary>
    Clear,

    /// <summary>Ends the entry.</summary>
    Confirm,
}

/// <summary>One key on the on-screen keyboard: its label, the text it types and what it does.</summary>
/// <param name="Label">What the cap shows.</param>
/// <param name="Text">What an <see cref="TextKeyboardAction.Insert"/> key types; null for an action key.</param>
/// <param name="Action">What pressing the key does.</param>
public sealed record TextKeyboardKey(string Label, string? Text, TextKeyboardAction Action = TextKeyboardAction.Insert);

/// <summary>One page of the on-screen keyboard: its name and its rows, bottom row last.</summary>
public sealed record TextKeyboardPage(string Name, IReadOnlyList<IReadOnlyList<TextKeyboardKey>> Rows)
{
    /// <summary>The keys of one row, for a grid that lays a page out a row at a time.</summary>
    public IReadOnlyList<TextKeyboardKey> this[int row] => Rows[row];
}

/// <summary>
/// A basic on-screen keyboard for typing a short text target with the gamepad. A cursor sits
/// on one key; the D-pad moves it and the A button presses it, so a search box, a file rename
/// or a short URL can be entered without a physical keyboard.
/// </summary>
/// <remarks>
/// <para>
/// The keyboard is deliberately basic: it types the characters its pages carry and nothing
/// else. Full IME integration - focus hand-off to a real input method, its candidate window
/// and its commit - is a follow-up, so a character no page carries (a non-ASCII letter, an
/// emoji) is out of reach here and the physical-keyboard capture on the editor stays the way
/// to type one.
/// </para>
/// <para>
/// This type owns the cursor and the pages only. Applying a key to the draft is the editor's
/// job (see <c>MappingEditorViewModel.ApplyTextKeyboardKey</c>), which is what keeps the
/// keyboard reusable and unit-testable without a window.
/// </para>
/// </remarks>
public sealed partial class TextKeyboard : ObservableObject
{
    /// <summary>The action row every page ends with: 退格, 空格, 清空 and 完成.</summary>
    private static readonly TextKeyboardKey[] ActionRow =
    [
        new("退格", null, TextKeyboardAction.Backspace),
        new("空格", " ", TextKeyboardAction.Space),
        new("清空", null, TextKeyboardAction.Clear),
        new("完成", null, TextKeyboardAction.Confirm),
    ];

    private static TextKeyboardKey[] Keys(string characters)
        => [.. characters.Select(character => new TextKeyboardKey(character.ToString(), character.ToString()))];

    private static readonly TextKeyboardPage[] PagesField =
    [
        new("小写", [
            Keys("abcdefghi"),
            Keys("jklmnopqr"),
            Keys("stuvwxyz"),
            ActionRow,
        ]),
        new("大写", [
            Keys("ABCDEFGHI"),
            Keys("JKLMNOPQR"),
            Keys("STUVWXYZ"),
            ActionRow,
        ]),
        new("数字", [
            Keys("1234567890"),
            Keys("-[];',./`\\"),
            Keys("=<>?:_+*"),
            ActionRow,
        ]),
        new("符号", [
            Keys("!@#$%^&*()"),
            Keys("{}|\"~"),
            Keys("§°¥€£•…"),
            ActionRow,
        ]),
        new("常用", [
            [
                new("https://", "https://"),
                new("http://", "http://"),
                new("www.", "www."),
                new(".com", ".com"),
                new(".cn", ".cn"),
            ],
            [
                new("搜索", "search "),
                new("@", "@"),
                new("#", "#"),
                new("_", "_"),
                new("-", "-"),
            ],
            ActionRow,
        ]),
    ];

    /// <summary>Every page, in the order the keyboard offers them.</summary>
    public IReadOnlyList<TextKeyboardPage> Pages { get; } = PagesField;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FocusedKey))]
    private TextKeyboardPage _activePage = PagesField[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FocusedKey))]
    private int _cursorRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FocusedKey))]
    private int _cursorColumn;

    /// <summary>The key the cursor is on, or null when the cursor sits outside the page.</summary>
    public TextKeyboardKey? FocusedKey
    {
        get
        {
            if (ActivePage is not { } page
                || CursorRow < 0 || CursorRow >= page.Rows.Count)
            {
                return null;
            }

            var row = page[CursorRow];
            return CursorColumn >= 0 && CursorColumn < row.Count ? row[CursorColumn] : null;
        }
    }

    /// <summary>The page after this one, wrapping around at the end.</summary>
    public TextKeyboardPage NextPage => Pages[(IndexOf(ActivePage) + 1) % Pages.Count];

    /// <summary>The page before this one, wrapping around at the start.</summary>
    public TextKeyboardPage PreviousPage
    {
        get
        {
            var index = IndexOf(ActivePage) - 1;
            return Pages[index < 0 ? Pages.Count - 1 : index];
        }
    }

    /// <summary>Where a page sits in <see cref="Pages"/>, by reference.</summary>
    private static int IndexOf(TextKeyboardPage page)
    {
        for (var i = 0; i < PagesField.Length; i++)
        {
            if (ReferenceEquals(PagesField[i], page))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Moves to another page, putting the cursor back on its first key.</summary>
    public void ShowPage(TextKeyboardPage page)
    {
        ActivePage = page;
        CursorRow = 0;
        CursorColumn = 0;
    }

    /// <summary>
    /// Moves the cursor one key in a direction. Returns false when the cursor is already at
    /// the edge it was pushed against, which is how a navigator knows the push was not spent
    /// here and may move focus out of the keyboard instead.
    /// </summary>
    public bool Move(InputDirection direction)
    {
        if (ActivePage is not { } page)
        {
            return false;
        }

        var row = CursorRow;
        var column = CursorColumn;

        switch (direction)
        {
            case InputDirection.Up:
                if (row <= 0)
                {
                    return false;
                }

                row--;
                break;
            case InputDirection.Down:
                if (row >= page.Rows.Count - 1)
                {
                    return false;
                }

                row++;
                break;
            case InputDirection.Left:
                if (column <= 0)
                {
                    return false;
                }

                column--;
                break;
            case InputDirection.Right:
                if (column >= page[CursorRow].Count - 1)
                {
                    return false;
                }

                column++;
                break;
            default:
                return false;
        }

        // A row may be shorter than the one the cursor came from, so the column is clamped
        // rather than left pointing past the end of the new row.
        CursorRow = row;
        CursorColumn = Math.Min(column, page[row].Count - 1);
        return true;
    }

    /// <summary>The key the cursor is on, for a press to act on.</summary>
    public TextKeyboardKey? Activate() => FocusedKey;
}
