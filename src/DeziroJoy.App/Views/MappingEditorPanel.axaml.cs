using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;

namespace DeziroJoy.App.Views;

/// <summary>
/// Code-behind for the mapping editor panel. The panel is declarative: it binds straight to
/// the <see cref="MappingEditorViewModel"/> the host assigns as its DataContext. The things
/// binding cannot express are mirrored here instead: the key-cap grid's selection visual - a
/// grid of buttons has no selector of its own to carry it - and the on-screen keyboard's
/// cursor, which is view-model state rather than real focus, so the cap it sits on and the
/// page it sits on are marked the same way. The page tabs and the key caps forward their own
/// activation to the editor, which owns every write to the draft.
/// </summary>
public partial class MappingEditorPanel : UserControl
{
    private MappingEditorViewModel? _editor;
    private TextKeyboard? _keyboard;

    public MappingEditorPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Arrow keys drive the on-screen keyboard's cursor while the keyboard holds focus.
        // The handler is a tunnel on the panel so it runs before the body's ScrollViewer
        // would claim the key for scrolling.
        AddHandler(KeyDownEvent, OnTextKeyboardKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_editor is not null)
        {
            _editor.PropertyChanged -= OnEditorPropertyChanged;
        }

        if (_keyboard is not null)
        {
            _keyboard.PropertyChanged -= OnKeyboardPropertyChanged;
            _keyboard = null;
        }

        _editor = DataContext as MappingEditorViewModel;

        if (_editor is not null)
        {
            _editor.PropertyChanged += OnEditorPropertyChanged;
            _keyboard = _editor.TextKeyboard;
            _keyboard.PropertyChanged += OnKeyboardPropertyChanged;
        }

