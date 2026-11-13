using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Platform;

/// <summary>
/// Translates engine commands into sink calls while tracking modifier state, so a
/// combo tap presses Ctrl once, taps C, and releases both in the right order.
/// </summary>
/// <remarks>
/// <para>
/// Held keys, modifiers and mouse buttons are refcounted per <see cref="OutputOwner"/> in a
/// table shared by every dispatcher of one session: an output goes down when its first owner
/// presses it and comes back up only when the last one releases it. That is what keeps a
/// second mapping's Ctrl combo from lifting a modifier another mapping still holds, two
/// macros sharing Ctrl from cancelling each other, two mappings sharing the left mouse
/// button from dropping the other's click, and two pads outputting one key from stealing it
/// from one another.
/// </para>
/// <para>
/// Refcounting is deliberately about outputs the mapper itself asked for. A real keyboard
/// holding the same key is a separate input-mixing concern: nothing here claims to make a
/// physical keypress and a mapped one coexist.
/// </para>
/// </remarks>
public sealed class CommandDispatcher
{
    private static readonly GamepadKey[] ModifierOrder =
    [
        GamepadKey.LeftCtrl,
        GamepadKey.LeftShift,
        GamepadKey.LeftAlt,
        GamepadKey.LeftWin,
    ];

    private readonly OutputOwnership _ownership;
    private readonly List<OutputOwner> _owners = [];
    private readonly HashSet<OutputOwner> _ownerSet = [];

    public CommandDispatcher(IOutputSink sink, OutputOwnership? ownership = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        Sink = sink;
        _ownership = ownership ?? new OutputOwnership();
    }

    public IOutputSink Sink { get; }

    /// <summary>True while anything in the session is held.</summary>
    public bool HasHeldInput => _ownership.HasHeldInput;

