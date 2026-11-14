using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// The floating on-screen keyboard: the independent window a 屏幕键盘 special key summons,
/// which types into whatever application has focus. It reuses the same
/// <see cref="TextKeyboard"/> model the mapping editor's text draft uses - a cursor on one
/// key, the D-pad moving it, A pressing it, the bumpers turning the page - so a search box,
/// a file rename or a short URL can be entered with the gamepad alone.
/// </summary>
/// <remarks>
/// <para>
/// This keyboard types into the focused application, not into a draft: the daemon is the
/// sole injection authority, so every key press is a request the daemon answers through its
/// own output path. The preview beside the caps mirrors what went out, because the window
/// floats over the target application and the user cannot see its caret.
/// </para>
/// <para>
/// It is deliberately basic, exactly like the model it reuses: it types the characters its
/// pages carry and nothing else. Full IME composition - focus hand-off to a real input
/// method, its candidate window and its commit - is a follow-up, so a character no page
/// carries is out of reach here.
/// </para>
/// <para>
/// The window itself owns no state: this view model holds the cursor, the pages, the preview
/// and the open flag, which is what makes the whole keyboard unit-testable without a window.
/// The window only shows and hides with <see cref="IsOpen"/> and mirrors the cursor onto the
/// caps.
/// </para>
/// </remarks>
public sealed partial class ScreenKeyboardViewModel : ObservableObject
{
    private Func<string, Task> _typeText = static _ => Task.CompletedTask;

    private Func<GamepadKey, KeyModifiers, Task> _tapKey = static (_, _) => Task.CompletedTask;

    /// <summary>
    /// Hands the keyboard the two requests it can make. Production passes the daemon; a test
    /// passes nothing and reads what would have gone out instead. Called once, by whoever owns
    /// the connection the requests travel over - the render window's view model, or the tray's
    /// feed.
    /// </summary>
    public void SetOutput(
        Func<string, Task>? typeText = null,
        Func<GamepadKey, KeyModifiers, Task>? tapKey = null)
    {
        _typeText = typeText ?? (static _ => Task.CompletedTask);
        _tapKey = tapKey ?? (static (_, _) => Task.CompletedTask);
    }

    /// <summary>
    /// Builds the keyboard over the two requests it can make. Production passes the daemon
    /// (or the local host when no daemon is in play); a test passes nothing and reads what
    /// would have been typed instead.
    /// </summary>
    public ScreenKeyboardViewModel(
        Func<string, Task>? typeText = null,
        Func<GamepadKey, KeyModifiers, Task>? tapKey = null)
        => SetOutput(typeText, tapKey);

    /// <summary>The keyboard itself: its pages, its cursor and the key it is on.</summary>
    public TextKeyboard Keyboard { get; } = new();

    /// <summary>
    /// Whether the window is on screen. The daemon owns this flag in daemon mode - it is
    /// what the engine's screen-keyboard key toggles and what a snapshot publishes - so this
    /// is a mirror, never the source. While it is set the input lease is taken, which is what
    /// hands the pad to this keyboard instead of to the mapping.
    /// </summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>
    /// What has been typed so far, mirrored from the requests that went out. The window
    /// floats above the target application and its caret is not visible from here, so this is
    /// the only feedback the user gets that the keyboard is saying what they think it is.
    /// </summary>
    [ObservableProperty]
    private string _typedText = string.Empty;

    /// <summary>Why the last request failed, or empty when none has.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// Raised when the keyboard asks to be dismissed - its 完成 key, or the 返回 button the
    /// window carries. The daemon owns the open flag, so this is a request rather than a
    /// close: the owner answers by clearing the flag, and the snapshot that follows is what
    /// takes the window away.
    /// </summary>
    public event Action? CloseRequested;

    /// <summary>Moves the cursor one key in a direction; false when it is already at that edge.</summary>
    public bool Move(InputDirection direction) => Keyboard.Move(direction);

    /// <summary>Shows the next page, wrapping around at the end.</summary>
    public void ShowNextPage() => Keyboard.ShowPage(Keyboard.NextPage);

    /// <summary>Shows the previous page, wrapping around at the start.</summary>
    public void ShowPreviousPage() => Keyboard.ShowPage(Keyboard.PreviousPage);

