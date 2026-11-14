using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views;

/// <summary>
/// The floating screen keyboard: the independent window a 屏幕键盘 special key summons, which
/// types into whatever application has focus.
/// <para>
/// It is a window of its own rather than part of the mapping GUI for the same reason the layer
/// hint overlay is: it must float above the target application - which means it must never take
/// that application's keyboard focus (<see cref="Window.ShowActivated"/> is false and the window
/// is not focusable) - and it must outlive the editor card, which is where the old embedded
/// keyboard lived and why a search box, a rename or a URL was awkward to fill in. Unlike the
/// layer hint it is not click-through: a mouse user can press a cap, and a window that cannot
/// be focused does not move keyboard focus away from the application underneath.
/// </para>
/// <para>
/// It owns no state. It shows and hides with the view model's open flag - which the daemon owns
/// in daemon mode, and publishes in a snapshot - and mirrors the view model's cursor onto the
/// caps, exactly as the editor panel used to mirror it onto the embedded grid. Every write goes
/// through the view model, which asks the daemon to inject.
/// </para>
/// </summary>
public partial class ScreenKeyboardWindow : Window
{
    /// <summary>How far the keyboard sits from a screen edge, clearing taskbars and screen borders.</summary>
    private const double EdgeMargin = 48;

    private readonly ScreenKeyboardViewModel _keyboard;

    /// <summary>
    /// The key grid, resolved by name the way every other window in this project resolves its
    /// controls: a window's XAML is loaded at runtime, so the named control is looked up
    /// rather than held in a compiled field. Cached, because the cursor moves per press.
    /// </summary>
    private ItemsControl? _keyGrid;

    private ItemsControl? KeyGrid => _keyGrid ??= this.FindControl<ItemsControl>("TextKeyboardGrid");

    /// <summary>
    /// The runtime-loader constructor: a window bound to a view model of its own, which no
    /// production path uses - the render process builds one over the live model - but which
    /// keeps the XAML resource loadable and the control previewable.
    /// </summary>
    public ScreenKeyboardWindow()
        : this(new ScreenKeyboardViewModel())
    {
    }

    public ScreenKeyboardWindow(ScreenKeyboardViewModel keyboard)
    {
        AvaloniaXamlLoader.Load(this);

        DataContext = keyboard;
        _keyboard = keyboard;
        keyboard.PropertyChanged += OnKeyboardPropertyChanged;

        // The cursor and the page live on the keyboard itself, not on the view model, so it is
        // the keyboard's own notifications that have to re-mark the caps.
        keyboard.Keyboard.PropertyChanged += OnKeyboardPropertyChanged;
        ApplyVisibility();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (!TryPosition())
        {
            // The window has not measured its content yet: the first layout pass that gives it
            // a size repositions it, then unhooks itself.
            LayoutUpdated += OnLayoutUpdated;
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (TryPosition())
        {
            LayoutUpdated -= OnLayoutUpdated;
        }
    }

    private void OnKeyboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The open flag is the only thing that decides whether the window is on screen, and the
        // cursor is the only thing that decides which cap reads as focused - so both are
        // re-read on every notification rather than on the two named ones alone.
        if (e.PropertyName == nameof(ScreenKeyboardViewModel.IsOpen))
        {
            ApplyVisibility();
            return;
        }

        if (e.PropertyName is nameof(ScreenKeyboardViewModel.StatusMessage))
        {
            RefreshStatus();
            return;
        }

        RefreshTextKeyboardFocus();
    }

