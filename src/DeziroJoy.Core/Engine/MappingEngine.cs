using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Engine;

/// <summary>
/// Deterministic mapping engine: feed it normalized gamepad snapshots plus a fixed
/// timestep and it returns the output commands for that tick. No clocks, no platform
/// calls - which keeps the whole mapping semantics unit-testable.
/// </summary>
/// <remarks>
/// <para>
/// Every source x target combination is honored: button -&gt; key / combo / mouse button /
/// text / mouse move / wheel / special key, and stick -&gt; mouse move / wheel / key /
/// combo / mouse button / text / special key. A combination that cannot work is rejected
/// when the engine is built rather than silently dropped at runtime.
/// </para>
/// <para>
/// Buttons may carry a <see cref="ButtonGesture"/> (双击 / 长按). The latency rule: a button
/// with no gesture configured fires on the press edge with zero wait; configuring 双击 or
/// 长按 introduces a wait, because the single action cannot fire until the gesture resolves.
/// A gesture may also carry an action of its own (<see cref="ButtonGesture.DoubleClickAction"/>,
/// <see cref="ButtonGesture.LongPressAction"/>); without one it re-fires the mapping's target,
/// so the action fields are purely additive. All gesture timing derives from the tick's
/// <c>deltaSeconds</c>.
/// </para>
/// <para>
/// A <see cref="ComplexShortcutTarget"/> is an action of its own - the click's, the
/// double-click's or the long press's, whichever position carries it - and its trigger names
/// that position, so a program never has to describe how it is started. Its execution
/// strategy says how it ends: 按住期间 lives exactly as long as the press, 执行一次 runs to
/// its last step and releases what it held, 再按切换 is stopped by the next trigger. A second
/// trigger while it runs answers its repeat policy: restart, ignore or queue one more run.
/// The click target and the double-click action stay independent of the long press's program,
/// so a quick press still fires the click and a second press still fires the double-click;
/// the only thing a program cannot coexist with is whatever would stop its own gesture from
/// firing it (no threshold, click auto-repeat, a repeat interval). v1 bound the program to
/// the mapping's own target with a bare long press instead; the constructor rewrites that
/// shape into this one, so an old profile behaves exactly as it did - one code path runs the
/// program.
/// </para>
/// <para>
/// Mappings carry a <see cref="MappingEntry.Layer"/>. Layer 0 is the base layer and always
/// runs; layer 1 is the one temporary layer, which only runs while a leader key whose
/// <see cref="SpecialKeyTarget.Action"/> is <see cref="LeaderAction.Layer"/> has it in
/// effect. The layer inherits every base mapping it does not override, so a temporary layer
/// that only rebinds a few buttons leaves everything else - a drag, a held key, a stick's
/// mouse move - running exactly as it was. See <see cref="ActivateLayer"/> for the switch
/// rules.
/// </para>
/// </remarks>
public sealed class MappingEngine
{
    /// <summary>
    /// Per-axis deflection at which a stick counts as pushed in an <see cref="InputDirection"/>.
    /// Half deflection is the classic digital conversion: a resting stick never reaches it and
    /// a deliberate push always does.
    /// </summary>
    internal const float StickDigitalThreshold = 0.5f;

    /// <summary>
    /// The largest timestep displacement is integrated for, on every tick. A tick that covers
    /// more real time than this moves the pointer one bounded step, never the whole gap: a
    /// 10 s stall must not teleport the pointer 10 s worth of stick deflection. Sub-pixel
    /// residuals are kept, so nothing that did fit inside the bound is lost.
    /// </summary>
    internal const float MaxIntegrationSeconds = 0.25f;

    /// <summary>
    /// A tick longer than this is a sampling gap, not jitter: the process was suspended, the
    /// machine slept, or the loop thread was starved past any point where catching up means
    /// anything. Such a tick clears every accumulated cycle and re-adopts the freshly polled
    /// snapshot as the edge baseline (see <see cref="RecoverFromSamplingGap"/>), so the tick
    /// that follows starts from the input the user is actually holding instead of replaying
    /// whatever the gap covered. Below it a tick is ordinary jitter and is integrated whole,
    /// which is what keeps periodic catch-up from losing a unit.
    /// </summary>
    internal const float SamplingGapSeconds = 3f;

    /// <summary>
    /// Identifies one gesture action's smoothing processor: a mapping plus a gesture.
    /// </summary>
    private readonly record struct GestureProcessorKey(Guid MappingId, GestureKind Kind);

    /// <summary>
    /// Identifies one complex-shortcut program: a mapping plus the gesture position the
    /// program rides in. A mapping may carry up to three - one per position - so the click,
    /// the double-click and the long press can each run their own program.
    /// </summary>
    private readonly record struct ProgramSlot(Guid MappingId, GestureKind Kind);

    /// <summary>One precision special-key mapping: where it listens and how it engages.</summary>
    private readonly record struct SpecialPrecisionKey(Guid MappingId, InputBinding Source, PrecisionMode Mode);

    /// <summary>
    /// The two timesteps one tick runs on. <see cref="Action"/> drives gesture and program
    /// timing and keeps the whole tick while the tick is ordinary jitter, so a periodic tap
    /// never loses a unit; <see cref="Integration"/> drives displacement and is bounded, so a
    /// long tick can never teleport the pointer. A sampling gap collapses both onto the bound.
    /// </summary>
    private readonly record struct TickSteps(float Action, float Integration);

    private readonly GamepadProfile _profile;
    private readonly List<MappingEntry> _mappings = [];
    private readonly List<MappingEntry> _baseMappings = [];
    private readonly List<MappingEntry> _layerMappings = [];
    private readonly Dictionary<Guid, MouseStickProcessor> _mouseProcessors = [];
    private readonly Dictionary<GestureProcessorKey, MouseStickProcessor> _gestureMouseProcessors = [];
    private readonly Dictionary<Guid, ScrollStickProcessor> _scrollProcessors = [];
    private readonly Dictionary<Guid, ButtonGestureState> _gestures = [];
    private readonly SortedSet<int> _previousExtraButtons = [];
    private readonly HashSet<GamepadButton> _togglePrecisionButtons = [];
    private readonly Dictionary<GamepadButton, bool> _precisionLatches = [];
    private readonly List<SpecialPrecisionKey> _specialPrecisionKeys = [];
    private readonly HashSet<Guid> _engagedPrecisionKeys = [];
    private readonly Dictionary<Guid, bool> _specialKeyWasActive = [];
    private readonly Dictionary<Guid, bool> _leaderLatches = [];
    private readonly Dictionary<Guid, List<ProgramSlot>> _programSlots = [];
    private readonly Dictionary<ProgramSlot, ComplexShortcutRuntime> _programs = [];
    private readonly HashSet<ProgramSlot> _queuedRestarts = [];
    private List<MappingEntry> _effectiveMappings = [];
    private int _activeLayerId;

    /// <summary>
    /// The leader keys currently holding the temporary layer in effect. The layer stays
    /// while any of them holds it, so releasing one of several leaders leaves it to the
    /// others instead of dropping it under a key that is still down.
    /// </summary>
    private readonly HashSet<Guid> _layerHolders = [];
    private GamepadButtonMask _previousButtons;

    public MappingEngine(GamepadProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;

        // The engine runs its own normalized copy of the profile's mappings: v1 complex
        // shortcuts are rewritten into the gesture's long-press action here, and the
        // deprecated layer key is rewritten into the leader that replaced it, so every
        // later pass - Tick, SeedLeaderLatches - sees exactly one shape.
        foreach (var mapping in _profile.Mappings)
        {
            if (!mapping.Enabled)
            {
                continue;
            }

            var normalized = NormalizeComplexShortcut(mapping);
            normalized = NormalizeLayerLeader(normalized);
            _mappings.Add(normalized);

            ValidateMapping(normalized);
            RegisterGestureProcessors(normalized);
            RegisterPrograms(normalized);

            switch (normalized.Target)
            {
                case MouseMoveTarget mouse:
                    _mouseProcessors[normalized.Id] = new MouseStickProcessor(mouse.Settings);
                    CollectTogglePrecisionButton(mouse.Settings);
                    break;
                case WheelScrollTarget wheel:
                    _scrollProcessors[normalized.Id] = new ScrollStickProcessor(wheel.Settings);
                    break;
                case SpecialKeyTarget special:
                    RegisterSpecialKey(normalized, special);
                    break;
            }
        }

        PartitionLayers();
        ValidateLayerReferences();
        RebuildEffective();
        SeedLeaderLatches();
    }