        RefreshKeyCapSelection();
        RefreshTextKeyboardPages();
        RefreshTextKeyboardFocus();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MappingEditorViewModel.ActiveSelectedKeyOption))
        {
            RefreshKeyCapSelection();
        }

        // A load replaces the draft wholesale, and one that lands on a target this build
        // does not know can leave every field the banner binds to exactly where it was -
        // so no notification may announce the change. The banner is therefore re-read on
        // every editor notification rather than left to trust that one particular field
        // moved.
        RefreshUnsupportedTarget();
    }

    /// <summary>
    /// Shows the banner while the draft carries a target this build cannot read. The banner's
    /// own binding states the rule; this restates it after a load, whose notifications name
    /// whatever field moved rather than the unsupported state itself.
    /// </summary>
    private void RefreshUnsupportedTarget()
        => UnsupportedTargetBanner.IsVisible = _editor is { } editor && editor.ActiveTargetUnsupported;

    /// <summary>
    /// The keyboard's own cursor or page moved, so the caps and the tabs are re-marked. Both
    /// changes raise <see cref="TextKeyboard.FocusedKey"/> as well, which is why the focus
    /// refresh runs on every notification rather than only on the two named ones.
    /// </summary>
    private void OnKeyboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TextKeyboard.ActivePage))
        {
            RefreshTextKeyboardPages();
        }

        RefreshTextKeyboardFocus();

        // A gamepad push moves the cursor without touching real focus - the keyboard is the
        // pad's own surface - so the cap it lands on is revealed here instead: the highlight
        // must never sit below the fold of the editor body that owns the scrolling.
        if (e.PropertyName is nameof(TextKeyboard.FocusedKey)
            or nameof(TextKeyboard.CursorRow)
            or nameof(TextKeyboard.CursorColumn))
        {
            RevealFocusedTextKeyboardKey();
        }
    }

    /// <summary>A key cap was activated, by pointer or by keyboard: it becomes the active slot's key.</summary>
    private void OnKeyCapClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: KeyPickerItem item } && _editor is not null)
        {
            _editor.ActiveSelectedKeyOption = item;
        }
    }

    /// <summary>
    /// A cap entered the tree - the expander was just opened - so it wears the selection the
    /// editor already holds instead of waiting for the next change to mark it.
    /// </summary>
    private void OnKeyCapLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: KeyPickerItem item } button)
        {
            button.Classes.Set("selected", IsSelected(item));
        }
    }

    private void RefreshKeyCapSelection()
    {
        foreach (var button in this.GetLogicalDescendants().OfType<Button>())
        {
            if (button.DataContext is KeyPickerItem item)
            {
                button.Classes.Set("selected", IsSelected(item));
            }
        }
    }

    private bool IsSelected(KeyPickerItem item)
        => _editor?.ActiveSelectedKeyOption is { } selected && selected.Key == item.Key;

    // ---- 屏幕键盘 ------------------------------------------------------------------

    /// <summary>
    /// A page tab was pressed: the keyboard shows that page and the cursor returns to its
    /// first key, which is the one call the keyboard itself owns. The checked state is
    /// mirrored from the keyboard's own page rather than written here, so a programmatic
    /// move - a load landing on another page - marks the same tab a press would.
    /// </summary>
    private void OnTextKeyboardPageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: TextKeyboardPage page } && _editor?.TextKeyboard is { } keyboard)
        {
            keyboard.ShowPage(page);
        }
    }

    /// <summary>
    /// A cap was activated, by pointer or by keyboard: the cursor moves onto it and the
    /// editor presses it, the two calls the editor's own cursor API is built from. A pointer
    /// press therefore leaves the cursor where the user last reached, so the next push
    /// continues from there instead of from wherever the cursor happened to sit.
    /// </summary>
    private void OnTextKeyboardKeyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TextKeyboardKey key } || _editor is not { } editor)
        {
            return;
        }

        if (TryMoveCursorTo(key))
        {
            editor.ActivateTextKeyboardKey();
            return;
        }

        editor.ApplyTextKeyboardKey(key);
    }

    /// <summary>A cap entered the tree - the keyboard was just offered or the page just changed.</summary>
    private void OnTextKeyboardKeyLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TextKeyboardKey key } button)
        {
            button.Classes.Set("focused", IsFocusedKey(key));
        }
    }

    /// <summary>A page tab entered the tree, so it wears the checked state the keyboard already holds.</summary>
    private void OnTextKeyboardTabLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: TextKeyboardPage page } tab)
        {
            tab.IsChecked = _keyboard?.ActivePage is { } active && ReferenceEquals(active, page);
        }
    }

    /// <summary>
    /// Marks the cap the keyboard's cursor sits on. The cursor is view-model state, not real
    /// focus, so this is what makes a cursor move visible: the cap wears the accent surface
    /// and its label weight rises with it, and neither state rests on colour alone.
    /// </summary>
    private void RefreshTextKeyboardFocus()
    {
        foreach (var button in TextKeyboardGrid.GetLogicalDescendants().OfType<Button>())
        {
            if (button.DataContext is TextKeyboardKey key)
            {
                button.Classes.Set("focused", IsFocusedKey(key));
            }
        }
    }

    private bool IsFocusedKey(TextKeyboardKey key)
        => _keyboard?.FocusedKey is { } focused && focused == key;

    /// <summary>Marks the tab of the page the keyboard is on, so exactly one segment reads as checked.</summary>
    private void RefreshTextKeyboardPages()
    {
        var active = _keyboard?.ActivePage;

        foreach (var tab in TextKeyboardPageNav.GetLogicalDescendants().OfType<RadioButton>())
        {
            if (tab.DataContext is TextKeyboardPage page)
            {
                tab.IsChecked = active is not null && ReferenceEquals(active, page);
            }
        }
    }

    /// <summary>Puts the keyboard's cursor on one key; false when the page does not carry it.</summary>
    private bool TryMoveCursorTo(TextKeyboardKey key)
    {
        var page = _keyboard?.ActivePage;
        if (page is null)
        {
            return false;
        }

        for (var row = 0; row < page.Rows.Count; row++)
        {
            var keys = page[row];
            for (var column = 0; column < keys.Count; column++)
            {
                if (keys[column] == key)
                {
                    _keyboard!.CursorRow = row;
                    _keyboard.CursorColumn = column;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Arrow keys move the on-screen keyboard's cursor while the keyboard holds focus. The
    /// editor's own API reports whether the push was spent here: at an edge it returns false
    /// and the key falls through to the body's scroll, so a push that cannot move the cursor
    /// still scrolls the card rather than doing nothing. The handler is a tunnel on the panel
    /// so it runs before the body's ScrollViewer would claim the key for scrolling.
    /// </summary>
    private void OnTextKeyboardKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editor is not { } editor || !editor.ShowTextKeyboard || !KeyboardHoldsFocus())
        {
            return;
        }

        var direction = e.Key switch
        {
            Key.Up => InputDirection.Up,
            Key.Down => InputDirection.Down,
            Key.Left => InputDirection.Left,
            Key.Right => InputDirection.Right,
            _ => (InputDirection?)null,
        };

        if (direction is null || !editor.MoveTextKeyboardCursor(direction.Value))
        {
            return;
        }

        e.Handled = true;

        // The cursor moved, so the cap it landed on takes real focus with it: the focus ring
        // and the cursor highlight then agree, and a press - Space or Enter on a focused cap
        // - activates the cap the cursor is on. The grid has no scrollbar of its own and the
        // editor body above owns the scrolling, so the cap is brought into view.
        RevealFocusedTextKeyboardKey(takeFocus: true);
    }

    /// <summary>
    /// Brings the cap the keyboard's cursor sits on into view, and - when asked - moves real
    /// focus onto it as well. A key press moves the cursor only while the keyboard already
    /// holds focus, so it takes the focus with the cursor; a gamepad push owns the keyboard
    /// outright, so it reveals without focusing and leaves whatever had focus - the text box
    /// a user may be typing in - alone.
    /// </summary>
    private void RevealFocusedTextKeyboardKey(bool takeFocus = false)
    {
        if (_keyboard?.FocusedKey is not { } focused)
        {
            return;
        }

        foreach (var button in TextKeyboardGrid.GetLogicalDescendants().OfType<Button>())
        {
            if (button.DataContext is TextKeyboardKey key && key == focused)
            {
                button.BringIntoView();

                if (takeFocus)
                {
                    button.Focus();
                }

                return;
            }
        }
    }

    /// <summary>True when the focus sits somewhere inside the on-screen keyboard, tabs included.</summary>
    private bool KeyboardHoldsFocus()
    {
        for (var current = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
            current is not null;
            current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, TextKeyboardRow))
            {
                return true;
            }
        }

        return false;
    }
}
