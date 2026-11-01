using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using CoreKeyModifiers = DeziroJoy.Core.Mapping.KeyModifiers;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Views;

/// <summary>
/// Invisible wiring control: forwards every engine sample to
/// <see cref="MappingEditorViewModel.CaptureGamepadSample"/> and every key press/release to the
/// keyboard-capture entry points, so the mapping editor can be filled from live input without
/// the window's code-behind taking part. The view model decides whether capture is armed, so
/// ordinary typing and ordinary gamepad use are untouched.
/// </summary>
public sealed class EditorInputCapture : Control
{
    private MainWindowViewModel? _viewModel;

    private Window? _window;

    private readonly HashSet<Key> _heldKeys = [];

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (TopLevel.GetTopLevel(this) is not Window window || window.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        _window = window;
        _viewModel = viewModel;
        viewModel.Host.Sampled += OnSampled;
        window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.Deactivated += OnWindowDeactivated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unwire();
        base.OnDetachedFromVisualTree(e);
    }

    private void Unwire()
    {
        if (_viewModel is not null)
        {
            _viewModel.Host.Sampled -= OnSampled;
        }

        if (_window is not null)
        {
            _window.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            _window.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
            _window.Deactivated -= OnWindowDeactivated;
        }

        _heldKeys.Clear();
        _viewModel = null;
        _window = null;
    }

    private void OnSampled(object? sender, ControllerSampledEventArgs args)
    {
        var editor = _viewModel?.Editor;
        if (editor is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => editor.CaptureGamepadSample(args.State));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var editor = _viewModel?.Editor;
        if (editor is null || !editor.IsCapturingKey)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            _heldKeys.Clear();
            editor.EndKeyboardCapture();
            return;
        }

        if (KeyboardKeyMap.IsModifier(e.Key))
        {
            return;
        }

        var key = KeyboardKeyMap.ToGamepadKey(e.Key);
        if (key is null)
        {
            return;
        }

        _heldKeys.Add(e.Key);
        editor.CaptureKeyboardKey(key.Value, ToModifiers(e.KeyModifiers));
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        var editor = _viewModel?.Editor;
        if (editor is null || !editor.IsCapturingKey)
        {
            return;
        }

        e.Handled = true;

        _heldKeys.Remove(e.Key);

        if (_heldKeys.Count == 0)
        {
            editor.EndKeyboardCapture();
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // Focus left the window mid-capture, so the remaining key-ups will never arrive and
        // the held set would stay non-empty forever.
        _heldKeys.Clear();
        _viewModel?.Editor.EndKeyboardCapture();
    }

    private static CoreKeyModifiers ToModifiers(Avalonia.Input.KeyModifiers modifiers)
    {
        var result = CoreKeyModifiers.None;

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            result |= CoreKeyModifiers.Ctrl;
        }

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
        {
            result |= CoreKeyModifiers.Shift;
        }

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt))
        {
            result |= CoreKeyModifiers.Alt;
        }

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta))
        {
            result |= CoreKeyModifiers.Win;
        }

        return result;
    }
}