    /// <summary>
    /// Rewrites a v1 complex shortcut - the program as the mapping's own target with a bare
    /// long press - into the current shape: the program as the gesture's long-press action
    /// and a 无功能 click, which is what a quick press did when the program owned the
    /// target. Only the exact shape v1 accepted is rewritten; anything else keeps its
    /// target and is rejected by <see cref="ValidateMapping"/>.
    /// </summary>
    private static MappingEntry NormalizeComplexShortcut(MappingEntry mapping)
    {
        if (mapping.Target is not ComplexShortcutTarget complex
            || mapping.Source is not ButtonBinding button
            || button.Gesture is not { } gesture
            || !gesture.HasEffectiveLongPress
            || gesture.LongPressAction is not null)
        {
            return mapping;
        }

        // A program whose trigger is not the long press is not the v1 shape: it says for
        // itself which position it runs from, so rewriting it would contradict it.
        if (complex.Trigger != ActionTrigger.LongPress)
        {
            return mapping;
        }

        return mapping with
        {
            Target = new NoneTarget(),
            Source = button with
            {
                Gesture = gesture with { LongPressAction = complex },
            },
        };
    }

    /// <summary>
    /// Rewrites the deprecated <see cref="SpecialKeyKind.Layer"/> target into the leader
    /// that replaced it: a leader whose action switches the one temporary layer. Profiles
    /// written before the merge are migrated on load; this is the safety net for one that
    /// was not, so the engine has exactly one layer-leader shape to run.
    /// </summary>
    private static MappingEntry NormalizeLayerLeader(MappingEntry mapping)
    {
        if (mapping.Target is not SpecialKeyTarget { Kind: SpecialKeyKind.Layer } layer)
        {
            return mapping;
        }

        return mapping with
        {
            Target = new SpecialKeyTarget(
                SpecialKeyKind.Leader,
                layer.PrecisionMode,
                layer.SwitchMode,
                layer.SlotA,
                layer.SlotB,
                LeaderAction.Layer),
        };
    }

    /// <summary>
    /// Compiles every program a mapping carries, keyed by the gesture position it rides in:
    /// the mapping's own target, the double-click action or the long-press action. The
    /// position is the program's trigger, so a mapping can run one program per position and
    /// the three never contend for the same press.
    /// </summary>
    private void RegisterPrograms(MappingEntry mapping)
    {
        if (mapping.Source is not ButtonBinding button)
        {
            // A stick has no gestures, so only its own target could carry a program - and
            // ValidateMapping refuses that before this runs.
            RegisterProgram(mapping.Id, GestureKind.Click, mapping.Target as ComplexShortcutTarget);
            return;
        }

        RegisterProgram(mapping.Id, GestureKind.Click, mapping.Target as ComplexShortcutTarget);
        RegisterProgram(mapping.Id, GestureKind.DoubleClick, button.Gesture?.DoubleClickAction as ComplexShortcutTarget);
        RegisterProgram(mapping.Id, GestureKind.LongPress, button.Gesture?.LongPressAction as ComplexShortcutTarget);
    }

    private void RegisterProgram(Guid mappingId, GestureKind kind, ComplexShortcutTarget? program)
    {
        if (program is null)
        {
            return;
        }

        var slot = new ProgramSlot(mappingId, kind);
        _programs[slot] = CompileComplexShortcut(mappingId, program);

        if (!_programSlots.TryGetValue(mappingId, out var slots))
        {
            slots = [];
            _programSlots[mappingId] = slots;
        }

        slots.Add(slot);
    }

    /// <summary>
    /// Compiles a complex shortcut's script when the engine is built, so a hand-edited
    /// profile fails here rather than on the first trigger. The parse error keeps its
    /// Chinese reason and line/column, wrapped in the ArgumentException every other
    /// rejected mapping throws.
    /// </summary>
    private static ComplexShortcutRuntime CompileComplexShortcut(Guid mappingId, ComplexShortcutTarget program)
    {
        try
        {
            // The program is its own owner: it holds keys a mapping never held, so the
            // dispatcher refcounts it apart from the mapping that started it.
            return ComplexShortcutRuntime.Compile(program, new OutputOwner(Guid.NewGuid()));
        }
        catch (ComplexShortcutParseException ex)
        {
            throw new ArgumentException(
                $"Mapping {mappingId} carries a complex shortcut script that does not parse: {ex.Message}",
                nameof(mappingId),
                ex);
        }
    }

    public IReadOnlyList<InputCommand> Tick(IGamepadState current, float deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(current);

        var commands = new List<InputCommand>();
        var pressed = current.PressedButtons;
        var steps = StepsFor(deltaSeconds, current);
        UpdatePrecisionState(current);

        // Layer leaders resolve before every other mapping: a frame that presses the leader
        // and another button together must run the layer's version of that button, never the
        // base one, and the layer's own state has to be settled before the loop below reads
        // the mappings it puts in effect.
        foreach (var mapping in _effectiveMappings)
        {
            if (mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader, Action: LeaderAction.Layer })
            {
                UpdateMapping(mapping, current, pressed, steps, commands);
            }
        }

        foreach (var mapping in _effectiveMappings)
        {
            if (mapping.Target is not SpecialKeyTarget { Kind: SpecialKeyKind.Leader, Action: LeaderAction.Layer })
            {
                UpdateMapping(mapping, current, pressed, steps, commands);
            }
        }