    /// <summary>
    /// Routes one sample's press edges to the keyboard: the D-pad moves the cursor, A presses
    /// the key it is on and the bumpers turn the page. Everything else is left alone, so the
    /// caller decides what an unowned press means. The dismissal - B, or the button the
    /// profile's own 屏幕键盘 key sits on - is the caller's to recognise, because only the
    /// caller knows the profile.
    /// </summary>
    public void Navigate(GamepadButtonMask edges)
    {
        if (edges == GamepadButtonMask.None)
        {
            return;
        }

        if (edges.Has(GamepadButton.LeftBumper))
        {
            ShowPreviousPage();
        }
        else if (edges.Has(GamepadButton.RightBumper))
        {
            ShowNextPage();
        }
        else if (edges.Has(GamepadButton.A))
        {
            Activate();
        }
        else if (DirectionOf(edges) is { } direction)
        {
            Move(direction);
        }
    }

    /// <summary>The direction one set of press edges names, or null when they name none.</summary>
    private static InputDirection? DirectionOf(GamepadButtonMask edges)
    {
        if (edges.Has(GamepadButton.DPadUp))
        {
            return InputDirection.Up;
        }

        if (edges.Has(GamepadButton.DPadDown))
        {
            return InputDirection.Down;
        }

        if (edges.Has(GamepadButton.DPadLeft))
        {
            return InputDirection.Left;
        }

        if (edges.Has(GamepadButton.DPadRight))
        {
            return InputDirection.Right;
        }

        return null;
    }

    /// <summary>
    /// Presses the key the cursor is on. An insert key types its text into the focused
    /// application, an action key runs its own edit - 退格 and 清空 reach the application
    /// too, because a preview that deletes without the target noticing is worse than no
    /// preview - and 完成 asks to be dismissed. Every request is fire-and-forget: a key
    /// press must never wait on a round trip, and a failure is reported beside the preview.
    /// </summary>
    public void Activate()
    {
        if (Keyboard.Activate() is not { } key)
        {
            return;
        }

        switch (key.Action)
        {
            case TextKeyboardAction.Backspace:
                TypedText = TypedText.Length > 0 ? TypedText[..^1] : TypedText;
                Send(_tapKey(GamepadKey.Backspace, KeyModifiers.None));
                break;
            case TextKeyboardAction.Clear:
                TypedText = string.Empty;
                Send(_tapKey(GamepadKey.A, KeyModifiers.Ctrl));
                Send(_tapKey(GamepadKey.Delete, KeyModifiers.None));
                break;
            case TextKeyboardAction.Space:
            case TextKeyboardAction.Insert:
                TypedText = Append(key.Text);
                Send(_typeText(key.Text ?? string.Empty));
                break;
            case TextKeyboardAction.Confirm:
                Close();
                break;
        }
    }

    /// <summary>Asks the owner to dismiss the window.</summary>
    public void Close() => CloseRequested?.Invoke();

    /// <summary>
    /// Back to a pristine keyboard: the first page, the cursor on its first key and an empty
    /// preview. Runs when the window is taken away, so the next appearance never starts on
    /// whatever the last one left behind.
    /// </summary>
    public void Reset()
    {
        Keyboard.ShowPage(Keyboard.Pages[0]);
        TypedText = string.Empty;
        StatusMessage = string.Empty;
    }

    /// <summary>Types more into the preview, stopping at the Core's own bound rather than refusing the key.</summary>
    private string Append(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return TypedText;
        }

        var room = TextTarget.MaxLength - TypedText.Length;
        return room <= 0 ? TypedText : TypedText + text[..(int)Math.Min(room, text.Length)];
    }

    /// <summary>
    /// Sends one request without waiting for it. A key press answers at once; the failure, if
    /// there is one, is reported through <see cref="StatusMessage"/> on the thread the request
    /// completes on, so it never blocks the pad.
    /// </summary>
    private void Send(Task request)
    {
        _ = request.ContinueWith(
            failed => ReportFailure(failed.Exception?.Flatten().InnerException?.Message ?? "未知原因"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Says why the last request failed, beside the preview. A caller that answers a request
    /// with a refusal rather than an exception - the daemon refusing to type because it holds no
    /// output channel - reports it here too, so a keyboard that typed nothing always says why.
    /// </summary>
    public void ReportFailure(string reason)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            StatusMessage = $"屏幕键盘输出失败：{reason}";
            return;
        }

        Dispatcher.UIThread.Post(() => StatusMessage = $"屏幕键盘输出失败：{reason}");
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (!value)
        {
            // The window is gone, so nothing it holds matters any more.
            Reset();
        }
    }
}