    private void ApplyVisibility()
    {
        if (_keyboard.IsOpen)
        {
            if (!IsVisible)
            {
                Show();
            }

            // Every appearance re-reads the screen, so the keyboard lands where the user is
            // looking rather than where it last was.
            TryPosition();
        }
        else if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>
    /// Parks the keyboard along the bottom of the screen it is on, clear of the taskbar. It is
    /// a bottom-anchored card rather than a centred one because the target application's own
    /// content - a search box, a rename field, a URL bar - usually lives above the bottom edge,
    /// and a keyboard that covers the field it is filling is worse than one that sits under it.
    /// Returns false when the platform reports no screens or the window has not been measured
    /// yet, leaving the window where the platform put it.
    /// </summary>
    private bool TryPosition()
    {
        if (Screens is null || Bounds.Width <= 0)
        {
            return false;
        }

        if (Screens.ScreenFromVisual(this) is not { } screen)
        {
            return false;
        }

        Position = ResolvePosition(screen.Bounds, Bounds.Size);
        return true;
    }

    /// <summary>
    /// The pixel position the keyboard takes, resolved purely from its inputs so the placement
    /// is unit-testable without a window: horizontally centred, one margin above the bottom
    /// edge, and never off the top of a screen shorter than the keyboard itself.
    /// </summary>
    internal static PixelPoint ResolvePosition(PixelRect screen, Size window, double edgeMargin = EdgeMargin)
    {
        var x = screen.X + (screen.Width - window.Width) / 2.0;
        var y = screen.Y + screen.Height - window.Height - edgeMargin;

        return new PixelPoint(
            (int)Math.Round(Math.Clamp(x, screen.X, Math.Max(screen.X, screen.X + screen.Width - window.Width))),
            (int)Math.Round(Math.Max(y, screen.Y)));
    }

    private void RefreshStatus()
    {
        if (this.FindControl<TextBlock>("StatusText") is { } status)
        {
            status.IsVisible = !string.IsNullOrEmpty(_keyboard.StatusMessage);
        }
    }

    /// <summary>
    /// A page tab was pressed: the keyboard shows that page and the cursor returns to its first
    /// key. The checked state is mirrored from the keyboard's own page rather than written here,
    /// so a programmatic move - a bumper press - marks the same tab a click would.
    /// </summary>
    private void OnTextKeyboardPageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: TextKeyboardPage page })
        {
            _keyboard.Keyboard.ShowPage(page);
        }
    }

    /// <summary>
    /// A cap was activated by pointer: the cursor moves onto it and the keyboard presses it, the
    /// two calls the keyboard's own cursor API is built from. A pointer press therefore leaves
    /// the cursor where the user last reached, so the next D-pad push continues from there.
    /// </summary>
    private void OnTextKeyboardKeyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TextKeyboardKey key })
        {
            return;
        }

        TryMoveCursorTo(key);
        _keyboard.Activate();
    }

    /// <summary>A cap entered the tree, so it wears the cursor the keyboard already holds.</summary>
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
            tab.IsChecked = ReferenceEquals(_keyboard.Keyboard.ActivePage, page);
        }
    }

    /// <summary>
    /// Marks the cap the keyboard's cursor sits on. The cursor is view-model state, not real
    /// focus - the window is not focusable, so no cap can hold keyboard focus anyway - so this
    /// is what makes a cursor move visible: the cap wears the accent surface and its label
    /// weight rises with it, and neither state rests on colour alone.
    /// </summary>
    private void RefreshTextKeyboardFocus()
    {
        if (KeyGrid is not { } grid)
        {
            return;
        }

        foreach (var button in grid.GetLogicalDescendants().OfType<Button>())
        {
            if (button.DataContext is TextKeyboardKey key)
            {
                button.Classes.Set("focused", IsFocusedKey(key));
            }
        }
    }

    private bool IsFocusedKey(TextKeyboardKey key)
        => _keyboard.Keyboard.FocusedKey is { } focused && focused == key;

    /// <summary>Puts the keyboard's cursor on one key; false when the page does not carry it.</summary>
    private bool TryMoveCursorTo(TextKeyboardKey key)
    {
        var keyboard = _keyboard.Keyboard;
        var page = keyboard.ActivePage;
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
                    keyboard.CursorRow = row;
                    keyboard.CursorColumn = column;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The 返回 button: a dismissal, exactly like the 完成 cap and the B button. The window asks
    /// rather than closing itself, because the daemon owns the flag and the next engine toggle
    /// has to agree with what the user sees.
    /// </summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => _keyboard.Close();
}