        _previousButtons = pressed;
        RefreshPreviousExtraButtons(current);
        return commands;
    }

    private void UpdateMapping(
        MappingEntry mapping,
        IGamepadState current,
        GamepadButtonMask pressed,
        TickSteps steps,
        List<InputCommand> commands)
    {
        switch (mapping.Source)
        {
            case ButtonBinding button when button.Button == GamepadButton.Extra:
                UpdateExtraButton(mapping, button, current, steps, commands);
                break;

            case ButtonBinding button:
                var mask = button.Button.ToMask();
                UpdateButton(
                    mapping,
                    button,
                    (pressed & mask) != 0,
                    (_previousButtons & mask) != 0,
                    current,
                    steps,
                    commands);
                break;

            case StickBinding stick:
                UpdateStick(mapping, stick, current, steps, commands);
                break;
        }
    }

    /// <summary>
    /// The timesteps this tick runs on, after the sampling-gap check. A gap clears every
    /// accumulated cycle and re-adopts the freshly polled snapshot as the edge baseline, so
    /// nothing the gap covered is replayed.
    /// </summary>
    private TickSteps StepsFor(float deltaSeconds, IGamepadState current)
    {
        var requested = Sanitize(deltaSeconds);

        if (requested > SamplingGapSeconds)
        {
            RecoverFromSamplingGap(current);
            return new TickSteps(MaxIntegrationSeconds, MaxIntegrationSeconds);
        }

        return new TickSteps(requested, Math.Min(requested, MaxIntegrationSeconds));
    }

    private static float Sanitize(float deltaSeconds)
        => float.IsNaN(deltaSeconds) || deltaSeconds < 0f ? 0f : deltaSeconds;

    /// <summary>
    /// Recovery from a sampling gap. Every accumulated cycle - gesture machines, smoothing
    /// processors, running programs - is cleared and the freshly polled snapshot becomes the
    /// edge baseline, exactly as <see cref="AdoptBaseline"/> does for a profile reload: a
    /// button the user never re-pressed must not fabricate a press edge, and a stick left
    /// deflected moves one bounded step instead of the whole gap. Precision latches are user
    /// state rather than accumulated time, so they survive.
    /// </summary>
    private void RecoverFromSamplingGap(IGamepadState current)
    {
        foreach (var state in _gestures.Values)
        {
            state.Reset();
        }

        foreach (var processor in _gestureMouseProcessors.Values)
        {
            processor.Reset();
        }

        foreach (var processor in _mouseProcessors.Values)
        {
            processor.Reset();
        }

        foreach (var processor in _scrollProcessors.Values)
        {
            processor.Reset();
        }

        foreach (var runtime in _programs.Values)
        {
            // Silent by design: the host pairs the same clearing with
            // CommandDispatcher.ReleaseAll, which lifts whatever the program held.
            runtime.Reset();
        }

        AdoptBaseline(current);
    }

    private void RefreshPreviousExtraButtons(IGamepadState current)
    {
        _previousExtraButtons.Clear();
        foreach (var code in current.ExtraButtons)
        {
            _previousExtraButtons.Add(code);
        }
    }

    /// <summary>Clears edge detection, gesture and smoothing state (profile switch, engine pause).</summary>
    /// <remarks>
    /// The processors themselves are registered once, when the engine is built, and are only
    /// ever reset here: a mapping or a gesture action whose continuous target moves the mouse
    /// must keep ramping through the same processor after a pause or a profile reload, exactly
    /// as the mapping's own stick processor does. The active layer goes back to the base
    /// layer; the host pairs this with <see cref="Platform.CommandDispatcher.ReleaseAll"/>,
    /// which lifts everything the layer's mappings held.
    /// </remarks>
    public void Reset()
    {
        _previousButtons = GamepadButtonMask.None;
        _previousExtraButtons.Clear();
        _gestures.Clear();
        foreach (var processor in _gestureMouseProcessors.Values)
        {
            processor.Reset();
        }

        _precisionLatches.Clear();
        _engagedPrecisionKeys.Clear();
        _specialKeyWasActive.Clear();
        SeedLeaderLatches();
        DeactivateLayer();
        _queuedRestarts.Clear();
        foreach (var runtime in _programs.Values)
        {
            // Silent by design: the host pairs Reset with CommandDispatcher.ReleaseAll,
            // which lifts the keys a running complex shortcut held.
            runtime.Reset();
        }

        foreach (var processor in _mouseProcessors.Values)
        {
            processor.Reset();
        }

        foreach (var processor in _scrollProcessors.Values)
        {
            processor.Reset();
        }
    }

    /// <summary>
    /// Adopts a just-polled input snapshot as the edge baseline without ticking: every button
    /// it reports held counts as already-pressed, so no press edge is fabricated for input the
    /// user never re-pressed, while a release still forms a real edge. The host primes a
    /// freshly built engine this way after a profile reload: the reload discards the old
    /// engine's edge memory while the pad keeps reporting the same held buttons, and without
    /// this the next tick would see a fake press edge for each of them - re-firing a held key
    /// the reload just released, or toggling a leader key once per held tick. Gesture state is
    /// deliberately left alone: a fresh engine starts every cycle at Idle, so an in-flight
    /// gesture is cancelled rather than resumed and a held button never re-arms mid-cycle.
    /// </summary>
    public void AdoptBaseline(IGamepadState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _previousButtons = state.PressedButtons;
        RefreshPreviousExtraButtons(state);
    }

    /// <summary>
    /// The temporary layer currently in effect: 1 while a leader key has it in effect, 0 for
    /// the base layer alone.
    /// </summary>
    public int ActiveLayerId => _activeLayerId;

    /// <summary>
    /// The mappings in effect right now: every base mapping, with the temporary layer's own
    /// mappings overriding the ones that listen on the same physical input. Exposed so the
    /// daemon's snapshot can report what a device is really running.
    /// </summary>
    public IReadOnlyList<MappingEntry> EffectiveMappings => _effectiveMappings;

    /// <summary>
    /// Builds the smoothing processor a gesture action needs when it names a continuous target.
    /// A gesture action is only ever tapped, so this exists purely so a mouse-moving gesture
    /// action ramps exactly like the mapping's own target would.
    /// </summary>
    private void RegisterGestureProcessors(MappingEntry mapping)
    {
        if (mapping.Source is not ButtonBinding { Gesture: { } gesture })
        {
            return;
        }

        RegisterGestureProcessor(mapping.Id, GestureKind.DoubleClick, gesture.DoubleClickAction);
        RegisterGestureProcessor(mapping.Id, GestureKind.LongPress, gesture.LongPressAction);
    }

    private void RegisterGestureProcessor(Guid mappingId, GestureKind kind, MappingTarget? action)
    {
        if (action is MouseMoveTarget mouse)
        {
            _gestureMouseProcessors[new GestureProcessorKey(mappingId, kind)] = new MouseStickProcessor(mouse.Settings);
            CollectTogglePrecisionButton(mouse.Settings);
        }
    }

    /// <summary>
    /// Splits the profile's mappings into the base layer and the one temporary layer above
    /// it. The base layer always runs; the temporary layer only runs while a leader key
    /// switches it in. A layer value above 1 is what a profile written before the
    /// single-layer model carries - migration folds it into 1 - so any non-zero value is
    /// read as the temporary layer.
    /// </summary>
    private void PartitionLayers()
    {
        foreach (var mapping in _mappings)
        {
            if (mapping.Layer == 0)
            {
                _baseMappings.Add(mapping);
                continue;
            }

            _layerMappings.Add(mapping);
        }
    }

    /// <summary>
    /// The physical input a mapping listens on, with the gesture left out: a layer entry
    /// overrides the base mapping on the same button or the same stick direction, whatever
    /// gesture either of them carries.
    /// </summary>
    private static InputBinding SourceKeyOf(InputBinding source) => source switch
    {
        ButtonBinding button => new ButtonBinding(button.Button, button.Code) { Direction = button.Direction },
        StickBinding stick => new StickBinding(stick.Stick) { Direction = stick.Direction },
        _ => source,
    };

    /// <summary>
    /// Rejects profiles whose temporary layer could never behave. The layer is entered
    /// through a leader key that switches it, so an overlay no leader names would silently
    /// never fire; a leader that switches the layer is refused on a layer mapping, because
    /// that mapping is not in effect until the layer is already active; and a layer mapping
    /// is refused on the same input as its leader, because activating the layer would take
    /// the leader out of effect and nothing would ever leave it again.
    /// </summary>
    private void ValidateLayerReferences()
    {
        var leaderSources = new HashSet<InputBinding>();

        foreach (var mapping in _mappings)
        {
            if (mapping.Target is not SpecialKeyTarget { Kind: SpecialKeyKind.Leader, Action: LeaderAction.Layer })
            {
                continue;
            }

            if (mapping.Layer != 0)
            {
                throw new ArgumentException(
                    $"Mapping {mapping.Id} switches the temporary layer from layer {mapping.Layer}. " +
                    "The temporary layer is named from the base layer only: a leader that sits " +
                    "inside the layer is not in effect until the layer is already active.",
                    nameof(mapping));
            }

            leaderSources.Add(SourceKeyOf(mapping.Source));
        }

        if (_layerMappings.Count > 0 && leaderSources.Count == 0)
        {
            throw new ArgumentException(
                "The temporary layer carries mappings but no leader key names it. The layer " +
                "is entered through a leader key whose action switches the temporary layer.",
                nameof(_profile));
        }

        // A layer entry listening on the same input as its own leader would take the leader
        // out of effect the moment the layer activates, and nothing would ever leave it.
        foreach (var mapping in _layerMappings)
        {
            if (leaderSources.Contains(SourceKeyOf(mapping.Source)))
            {
                throw new ArgumentException(
                    $"Mapping {mapping.Id} listens on {Describe(mapping.Source)} in the temporary " +
                    "layer, the same input that layer's leader key listens on. Activating the layer " +
                    "would take the leader out of effect, and nothing would ever leave the layer again.",
                    nameof(mapping));
            }
        }
    }

    /// <summary>A short human-readable name for one physical input, for rejection messages.</summary>
    private static string Describe(InputBinding source) => source switch
    {
        ButtonBinding { Button: GamepadButton.Extra, Code: var code } => $"extra button {code}",
        ButtonBinding button => button.Button.ToString(),
        StickBinding stick => $"{stick.Stick} {stick.Direction}",
        _ => source.GetType().Name,
    };

    /// <summary>
    /// Recomputes the mappings in effect: every base mapping, with the temporary layer's own
    /// mappings overriding the ones that listen on the same physical input. A layer entry that
    /// repeats its base mapping exactly is skipped, so an identical continuous action - a drag,
    /// a held key, a stick's mouse move - keeps running through the base mapping's own state
    /// instead of being cancelled and re-pressed on every switch.
    /// </summary>
    private void RebuildEffective()
    {
        var effective = new List<MappingEntry>(_baseMappings);

        if (_activeLayerId != 0)
        {
            foreach (var mapping in _layerMappings)
            {
                var index = effective.FindIndex(candidate =>
                    SourceKeyOf(candidate.Source) == SourceKeyOf(mapping.Source));

                if (index < 0)
                {
                    effective.Add(mapping);
                    continue;
                }

                var replaced = effective[index];
                if (replaced.Source == mapping.Source
                    && replaced.Target == mapping.Target
                    && replaced.Enabled == mapping.Enabled)
                {
                    continue;
                }

                effective[index] = mapping;
            }
        }

        _effectiveMappings = effective;
    }

    /// <summary>
    /// Enters the temporary layer while its leader is held. The layer inherits every base
    /// mapping it does not override, so the switch only touches what the layer actually
    /// changes: see <see cref="SwitchLayer"/>. A leader that arrives while the layer is
    /// already in effect simply joins the ones holding it.
    /// </summary>
    private void ActivateLayer(Guid leaderId, IGamepadState current, List<InputCommand> commands)
    {
        _layerHolders.Add(leaderId);

        if (_activeLayerId != 0)
        {
            return;
        }

        SwitchLayer(1, current, commands);
    }

    /// <summary>
    /// Leaves the temporary layer when its leader is released, handing every source it took
    /// over back to the base layer: see <see cref="SwitchLayer"/>. The layer stays while
    /// another leader still holds it, so releasing one of several leaves it to the rest.
    /// </summary>
    private void DeactivateLayer(Guid leaderId, IGamepadState current, List<InputCommand> commands)
    {
        if (!_layerHolders.Remove(leaderId) || _layerHolders.Count > 0)
        {
            return;
        }

        SwitchLayer(0, current, commands);
    }

    /// <summary>
    /// Flips the temporary layer on each new press edge of a Press-mode leader: the layer
    /// stays in effect after the release, the way a Press-mode preset leader leaves the slot
    /// it switched to. Leaving it hands every source it took over back to the base layer.
    /// </summary>
    private void ToggleLayer(Guid leaderId, IGamepadState current, List<InputCommand> commands)
    {
        if (_activeLayerId != 0)
        {
            _layerHolders.Clear();
            SwitchLayer(0, current, commands);
            return;
        }

        ActivateLayer(leaderId, current, commands);
    }

    /// <summary>Drops the active layer without emitting anything (profile switch, engine pause).</summary>
    private void DeactivateLayer()
    {
        if (_activeLayerId == 0)
        {
            return;
        }

        _layerHolders.Clear();
        _activeLayerId = 0;
        RebuildEffective();
    }

    /// <summary>
    /// Moves the temporary layer in or out and re-derives the activation of every source
    /// whose effective mapping moved with it: the activation the old mapping owned is
    /// cancelled, and the new mapping adopts one if the user is still holding the source. A
    /// source whose effective mapping did not change is not touched at all, and neither is
    /// one the layer added that nothing is holding. The result is deterministic whatever
    /// order the two layers list their mappings in.
    /// </summary>
    private void SwitchLayer(int layerId, IGamepadState current, List<InputCommand> commands)
    {
        var before = EffectiveBySource();
        _activeLayerId = layerId;
        RebuildEffective();
        var after = EffectiveBySource();

        foreach (var (source, previous) in before)
        {
            var next = after.GetValueOrDefault(source);

            // Unchanged: the layer left this source alone, so its activation carries on.
            if (next is not null && SameActivation(previous, next))
            {
                continue;
            }

            // The old mapping's activation is cancelled either way - the release edge that
            // would have ended it is not coming, because the mapping is no longer in effect -
            // and the new one adopts an activation if the user is still holding the source.
            CancelHold(previous, current, commands);

            if (next is not null)
            {
                AdoptHold(next, current, commands);
            }
        }

        // A source only the new layer listens on: nothing owned it before, so it simply
        // adopts an activation if the user is holding it.
        foreach (var (source, next) in after)
        {
            if (!before.ContainsKey(source))
            {
                AdoptHold(next, current, commands);
            }
        }
    }

    /// <summary>The mappings in effect, keyed by the physical input each one listens on.</summary>
    private Dictionary<InputBinding, MappingEntry> EffectiveBySource()
    {
        var bySource = new Dictionary<InputBinding, MappingEntry>();
        foreach (var mapping in _effectiveMappings)
        {
            bySource[SourceKeyOf(mapping.Source)] = mapping;
        }

        return bySource;
    }

    /// <summary>
    /// Whether a layer entry activates exactly what the base mapping it displaces already
    /// does, in which case <see cref="RebuildEffective"/> keeps the base entry and nothing
    /// about the activation changes.
    /// </summary>
    private static bool SameActivation(MappingEntry baseMapping, MappingEntry layerMapping)
        => baseMapping.Source == layerMapping.Source
            && baseMapping.Target == layerMapping.Target
            && baseMapping.Enabled == layerMapping.Enabled;

    /// <summary>
    /// Begins the activation a mapping now owns because the layer put it in effect: the user
    /// is already holding the source, so the hold starts on this tick rather than waiting for
    /// a press edge that will never come. A mapping whose source is not held is left alone -
    /// it fires on its next real press edge.
    /// </summary>
    private void AdoptHold(MappingEntry mapping, IGamepadState current, List<InputCommand> commands)
    {
        if (!SourceActive(mapping.Source, current))
        {
            return;
        }

        var state = GestureStateFor(mapping.Id);
        if (state.IsActive)
        {
            return;
        }

        // The press that produced this hold is not this machine's to resolve: it parks in
        // Holding, so the release edge ends the activation exactly as a resolved single
        // would and no gesture timing is re-run.
        state.AdoptHold();

        // The source counts as already pressed from here on, so the mapping loop below sees
        // no press edge for it and the activation is not begun a second time.
        MarkSourcePressed(mapping.Source);
        BeginHold(mapping.Target, mapping.Id, current, commands);
    }

    /// <summary>
    /// Records a source as already pressed in this tick's edge baseline, so a later pass over
    /// the same tick sees no press edge for input the user pressed before the layer moved.
    /// </summary>
    private void MarkSourcePressed(InputBinding source)
    {
        switch (source)
        {
            case ButtonBinding { Button: GamepadButton.Extra, Code: var code }:
                _previousExtraButtons.Add(code);
                break;
            case ButtonBinding button:
                _previousButtons |= button.Button.ToMask();
                break;
        }
    }

    /// <summary>
    /// Ends the activation a mapping owned before the layer took it away. A running complex
    /// shortcut stops with it, so no program outlives the mapping that started it - a long
    /// press is a tap, so its program can be running while the mapping holds nothing of its
    /// own.
    /// </summary>
    private void CancelHold(MappingEntry mapping, IGamepadState current, List<InputCommand> commands)
    {
        var state = GestureStateFor(mapping.Id);
        var ranProgram = StopProgramsOf(mapping.Id, commands);
        var wasActive = state.IsActive;

        if (!wasActive && !ranProgram)
        {
            return;
        }

        state.Reset();

        if (wasActive)
        {
            EndHold(mapping.Target, mapping.Id, current, commands);
        }
    }

    /// <summary>Whether a mapping's physical source is active in this tick's snapshot.</summary>
    private static bool SourceActive(InputBinding source, IGamepadState current)
    {
        switch (source)
        {
            case ButtonBinding button:
                return button.Button == GamepadButton.Extra
                    ? current.ExtraButtons.Contains(button.Code)
                    : current.PressedButtons.Has(button.Button);
            case StickBinding { Direction: { } direction } stick:
                return StickDirectionActive(current, stick.Stick, direction);
            case StickBinding stick:
                var (x, y) = StickAxes(current, stick.Stick);
                return x != 0f || y != 0f;
            default:
                return false;
        }
    }

    /// <summary>
    /// Remembers a precision button whose mode is <see cref="PrecisionMode.Toggle"/>, so
    /// <see cref="UpdatePrecisionLatches"/> knows which buttons to watch. Buttons shared by
    /// several mappings collapse into one entry and therefore share one latch. Only consulted
    /// on the legacy path: a profile carrying special-key precision mappings sources precision
    /// from those instead (see <see cref="UpdatePrecisionState"/>).
    /// </summary>
    private void CollectTogglePrecisionButton(MouseStickSettings settings)
    {
        if (settings.PrecisionMode == PrecisionMode.Toggle)
        {
            _togglePrecisionButtons.Add(settings.PrecisionButton);
        }
    }

    /// <summary>
    /// Refreshes precision engagement before the mapping loop, so the tick that presses a
    /// precision key already moves at precision speed. Special-key precision mappings own the
    /// button set when the profile has any; otherwise the legacy per-mapping settings path
    /// runs, which is what keeps un-migrated v1 profiles behaving exactly as before.
    /// </summary>
    private void UpdatePrecisionState(IGamepadState current)
    {
        if (_specialPrecisionKeys.Count > 0)
        {
            UpdateSpecialPrecisionKeys(current);
        }
        else
        {
            UpdatePrecisionLatches(current);
        }
    }

    /// <summary>
    /// Evaluates every special-key precision mapping for this tick. Hold engages while the
    /// source is physically active; Toggle flips its latch on each new activation edge and
    /// then survives the release, mirroring the legacy settings-based latch semantics.
    /// </summary>
    private void UpdateSpecialPrecisionKeys(IGamepadState current)
    {
        foreach (var key in _specialPrecisionKeys)
        {
            var active = SpecialKeyActive(current, key.Source);
            var wasActive = _specialKeyWasActive.GetValueOrDefault(key.MappingId);

            if (key.Mode == PrecisionMode.Toggle)
            {
                if (active && !wasActive)
                {
                    if (!_engagedPrecisionKeys.Add(key.MappingId))
                    {
                        _engagedPrecisionKeys.Remove(key.MappingId);
                    }
                }
            }
            else if (active)
            {
                _engagedPrecisionKeys.Add(key.MappingId);
            }
            else
            {
                _engagedPrecisionKeys.Remove(key.MappingId);
            }

            _specialKeyWasActive[key.MappingId] = active;
        }
    }

    /// <summary>Whether a special key's source is active right now: its button held, or its stick deflected past the threshold.</summary>
    private static bool SpecialKeyActive(IGamepadState current, InputBinding source) => source switch
    {
        ButtonBinding button => current.PressedButtons.Has(button.Button),
        StickBinding { Direction: { } direction } stick => StickDirectionActive(current, stick.Stick, direction),
        _ => false,
    };

    private static bool StickDirectionActive(IGamepadState state, GamepadStick stick, InputDirection direction)
    {
        var (x, y) = StickAxes(state, stick);
        return IsDirectionActive(x, y, direction);
    }

    /// <summary>
    /// Registers a special-key mapping. Precision keys join the button set
    /// <see cref="UpdateSpecialPrecisionKeys"/> watches; leader keys need no registration
    /// beyond validation, because their commands fire from the normal hold/tap edges.
    /// </summary>
    private void RegisterSpecialKey(MappingEntry mapping, SpecialKeyTarget special)
    {
        if (special.Kind == SpecialKeyKind.Precision)
        {
            _specialPrecisionKeys.Add(new SpecialPrecisionKey(mapping.Id, mapping.Source, special.PrecisionMode));
        }
    }

    /// <summary>
    /// Anchors every Press-mode leader's toggle to the slot the profile actually runs, so the
    /// first press after construction - or after <see cref="Reset"/> - moves to the slot the
    /// profile is not on. The host also calls this on a running engine when the active slot
    /// moved without reloading it (a preset switch that left the device on its own slot): the
    /// latch is seeded once at construction, and a leader press whose switch never landed
    /// leaves it flipped, so the next press would ask for the slot the device is already on.
    /// </summary>
    public void SyncLeaderLatches(int currentSlot)
    {
        _leaderLatches.Clear();
        foreach (var mapping in _mappings)
        {
            if (mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader, SwitchMode: LeaderSwitchMode.Press } leader)
            {
                _leaderLatches[mapping.Id] = currentSlot == leader.SlotB;
            }
        }
    }

    /// <summary>
    /// Seeds each Press-mode leader's toggle from the profile's current slot, so the first
    /// press after construction moves to the slot the profile is not on.
    /// </summary>
    private void SeedLeaderLatches() => SyncLeaderLatches(_profile.PresetSlot);

    /// <summary>
    /// Flips the latch of every Toggle-mode precision button that saw a new press edge this
    /// tick. Runs before the mapping loop, so the tick that presses the button already moves
    /// at precision speed, and reads the same previous-button snapshot the mapping edges use.
    /// </summary>
    private void UpdatePrecisionLatches(IGamepadState current)
    {
        foreach (var button in _togglePrecisionButtons)
        {
            var mask = button.ToMask();
            var isPressed = (current.PressedButtons & mask) != 0;
            var wasPressed = (_previousButtons & mask) != 0;

            if (isPressed && !wasPressed)
            {
                _precisionLatches[button] = !_precisionLatches.GetValueOrDefault(button);
            }
        }
    }

    /// <summary>
    /// Whether precision slowing is in effect for one tick. With special-key precision
    /// mappings present they own the answer and the per-mapping settings fields are inert;
    /// otherwise the legacy settings decide: Hold reads the physical button, Toggle reads the
    /// latch, which survives the button's release until the next press edge.
    /// </summary>
    private bool PrecisionActive(IGamepadState current, MouseStickSettings settings)
        => _specialPrecisionKeys.Count > 0
            ? _engagedPrecisionKeys.Count > 0
            : settings.PrecisionMode switch
            {
                PrecisionMode.Toggle => _precisionLatches.GetValueOrDefault(settings.PrecisionButton),
                _ => current.PressedButtons.Has(settings.PrecisionButton),
            };

    /// <summary>
    /// Rejects mappings that cannot work instead of silently dropping them: a continuous
    /// target driven by a button needs a direction (a button is binary), and a digital target
    /// driven by a stick needs one (a stick is an axis). The editor never produces either, so
    /// this only guards hand-edited profiles.
    /// </summary>
    private static void ValidateMapping(MappingEntry mapping)
    {
        if (mapping.Source is ButtonBinding { Gesture: { } gesture })
        {
            ValidateComplexShortcutAction(mapping, gesture);
        }

        switch (mapping.Target)
        {
            case MouseMoveTarget:
            case WheelScrollTarget:
                if (mapping.Source is ButtonBinding { Direction: null } button)
                {
                    throw new ArgumentException(
                        $"Mapping {mapping.Id} drives a continuous target from button {button.Button} " +
                        "without a direction. A button is binary: set ButtonBinding.Direction " +
                        "(Up/Down/Left/Right) to say which way it deflects.",
                        nameof(mapping));
                }

                break;

            case KeyPressTarget:
            case KeyComboTarget:
            case MouseButtonTarget:
            case TextTarget:
            case SpecialKeyTarget:
                if (mapping.Source is StickBinding { Direction: null } stick)
                {
                    throw new ArgumentException(
                        $"Mapping {mapping.Id} drives a digital target from stick {stick.Stick} " +
                        "without a direction. A stick is an axis: set StickBinding.Direction " +
                        "(Up/Down/Left/Right) to say which deflection presses it.",
                        nameof(mapping));
                }

                break;

            case ComplexShortcutTarget:
                ValidateComplexShortcutSource(mapping);
                break;
        }
    }

    /// <summary>
    /// v1 bound a complex shortcut to a button's bare 长按 and nothing else. That shape is
    /// normalized into the gesture's long-press action by <see cref="NormalizeComplexShortcut"/>
    /// before validation, so a target that still carries a program is one the normalizer
    /// could not rewrite - a stick source, a program whose trigger names another position,
    /// no long press, click auto-repeat, or a long-press action of its own - and none of
    /// those can run it from where it sits.
    /// </summary>
    private static void ValidateComplexShortcutSource(MappingEntry mapping)
    {
        if (mapping.Source is not ButtonBinding button)
        {
            throw new ArgumentException(
                $"Mapping {mapping.Id} drives a complex shortcut from {mapping.Source.GetType().Name}. " +
                "A complex shortcut is a long-press program: bind it to a button.",
                nameof(mapping));
        }

        var program = (ComplexShortcutTarget)mapping.Target;
        var gesture = button.Gesture;

        // The v1 shape: a long-press program sitting on the mapping's own target. The
        // normalizer rewrites the one form v1 accepted - a bare long press - so whatever
        // reaches here is a form it could not, and each refusal names why.
        if (program.Trigger == ActionTrigger.LongPress)
        {
            if (gesture is not { LongPressThreshold: > 0f })
            {
                throw new ArgumentException(
                    $"Mapping {mapping.Id} binds a complex shortcut to button {button.Button} " +
                    "without a long press. A complex shortcut starts on 长按: set " +
                    "ButtonGesture.LongPressThreshold.",
                    nameof(mapping));
            }

            if (gesture.HasClickRepeat || gesture.LongPressAction is not null)
            {
                throw new ArgumentException(
                    $"Mapping {mapping.Id} pairs a complex shortcut with click auto-repeat / " +
                    $"an extra gesture action on button {button.Button}. The long press already " +
                    "owns the program: leave the gesture as a bare long-press threshold.",
                    nameof(mapping));
            }

            // A bare long press is exactly the shape the normalizer rewrites, so this is
            // unreachable; it is refused rather than left to run nothing.
            throw new ArgumentException(
                $"Mapping {mapping.Id} binds a complex shortcut to button {button.Button} " +
                "without a long press. A complex shortcut starts on 长按: set " +
                "ButtonGesture.LongPressThreshold.",
                nameof(mapping));
        }

        if (program.Trigger is ActionTrigger.DoubleClick)
        {
            // The mapping's own target is the click position: only the click - a press edge
            // or the release edge that ends it - can start a program sitting there.
            throw new ArgumentException(
                $"Mapping {mapping.Id} binds a complex shortcut to button {button.Button} with its " +
                $"trigger set to {TriggerName(program.Trigger)}. A long-press program only ever runs " +
                "from the long press: leave the trigger at 长按 and set " +
                "ButtonGesture.LongPressThreshold, or set the trigger to 单击/松开 to run it " +
                "from this button's own click.",
                nameof(mapping));
        }

        if (gesture is { HasClickRepeat: true })
        {
            throw new ArgumentException(
                $"Mapping {mapping.Id} pairs a complex shortcut with click auto-repeat on " +
                $"button {button.Button}. The repeat makes the click re-fire the program's " +
                "own target on every interval: leave ClickRepeatInterval at 0.",
                nameof(mapping));
        }

        if (gesture is { LongPressAction: not null })
        {
            throw new ArgumentException(
                $"Mapping {mapping.Id} pairs a complex shortcut with another gesture action " +
                $"on button {button.Button}. The long press already owns the program: leave " +
                "the gesture's long-press action empty, or move the program onto it.",
                nameof(mapping));
        }

        if (program.Trigger == ActionTrigger.Release && program.Execution != ActionExecution.Once)
        {
            // Nothing is held once the button is up, so "while held" has no hold to key off
            // and "toggle" has no second press to toggle on.
            throw new ArgumentException(
                $"Mapping {mapping.Id} runs a complex shortcut on 松开 with execution " +
                $"{ExecutionName(program.Execution)}. A release-triggered program runs to its " +
                "end: set ActionExecution.Once.",
                nameof(mapping));
        }

        ValidateExecutionAndRepeat(mapping, program);
    }

    /// <summary>
    /// A queued repeat cannot pair with a held run: the queue fires when the current run
    /// ends, and a held run ends on the release - which is also what stops the program, so
    /// the queued run would start and stop in the same breath. The pairing is refused
    /// rather than silently dropped.
    /// </summary>
    private static void ValidateExecutionAndRepeat(MappingEntry mapping, ComplexShortcutTarget program)
    {
        if (program.Execution == ActionExecution.WhileHeld && program.Repeat == ActionRepeat.Queue)
        {
            throw new ArgumentException(
                $"Mapping {mapping.Id} queues a repeat for a program that runs " +
                "按住期间执行. A queued run starts when the current one ends - the release that " +
                "also stops a held program: set the execution to 执行一次 or 再按切换, or the " +
                "repeat to 重新开始 / 忽略.",
                nameof(mapping));
        }
    }

    /// <summary>
    /// A complex shortcut is the long press's or the double-click's own action, so the click
    /// target stays independent of it. What it cannot coexist with is anything that would
    /// stop its own gesture from firing it - no threshold, click auto-repeat (which makes
    /// the long press inert) or a long-press repeat (which would restart the program) -
    /// and it cannot be triggered from the position it does not sit in, because a program
    /// only ever runs from the gesture that carries it.
    /// </summary>
    private static void ValidateComplexShortcutAction(MappingEntry mapping, ButtonGesture gesture)
    {
        ValidateProgramAt(mapping, gesture.LongPressAction, GestureKind.LongPress, gesture);
        ValidateProgramAt(mapping, gesture.DoubleClickAction, GestureKind.DoubleClick, gesture);
    }

    private static void ValidateProgramAt(
        MappingEntry mapping,
        MappingTarget? action,
        GestureKind position,
        ButtonGesture gesture)
    {
        if (action is not ComplexShortcutTarget program)
        {
            return;
        }

        var button = ((ButtonBinding)mapping.Source).Button;

        if (TriggerKind(program.Trigger) != position)
        {
            var rides = position switch
            {
                GestureKind.LongPress => "the long press",
                GestureKind.DoubleClick => "the double-click",
                _ => "the click",
            };

            throw new ArgumentException(
                $"Mapping {mapping.Id} binds a complex shortcut to {rides} on button {button}, " +
                $"but its trigger is {TriggerName(program.Trigger)}. A complex shortcut is a " +
                "long-press program: only the position that carries it can start it. Set the " +
                $"trigger to {TriggerName(TriggerFor(position))} or move the program to the " +
                "position its trigger names.",
                nameof(mapping));
        }

        if (position == GestureKind.LongPress)
        {
            if (!gesture.HasLongPress)
            {
                throw new ArgumentException(
                    $"Mapping {mapping.Id} carries a complex shortcut without a long press. " +
                    "A complex shortcut starts on 长按: set ButtonGesture.LongPressThreshold.",
                    nameof(mapping));
            }

            if (gesture.HasClickRepeat)
            {
                throw new ArgumentException(
                    $"Mapping {mapping.Id} pairs a complex shortcut with click auto-repeat. " +
                    "The repeat makes the long press inert, so the program could never start.",
                    nameof(mapping));
            }

            if (gesture.LongPressRepeatInterval > 0f)
            {
                throw new ArgumentException(
                    $"Mapping {mapping.Id} pairs a complex shortcut with a long-press repeat. " +
                    "A repeat would restart the program: leave " +
                    "ButtonGesture.LongPressRepeatInterval at 0.",
                    nameof(mapping));
            }
        }
        else
        {
            if (!gesture.HasDoubleClick)
            {
                throw new ArgumentException(
                    $"Mapping {mapping.Id} binds a complex shortcut to the double-click " +
                    $"without a double-click window. A double-click program starts on 双击: " +
                    "set ButtonGesture.DoubleClickWindow.",
                    nameof(mapping));
            }

            if (gesture.HasClickRepeat)
            {
                throw new ArgumentException(
                    $"Mapping {mapping.Id} pairs a complex shortcut with click auto-repeat. " +
                    "The repeat makes the double-click inert, so the program could never start.",
                    nameof(mapping));
            }

            // A double-click holds nothing, so there is no hold to run a program for.
            if (program.Execution == ActionExecution.WhileHeld)
            {
                throw new ArgumentException(
                    $"Mapping {mapping.Id} runs a complex shortcut on 双击 with execution " +
                    "按住期间执行. A double-click holds nothing: set ActionExecution.Once " +
                    "or ActionExecution.Toggle.",
                    nameof(mapping));
            }
        }

        ValidateExecutionAndRepeat(mapping, program);
    }

    /// <summary>
    /// The gesture position each trigger reads. A program's trigger names the position it
    /// rides in: the long press, the double-click, or the mapping's own click - which the
    /// release variant also reads, one edge later.
    /// </summary>
    private static GestureKind TriggerKind(ActionTrigger trigger) => trigger switch
    {
        ActionTrigger.DoubleClick => GestureKind.DoubleClick,
        ActionTrigger.LongPress => GestureKind.LongPress,
        _ => GestureKind.Click,
    };

    /// <summary>The trigger a gesture position carries, for a refusal that names both.</summary>
    private static ActionTrigger TriggerFor(GestureKind kind) => kind switch
    {
        GestureKind.DoubleClick => ActionTrigger.DoubleClick,
        GestureKind.LongPress => ActionTrigger.LongPress,
        _ => ActionTrigger.Press,
    };

    private static string TriggerName(ActionTrigger trigger) => trigger switch
    {
        ActionTrigger.Press => "单击",
        ActionTrigger.DoubleClick => "双击",
        ActionTrigger.Release => "松开",
        _ => "长按",
    };

    private static string ExecutionName(ActionExecution execution) => execution switch
    {
        ActionExecution.Once => "执行一次",
        ActionExecution.Toggle => "再按切换",
        _ => "按住期间执行",
    };

    /// <summary>
    /// Edge detection for a raw-code button. The code is compared by value, so two different
    /// unmapped inputs never collide the way two buttons sharing a mask bit would.
    /// </summary>
    private void UpdateExtraButton(
        MappingEntry mapping,
        ButtonBinding button,
        IGamepadState current,
        TickSteps steps,
        List<InputCommand> commands)
    {
        var code = button.Code;
        var pressed = current.ExtraButtons.Contains(code);
        var wasPressed = _previousExtraButtons.Contains(code);

        UpdateButton(mapping, button, pressed, wasPressed, current, steps, commands);
    }

    /// <summary>
    /// One button mapping for one tick. Without a gesture the press edge activates the target
    /// immediately (zero latency); with one, the gesture state machine decides when - and
    /// whether - the single action fires, and which target a resolved gesture emits.
    /// </summary>
    private void UpdateButton(
        MappingEntry mapping,
        ButtonBinding button,
        bool isPressed,
        bool wasPressed,
        IGamepadState current,
        TickSteps steps,
        List<InputCommand> commands)
    {
        var gesture = GestureStateFor(mapping.Id);
        var step = gesture.Update(button.Gesture, isPressed, wasPressed, steps.Action);

        // A program whose trigger resolved on this tick consumes the edge: the mapping's
        // own target must not fire it as well, the way a long-press program never let the
        // click fire on the same press.
        var triggered = UpdatePrograms(mapping, gesture, step, isPressed, steps, commands);

        if (!triggered)
        {
            switch (step.Action)
            {
                case GestureAction.BeginHold:
                    BeginHold(mapping.Target, mapping.Id, current, commands);
                    break;
                case GestureAction.EndHold:
                    EndHold(mapping.Target, mapping.Id, current, commands);
                    break;
                case GestureAction.Tap:
                    Tap(mapping, button, step.Kind, current, steps, commands);
                    break;
            }

            // The drain mirrors the state machine's own ordering: a release edge ends the cycle
            // before any repeat is considered, so a release tick whose step carries RepeatElapsed
            // past the interval must not emit a repeat after the button is already up. The
            // button being down is also what keeps the catch-up drain working on a held tick.
            // Each repeat phase drains its own interval, so one long tick emits every repeat it
            // covers instead of one: the state machine has already emitted the first, and this
            // fills in the rest - up to the per-tick catch-up budget, whose remainder stays on
            // the repeat clock for the next tick rather than firing as one burst. The phases
            // never share a tick - click auto-repeat and the long-press are mutually exclusive -
            // so a ClickRepeating tick must never emit the long-press a hand-edited profile may
            // have left configured alongside the repeat.
            if (button.Gesture is { } configured && isPressed)
            {
                var clickRepeats = 0;
                while (gesture.Phase == GesturePhase.ClickRepeating
                       && clickRepeats < ButtonGestureState.MaxCatchUpUnits
                       && gesture.TryDequeueClickRepeat(configured))
                {
                    clickRepeats++;
                    Tap(mapping, button, GestureKind.Click, current, steps, commands);
                }

                var longRepeats = 0;
                while (gesture.Phase == GesturePhase.LongPressActive
                       && longRepeats < ButtonGestureState.MaxCatchUpUnits
                       && gesture.TryDequeueRepeat(configured))
                {
                    longRepeats++;
                    Tap(mapping, button, GestureKind.LongPress, current, steps, commands);
                }
            }
        }

        UpdateContinuous(mapping, button, gesture, current, steps, commands);
    }

    /// <summary>
    /// Every program one mapping carries, for one tick. A program whose trigger resolved is
    /// started (or re-triggered under its repeat policy) and advanced by the time left in
    /// this tick after the gesture resolved; the others are advanced, and one whose execution
    /// strategy ended is stopped with its held keys released. Returns true when a trigger
    /// consumed this tick's edge, so the mapping's own target does not fire it too.
    /// </summary>
    private bool UpdatePrograms(
        MappingEntry mapping,
        ButtonGestureState gesture,
        GestureStep step,
        bool isPressed,
        TickSteps steps,
        List<InputCommand> commands)
    {
        if (!_programSlots.TryGetValue(mapping.Id, out var slots))
        {
            return false;
        }

        var consumed = false;

        foreach (var slot in slots)
        {
            var program = _programs[slot];

            if (Triggered(slot, program, step))
            {
                consumed = true;
                ApplyTrigger(program, slot, commands);

                if (program.IsRunning)
                {
                    // The tick that starts a program advances it by the time past the
                    // threshold inside this tick - the rest of the tick was spent waiting
                    // for the gesture to resolve - and by the whole tick for a trigger that
                    // resolved on an edge.
                    var overflow = slot.Kind == GestureKind.LongPress ? gesture.RepeatElapsed : steps.Action;
                    program.Tick(overflow, commands);
                }

                continue;
            }

            AdvanceProgram(program, slot, isPressed, steps, commands);
        }

        return consumed;
    }

    /// <summary>
    /// Whether the gesture step that just resolved is the one this program's trigger reads.
    /// A release trigger reads the end of the click's activation - the tap that fired on the
    /// release, or the hold the user let go - while every other trigger reads its start.
    /// </summary>
    private static bool Triggered(ProgramSlot slot, ComplexShortcutRuntime program, GestureStep step)
    {
        if (step.Action == GestureAction.None || step.Kind != slot.Kind)
        {
            return false;
        }

        return program.Trigger == ActionTrigger.Release
            ? step.Action is GestureAction.EndHold or GestureAction.Tap
            : step.Action is GestureAction.Tap or GestureAction.BeginHold;
    }

    /// <summary>
    /// What a trigger does. A stopped program starts; a running one answers its execution
    /// strategy - a toggle stops - and otherwise its repeat policy: restart from the first
    /// step, ignore the press, or queue exactly one more run for when this one ends.
    /// </summary>
    private void ApplyTrigger(ComplexShortcutRuntime program, ProgramSlot slot, List<InputCommand> commands)
    {
        // A toggle is stopped by the same trigger that starts it, so its repeat policy has
        // nothing to answer.
        if (program.Execution == ActionExecution.Toggle)
        {
            if (program.IsRunning)
            {
                _queuedRestarts.Remove(slot);
                program.Stop(commands);
            }
            else
            {
                program.Start();
            }

            return;
        }

        if (!program.IsRunning)
        {
            _queuedRestarts.Remove(slot);
            program.Start();
            return;
        }

        switch (program.Repeat)
        {
            case ActionRepeat.Restart:
                program.Stop(commands);
                program.Start();
                break;
            case ActionRepeat.Queue:
                // At most one waiting run: a program queued twice is still one more run.
                _queuedRestarts.Add(slot);
                break;
        }
    }

    /// <summary>
    /// Advances one program, or ends it. A program that runs 按住期间 lives exactly as long
    /// as the press that started it, so the release tick stops it and hands its held keys
    /// back; one that runs 执行一次 or 再按切换 is only ever stopped by its own completion or
    /// by the next trigger. A queued restart fires the moment the run it waited for ends.
    /// </summary>
    private void AdvanceProgram(
        ComplexShortcutRuntime program,
        ProgramSlot slot,
        bool isPressed,
        TickSteps steps,
        List<InputCommand> commands)
    {
        if (!program.IsRunning)
        {
            if (_queuedRestarts.Remove(slot))
            {
                program.Start();
                program.Tick(steps.Action, commands);
            }

            return;
        }

        if (program.Execution == ActionExecution.WhileHeld && !isPressed)
        {
            program.Stop(commands);
            return;
        }

        program.Tick(steps.Action, commands);
    }

    /// <summary>Stops every program one mapping carries, releasing what each held.</summary>
    private bool StopProgramsOf(Guid mappingId, List<InputCommand> commands)
    {
        if (!_programSlots.TryGetValue(mappingId, out var slots))
        {
            return false;
        }

        var stopped = false;
        foreach (var slot in slots)
        {
            var program = _programs[slot];
            _queuedRestarts.Remove(slot);

            if (program.IsRunning)
            {
                program.Stop(commands);
                stopped = true;
            }
        }

        return stopped;
    }

    /// <summary>
    /// The target a resolved gesture emits. A gesture carrying an action of its own fires that
    /// action; one without re-fires the mapping's own target, which is what makes the action
    /// fields purely additive - a profile that never sets them behaves exactly as before.
    /// </summary>
    private static MappingTarget TargetFor(MappingEntry mapping, ButtonGesture? gesture, GestureKind kind)
        => kind switch
        {
            GestureKind.DoubleClick => gesture?.DoubleClickAction ?? mapping.Target,
            GestureKind.LongPress => gesture?.LongPressAction ?? mapping.Target,
            _ => mapping.Target,
        };

    /// <summary>
    /// Per-tick output for continuous targets driven by a button: while the activation is in
    /// effect the processor is fed full deflection in the button's direction, otherwise rest,
    /// so the pointer ramps up and settles exactly like a stick would.
    /// </summary>
    private void UpdateContinuous(
        MappingEntry mapping,
        ButtonBinding button,
        ButtonGestureState gesture,
        IGamepadState current,
        TickSteps steps,
        List<InputCommand> commands)
    {
        var (axisX, axisY) = gesture.IsActive && button.Direction is { } direction
            ? DirectionAxis(direction)
            : (0f, 0f);

        switch (mapping.Target)
        {
            case MouseMoveTarget mouse when _mouseProcessors.TryGetValue(mapping.Id, out var mouseProcessor):
                var precisionActive = PrecisionActive(current, mouse.Settings);
                var (dx, dy) = mouseProcessor.Update(axisX, axisY, precisionActive, steps.Integration);
                if (dx != 0 || dy != 0)
                {
                    commands.Add(new MouseMoveCommand(dx, dy));
                }

                break;

            case WheelScrollTarget when _scrollProcessors.TryGetValue(mapping.Id, out var scrollProcessor):
                var (horizontal, vertical) = scrollProcessor.Update(axisX, axisY, steps.Integration);
                if (horizontal != 0 || vertical != 0)
                {
                    commands.Add(new WheelCommand(horizontal, vertical));
                }

                break;
        }
    }

    private void UpdateStick(
        MappingEntry mapping,
        StickBinding stick,
        IGamepadState current,
        TickSteps steps,
        List<InputCommand> commands)
    {
        var (x, y) = StickAxes(current, stick.Stick);

        if (mapping.Target is MouseMoveTarget mouseTarget
            && _mouseProcessors.TryGetValue(mapping.Id, out var mouseProcessor))
        {
            var precisionActive = PrecisionActive(current, mouseTarget.Settings);
            var (dx, dy) = mouseProcessor.Update(x, y, precisionActive, steps.Integration);
            if (dx != 0 || dy != 0)
            {
                commands.Add(new MouseMoveCommand(dx, dy));
            }
        }
        else if (mapping.Target is WheelScrollTarget
                 && _scrollProcessors.TryGetValue(mapping.Id, out var scrollProcessor))
        {
            var (horizontal, vertical) = scrollProcessor.Update(x, y, steps.Integration);
            if (horizontal != 0 || vertical != 0)
            {
                commands.Add(new WheelCommand(horizontal, vertical));
            }
        }
        else if (stick.Direction is { } direction)
        {
            UpdateStickDirection(mapping, x, y, direction, current, steps, commands);
        }
    }

    /// <summary>
    /// A stick used as a digital source: a deflection past <see cref="StickDigitalThreshold"/>
    /// in the mapped direction presses the target, returning past it releases. The test is
    /// per-axis rather than radial, so a diagonal push can activate two directions at once,
    /// the way a real D-pad does.
    /// </summary>
    private void UpdateStickDirection(
        MappingEntry mapping,
        float x,
        float y,
        InputDirection direction,
        IGamepadState current,
        TickSteps steps,
        List<InputCommand> commands)
    {
        var active = IsDirectionActive(x, y, direction);
        var state = GestureStateFor(mapping.Id);

        if (active == state.IsActive)
        {
            return;
        }

        if (active)
        {
            BeginHold(mapping.Target, mapping.Id, current, commands);
        }
        else
        {
            EndHold(mapping.Target, mapping.Id, current, commands);
        }

        state.IsActive = active;
    }

    private static bool IsDirectionActive(float x, float y, InputDirection direction) => direction switch
    {
        InputDirection.Up => y >= StickDigitalThreshold,
        InputDirection.Down => y <= -StickDigitalThreshold,
        InputDirection.Left => x <= -StickDigitalThreshold,
        InputDirection.Right => x >= StickDigitalThreshold,
        _ => false,
    };

    private static (float X, float Y) StickAxes(IGamepadState state, GamepadStick stick) => stick switch
    {
        GamepadStick.Left => (state.LeftStickX, state.LeftStickY),
        GamepadStick.Right => (state.RightStickX, state.RightStickY),
        _ => (0f, 0f),
    };

    /// <summary>
    /// Full deflection for a direction, in the engine's normalized stick convention: +Y is up.
    /// That direction is a cross-platform Core contract (the Linux tracker negates evdev's
    /// positive-down ABS_Y to match XInput), so a button and a stick pushed the same way drive
    /// the target the same way, and the target's own InvertY and per-axis scales still apply.
    /// </summary>
    internal static (float X, float Y) DirectionAxis(InputDirection direction) => direction switch
    {
        InputDirection.Up => (0f, 1f),
        InputDirection.Down => (0f, -1f),
        InputDirection.Left => (-1f, 0f),
        InputDirection.Right => (1f, 0f),
        _ => (0f, 0f),
    };

    /// <summary>
    /// One instantaneous activation of the target a resolved gesture names: a key or mouse button
    /// goes down and straight back up, a mouse-move target nudges one tick's worth of full
    /// deflection in the button's direction, and a wheel target emits exactly one notch.
    /// </summary>
    private void Tap(
        MappingEntry mapping,
        ButtonBinding button,
        GestureKind kind,
        IGamepadState current,
        TickSteps steps,
        List<InputCommand> commands)
    {
        var target = TargetFor(mapping, button.Gesture, kind);

        switch (target)
        {
            case MouseMoveTarget mouse:
                TapMouseMove(mapping.Id, mouse, button, kind, current, steps, commands);
                break;
            case WheelScrollTarget wheel:
                TapWheel(wheel, button, commands);
                break;
            default:
                BeginHold(target, mapping.Id, current, commands);
                EndHold(target, mapping.Id, current, commands);
                break;
        }
    }

    private void TapMouseMove(
        Guid mappingId,
        MouseMoveTarget mouse,
        ButtonBinding button,
        GestureKind kind,
        IGamepadState current,
        TickSteps steps,
        List<InputCommand> commands)
    {
        var processor = MouseProcessorFor(mappingId, kind);
        if (processor is null)
        {
            return;
        }

        // Validation guarantees a direction for a continuous target; the fallback only keeps
        // the compiler happy.
        var (axisX, axisY) = DirectionAxis(button.Direction ?? InputDirection.Up);
        var precisionActive = PrecisionActive(current, mouse.Settings);
        var (dx, dy) = processor.Update(axisX, axisY, precisionActive, steps.Integration);

        // A tap is a single nudge: leave the processor at rest so no drift trails it.
        processor.Reset();

        if (dx != 0 || dy != 0)
        {
            commands.Add(new MouseMoveCommand(dx, dy));
        }
    }

    /// <summary>
    /// The smoothing processor a tap feeds: the mapping's own for the single, and the gesture
    /// action's own for 双击 / 长按, so a gesture action that moves the mouse ramps with its own
    /// sensitivity rather than borrowing the mapping's.
    /// </summary>
    private MouseStickProcessor? MouseProcessorFor(Guid mappingId, GestureKind kind)
        => kind == GestureKind.Click
            ? _mouseProcessors.GetValueOrDefault(mappingId)
            : _gestureMouseProcessors.GetValueOrDefault(new GestureProcessorKey(mappingId, kind));

    private static void TapWheel(WheelScrollTarget wheel, ButtonBinding button, List<InputCommand> commands)
    {
        var direction = button.Direction ?? InputDirection.Up;
        var vertical = direction switch
        {
            InputDirection.Up => wheel.Settings.InvertY ? -1 : 1,
            InputDirection.Down => wheel.Settings.InvertY ? 1 : -1,
            _ => 0,
        };
        var horizontal = (direction switch
        {
            InputDirection.Right => 1,
            InputDirection.Left => -1,
            _ => 0,
        }) * (wheel.Settings.EnableHorizontal ? 1 : 0);

        if (horizontal != 0 || vertical != 0)
        {
            commands.Add(new WheelCommand(horizontal, vertical));
        }
    }

    /// <summary>
    /// Begins a hold-style activation. A combo is a tap by nature, so it fires both edges at
    /// once and has nothing to end. A leader key in Hold mode switches to its alternate slot
    /// for the duration of the hold; one in Press mode toggles between its two slots on the
    /// press edge. A leader key whose action is <see cref="LeaderAction.Layer"/> switches the
    /// temporary layer instead: Hold enters it for the duration of the hold, Press toggles it
    /// on each press edge. Precision keys emit nothing here - their engagement is tracked
    /// separately.
    /// </summary>
    private void BeginHold(MappingTarget target, Guid mappingId, IGamepadState current, List<InputCommand> commands)
    {
        switch (target)
        {
            case KeyPressTarget key:
                commands.Add(new KeyDownCommand(key.Key, KeyModifiers.None, new OutputOwner(mappingId)));
                break;
            case KeyComboTarget combo:
                commands.Add(new KeyDownCommand(combo.Key, combo.Modifiers, new OutputOwner(mappingId)));
                commands.Add(new KeyUpCommand(combo.Key, combo.Modifiers, new OutputOwner(mappingId)));
                break;
            case MouseButtonTarget mouseButton:
                commands.Add(new MouseDownCommand(mouseButton.Button, new OutputOwner(mappingId)));
                break;
            case TextTarget text:
                // Text is a tap by nature: it goes out on the press edge and a release has
                // nothing to end, so BeginHold carries the whole activation.
                commands.Add(new TextCommand(text.Text));
                break;
            case SpecialKeyTarget { Kind: SpecialKeyKind.Leader, Action: LeaderAction.Layer } layer:
                if (layer.SwitchMode == LeaderSwitchMode.Press)
                {
                    ToggleLayer(mappingId, current, commands);
                }
                else
                {
                    ActivateLayer(mappingId, current, commands);
                }

                break;
            case SpecialKeyTarget { Kind: SpecialKeyKind.Leader, SwitchMode: LeaderSwitchMode.Hold } leader:
                commands.Add(new SwitchPresetCommand(leader.SlotB));
                break;
            case SpecialKeyTarget { Kind: SpecialKeyKind.Leader } leader:
                var latched = !_leaderLatches.GetValueOrDefault(mappingId);
                _leaderLatches[mappingId] = latched;
                commands.Add(new SwitchPresetCommand(latched ? leader.SlotB : leader.SlotA));
                break;
        }
    }

    /// <summary>
    /// Ends a hold-style activation. A leader key in Hold mode returns to its primary slot
    /// once the key is released; a leader key holding the temporary layer leaves it, releasing
    /// everything the layer's mappings put in effect - unless another leader still holds it.
    /// A Press-mode layer leader has nothing to end: the layer it toggled stays until the next
    /// press edge.
    /// </summary>
    private void EndHold(MappingTarget target, Guid mappingId, IGamepadState current, List<InputCommand> commands)
    {
        switch (target)
        {
            case KeyPressTarget key:
                commands.Add(new KeyUpCommand(key.Key, KeyModifiers.None, new OutputOwner(mappingId)));
                break;
            case MouseButtonTarget mouseButton:
                commands.Add(new MouseUpCommand(mouseButton.Button, new OutputOwner(mappingId)));
                break;
            case SpecialKeyTarget { Kind: SpecialKeyKind.Leader, Action: LeaderAction.Layer, SwitchMode: LeaderSwitchMode.Hold }:
                DeactivateLayer(mappingId, current, commands);
                break;
            case SpecialKeyTarget { Kind: SpecialKeyKind.Leader, SwitchMode: LeaderSwitchMode.Hold } leader:
                commands.Add(new SwitchPresetCommand(leader.SlotA));
                break;
        }
    }

    private ButtonGestureState GestureStateFor(Guid mappingId)
    {
        if (!_gestures.TryGetValue(mappingId, out var state))
        {
            state = new ButtonGestureState();
            _gestures.Add(mappingId, state);
        }

        return state;
    }
}
