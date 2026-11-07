using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Platform;

/// <summary>
/// Translates engine commands into sink calls while tracking modifier state, so a
/// combo tap presses Ctrl once, taps C, and releases both in the right order.
/// </summary>
public sealed class CommandDispatcher
{
    private static readonly GamepadKey[] ModifierOrder =
    [
        GamepadKey.LeftCtrl,
        GamepadKey.LeftShift,
        GamepadKey.LeftAlt,
        GamepadKey.LeftWin,
    ];

    private readonly HashSet<GamepadKey> _heldKeys = [];
    private readonly HashSet<GamepadKey> _heldModifiers = [];
    private readonly HashSet<MouseButton> _heldMouseButtons = [];

    public CommandDispatcher(IOutputSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        Sink = sink;
    }

    public IOutputSink Sink { get; }

    public bool HasHeldInput => _heldKeys.Count > 0 || _heldModifiers.Count > 0 || _heldMouseButtons.Count > 0;

    public void Dispatch(InputCommand command)
    {
        switch (command)
        {
            case KeyDownCommand down:
                PressModifiers(down.Modifiers);
                PressKey(down.Key);
                break;
            case KeyUpCommand up:
                ReleaseKey(up.Key);
                ReleaseModifiers(up.Modifiers);
                break;
            case MouseMoveCommand move:
                Sink.MouseMove(move.Dx, move.Dy);
                break;
            case WheelCommand wheel:
                Sink.ScrollWheel(wheel.HorizontalNotches, wheel.VerticalNotches);
                break;
            case MouseDownCommand mouseDown:
                if (_heldMouseButtons.Add(mouseDown.Button))
                {
                    Sink.MouseButtonDown(mouseDown.Button);
                }

                break;
            case MouseUpCommand mouseUp:
                if (_heldMouseButtons.Remove(mouseUp.Button))
                {
                    Sink.MouseButtonUp(mouseUp.Button);
                }

                break;

            case SwitchPresetCommand:
                // Host-side only: a preset switch never reaches the output sink. The host
                // applies it by swapping the active profile, so the dispatcher drops it.
                break;
        }
    }

    public void DispatchAll(IEnumerable<InputCommand> commands)
    {
        foreach (var command in commands)
        {
            Dispatch(command);
        }
    }

    /// <summary>Releases everything currently held - safety net for profile switches and pauses.</summary>
    public void ReleaseAll()
    {
        foreach (var key in _heldKeys.Reverse())
        {
            Sink.KeyUp(key);
        }

        _heldKeys.Clear();

        foreach (var modifier in ModifierOrder.Reverse())
        {
            if (_heldModifiers.Remove(modifier))
            {
                Sink.KeyUp(modifier);
            }
        }

        foreach (var button in _heldMouseButtons.Reverse())
        {
            Sink.MouseButtonUp(button);
        }

        _heldMouseButtons.Clear();
    }

    private void PressModifiers(KeyModifiers modifiers)
    {
        foreach (var modifier in ModifierOrder)
        {
            if (ModifierMatches(modifier, modifiers) && _heldModifiers.Add(modifier))
            {
                Sink.KeyDown(modifier);
            }
        }
    }

    private void ReleaseModifiers(KeyModifiers modifiers)
    {
        foreach (var modifier in ModifierOrder.Reverse())
        {
            if (ModifierMatches(modifier, modifiers) && _heldModifiers.Remove(modifier))
            {
                Sink.KeyUp(modifier);
            }
        }
    }

    private void PressKey(GamepadKey key)
    {
        if (_heldKeys.Add(key))
        {
            Sink.KeyDown(key);
        }
    }

    private void ReleaseKey(GamepadKey key)
    {
        if (_heldKeys.Remove(key))
        {
            Sink.KeyUp(key);
        }
    }

    private static bool ModifierMatches(GamepadKey modifier, KeyModifiers modifiers) => modifier switch
    {
        GamepadKey.LeftCtrl => modifiers.HasFlag(KeyModifiers.Ctrl),
        GamepadKey.LeftShift => modifiers.HasFlag(KeyModifiers.Shift),
        GamepadKey.LeftAlt => modifiers.HasFlag(KeyModifiers.Alt),
        GamepadKey.LeftWin => modifiers.HasFlag(KeyModifiers.Win),
        _ => false,
    };
}