    public void Dispatch(InputCommand command)
    {
        switch (command)
        {
            case KeyDownCommand down:
                Remember(down.Owner);
                PressModifiers(down.Modifiers, down.Owner);
                PressKey(down.Key, down.Owner);
                break;
            case KeyUpCommand up:
                Remember(up.Owner);
                ReleaseKey(up.Key, up.Owner);
                ReleaseModifiers(up.Modifiers, up.Owner);
                break;
            case MouseMoveCommand move:
                Sink.MouseMove(move.Dx, move.Dy);
                break;
            case WheelCommand wheel:
                Sink.ScrollWheel(wheel.HorizontalNotches, wheel.VerticalNotches);
                break;
            case MouseDownCommand mouseDown:
                Remember(mouseDown.Owner);
                _ownership.PressMouseButton(Sink, mouseDown.Button, mouseDown.Owner);
                break;
            case MouseUpCommand mouseUp:
                Remember(mouseUp.Owner);
                _ownership.ReleaseMouseButton(Sink, mouseUp.Button, mouseUp.Owner);
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

    /// <summary>
    /// Releases everything this dispatcher's owners hold - safety net for profile switches,
    /// pauses and disconnects. Outputs another device's owners still hold are left alone: a
    /// revoked owner never lifts a key it does not own.
    /// </summary>
    public void ReleaseAll()
    {
        for (var i = _owners.Count - 1; i >= 0; i--)
        {
            _ownership.Revoke(Sink, _owners[i]);
        }

        _owners.Clear();
        _ownerSet.Clear();
    }

    /// <summary>
    /// Releases only what <paramref name="owner"/> holds, leaving every other owner's outputs
    /// in place. The cancel path for one mapping or one macro instance: a revoked owner never
    /// lifts a key it does not own.
    /// </summary>
    public void Revoke(OutputOwner owner) => _ownership.Revoke(Sink, owner);

    /// <summary>Remembers an owner this dispatcher has dispatched for, so ReleaseAll can revoke it.</summary>
    private void Remember(OutputOwner owner)
    {
        if (_ownerSet.Add(owner))
        {
            _owners.Add(owner);
        }
    }

    private void PressModifiers(KeyModifiers modifiers, OutputOwner owner)
    {
        foreach (var modifier in ModifierOrder)
        {
            if (ModifierMatches(modifier, modifiers))
            {
                _ownership.PressModifier(Sink, modifier, owner);
            }
        }
    }

    private void ReleaseModifiers(KeyModifiers modifiers, OutputOwner owner)
    {
        foreach (var modifier in ModifierOrder.Reverse())
        {
            if (ModifierMatches(modifier, modifiers))
            {
                _ownership.ReleaseModifier(Sink, modifier, owner);
            }
        }
    }

    private void PressKey(GamepadKey key, OutputOwner owner) => _ownership.PressKey(Sink, key, owner);

    private void ReleaseKey(GamepadKey key, OutputOwner owner) => _ownership.ReleaseKey(Sink, key, owner);

    private static bool ModifierMatches(GamepadKey modifier, KeyModifiers modifiers) => modifier switch
    {
        GamepadKey.LeftCtrl => modifiers.HasFlag(KeyModifiers.Ctrl),
        GamepadKey.LeftShift => modifiers.HasFlag(KeyModifiers.Shift),
        GamepadKey.LeftAlt => modifiers.HasFlag(KeyModifiers.Alt),
        GamepadKey.LeftWin => modifiers.HasFlag(KeyModifiers.Win),
        _ => false,
    };
}

/// <summary>
/// The session-wide record of which outputs are held and who holds them. One instance is
/// shared by every dispatcher that injects into the same desktop, so two devices never
/// disagree about whether a key is down.
/// <para>
/// Keys and modifiers share one group on purpose: LeftCtrl is one physical output whether a
/// mapping holds it as a key or a combo asks for it as a modifier, and splitting the two is
/// what let a combo's end lift a modifier another mapping was still holding. Every output
/// keeps its own set of owners plus the order it was first pressed in, which is what
/// <see cref="Revoke"/> and <see cref="ReleaseAll"/> unwind.
/// </para>
/// </summary>
public sealed class OutputOwnership
{
    private readonly HoldSet<GamepadKey> _keys = new();
    private readonly HoldSet<MouseButton> _mouseButtons = new();

    /// <summary>True while anything at all is held.</summary>
    public bool HasHeldInput => !_keys.IsEmpty || !_mouseButtons.IsEmpty;

    public void PressKey(IOutputSink sink, GamepadKey key, OutputOwner owner)
    {
        if (_keys.Add(key, owner))
        {
            sink.KeyDown(key);
        }
    }

    public void ReleaseKey(IOutputSink sink, GamepadKey key, OutputOwner owner)
    {
        if (_keys.Remove(key, owner))
        {
            sink.KeyUp(key);
        }
    }

    /// <summary>
    /// Presses a modifier. Identical to <see cref="PressKey"/> - a modifier is a key - and
    /// kept separate only so the dispatcher can keep pressing them in canonical order.
    /// </summary>
    public void PressModifier(IOutputSink sink, GamepadKey modifier, OutputOwner owner)
        => PressKey(sink, modifier, owner);

    public void ReleaseModifier(IOutputSink sink, GamepadKey modifier, OutputOwner owner)
        => ReleaseKey(sink, modifier, owner);

    public void PressMouseButton(IOutputSink sink, MouseButton button, OutputOwner owner)
    {
        if (_mouseButtons.Add(button, owner))
        {
            sink.MouseButtonDown(button);
        }
    }

    public void ReleaseMouseButton(IOutputSink sink, MouseButton button, OutputOwner owner)
    {
        if (_mouseButtons.Remove(button, owner))
        {
            sink.MouseButtonUp(button);
        }
    }

    /// <summary>
    /// Releases everything <paramref name="owner"/> holds, in reverse press order within each
    /// group and with the keys before the mouse buttons, exactly as <see cref="ReleaseAll"/>
    /// unwinds a combo. Outputs another owner still holds stay down.
    /// </summary>
    public void Revoke(IOutputSink sink, OutputOwner owner)
    {
        foreach (var key in _keys.InReversePressOrder())
        {
            if (_keys.Remove(key, owner))
            {
                sink.KeyUp(key);
            }
        }

        foreach (var button in _mouseButtons.InReversePressOrder())
        {
            if (_mouseButtons.Remove(button, owner))
            {
                sink.MouseButtonUp(button);
            }
        }
    }

    /// <summary>
    /// Releases everything held, whatever owns it. The abnormal-exit safety net: keys in
    /// reverse press order - which is the order a combo unwinds in - then mouse buttons in
    /// reverse press order.
    /// </summary>
    public void ReleaseAll(IOutputSink sink)
    {
        foreach (var key in _keys.InReversePressOrder())
        {
            sink.KeyUp(key);
        }

        _keys.Clear();

        foreach (var button in _mouseButtons.InReversePressOrder())
        {
            sink.MouseButtonUp(button);
        }

        _mouseButtons.Clear();
    }

    /// <summary>
    /// One group of held outputs: which owners hold each one, plus the order the outputs were
    /// first pressed in. An output with no owners left is dropped, so the press order stays
    /// the order of the outputs that are still down.
    /// </summary>
    private sealed class HoldSet<T>
        where T : notnull
    {
        private readonly Dictionary<T, HashSet<OutputOwner>> _owners = [];
        private readonly List<T> _pressOrder = [];

        public bool IsEmpty => _owners.Count == 0;

        /// <summary>
        /// Adds an owner; true when this press is the one that put the output down. A later
        /// owner pressing an output that is already down adds itself to the owner set and
        /// sends nothing, so the OS never sees a second press for one physical hold.
        /// </summary>
        public bool Add(T key, OutputOwner owner)
        {
            if (!_owners.TryGetValue(key, out var set))
            {
                set = [];
                _owners[key] = set;
                _pressOrder.Add(key);
                set.Add(owner);
                return true;
            }

            set.Add(owner);
            return false;
        }

        /// <summary>Removes an owner; true when this release is the one that lifted the output.</summary>
        public bool Remove(T key, OutputOwner owner)
        {
            if (!_owners.TryGetValue(key, out var set) || !set.Remove(owner))
            {
                return false;
            }

            if (set.Count == 0)
            {
                _owners.Remove(key);
                _pressOrder.Remove(key);
                return true;
            }

            return false;
        }

        public bool Has(T key) => _owners.ContainsKey(key);

        public void Clear()
        {
            _owners.Clear();
            _pressOrder.Clear();
        }

        /// <summary>
        /// The held outputs in reverse press order. Materialized, because the caller removes
        /// entries while walking it.
        /// </summary>
        public IEnumerable<T> InReversePressOrder() => _pressOrder.ToArray().AsEnumerable().Reverse();
    }
}
