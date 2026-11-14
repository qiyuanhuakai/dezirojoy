using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// A row in a gesture-action picker: the label to show and the target it stands for. A gesture
/// action is an ordinary <see cref="MappingTarget"/> - the same type a mapping's target uses -
/// so a double-click or long-press can fire a key, a combo or a mouse button exactly like the
/// mapping itself, with no second representation to keep in step. A null target marks a row
/// that is not an action at all: 已关闭 turns the gesture off, 重复单击的功能 leaves it
/// re-firing the mapping's own target.
/// </summary>
public sealed record GestureActionOption(string Label, MappingTarget? Target);

/// <summary>A special-key sub-kind as the picker shows it: 精准键 or Leader 键.</summary>
public sealed record SpecialKeyKindOption(SpecialKeyKind Value, string Label);

/// <summary>A leader switch mode as the picker shows it: 按下切换 or 按住切换.</summary>
public sealed record LeaderSwitchModeOption(LeaderSwitchMode Value, string Label);

/// <summary>What a leader key switches, as the picker shows it: 切换预设 or 切换临时层.</summary>
public sealed record LeaderActionOption(LeaderAction Value, string Label);

/// <summary>
/// One layer as the layer picker shows it: 基础层, which every mapping already runs, or the
/// one 临时层 above it, which only runs while a Leader 键 whose action switches the
/// temporary layer has it in effect.
/// </summary>
public sealed record MappingLayerOption(int Value, string Label);

/// <summary>
/// One place where a global special key - a 精准键 or a Leader 键 (which switches 预设 or
/// the 临时层) - collides with another setting on the same physical button. The two
/// device-level keys are shared across every preset of the device, so a collision on their
/// trigger button is a configuration defect rather than a per-preset quirk: the mapping
/// row, the 全局特殊键 area and the editor all read the same verdict and say the same
/// thing about it.
/// </summary>
/// <param name="Kind">Which global key is at risk.</param>
/// <param name="Button">The physical button the two settings share.</param>
/// <param name="Code">The raw code that tells one extra button from another.</param>
/// <param name="SpecialEntryId">The mapping that carries the global key.</param>
/// <param name="OtherEntryId">
/// The other mapping involved, or <see cref="SpecialEntryId"/> itself when the collision is
/// inside the special key's own mapping - a gesture on it, for instance.
/// </param>
/// <param name="Message">
/// The Chinese hint: which setting collides with which global key, and what the collision
/// costs at runtime.
/// </param>
public sealed record SpecialKeyConflict(
    SpecialKeyKind Kind,
    GamepadButton Button,
    int Code,
    Guid SpecialEntryId,
    Guid OtherEntryId,
    string Message)
{
    /// <summary>What the shared button is called, the way every picker names it.</summary>
    public string ButtonLabel => MappingDescriptions.Button(Button, Code);

    /// <summary>What the global key is called, the way its own picker names it.</summary>
    public string GlobalKeyLabel => MappingDescriptions.SpecialKey(new SpecialKeyTarget(Kind));
}

/// <summary>
/// Where a special key sits on its button: the mapping's own click, or one of the gesture's
/// two actions. The trigger decides how the key fires - every press, the second press of a
/// double, or a hold past the long-press threshold - which is what the conflict wording and
/// the 全局特殊键 summary both have to name.
/// </summary>
public enum SpecialKeyTrigger
{
    Click,

    DoubleClick,

    LongPress,
}

/// <summary>
/// The conflict rules between the global special keys - a 精准键 or a Leader 键, whichever
/// of the two the leader switches - and everything else configured on their trigger
/// buttons. Every rule is stated the way the engine actually behaves - the gesture machine
/// in <c>ButtonGestureState</c> decides which press resolves to what, and a precision key
/// engages from the raw button rather than from the mapping loop - so a hint never warns
/// about a pairing that works and never stays silent about one that does not. The analysis
/// only reports: fixing a collision stays the user's edit to make.
/// </summary>
/// <remarks>
/// The engine lets the three gestures coexist: no long-press action consumes the press, so a
/// release before the long-press threshold always fires the single. What a gesture CAN do to
/// a leader key on the click is delay it (the double-click window) or repeat it (click
/// auto-repeat, or a long press that carries a repeat interval and falls back to the click),
/// and what another entry on the same button can do is fire beside it. A precision key
/// engages from the raw button, so gestures leave it alone - but only from the click
/// position, the one the engine registers it from. A layer leader switches the temporary
/// layer without reloading the engine, so only a preset leader's switch cancels an
/// undecided gesture.
/// </remarks>
public static class SpecialKeyConflicts
{
    /// <summary>
    /// Every collision in one mapping list. Mappings are grouped by physical button - the
    /// button plus the raw code that tells one extra button from another - because a gesture
    /// or a direction is configuration ON an element, not a second element. A disabled
    /// mapping never runs, so it never collides; a stick has no gestures, so only buttons
    /// are examined.
    /// </summary>
    public static IReadOnlyList<SpecialKeyConflict> Analyze(IReadOnlyList<MappingEntry> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        var conflicts = new List<SpecialKeyConflict>();
        var buttons = new Dictionary<(GamepadButton Button, int Code), List<MappingEntry>>();

        foreach (var mapping in mappings)
        {
            if (!mapping.Enabled || mapping.Source is not ButtonBinding binding)
            {
                continue;
            }

            var key = (binding.Button, binding.Code);
            if (!buttons.TryGetValue(key, out var group))
            {
                group = [];
                buttons.Add(key, group);
            }

            group.Add(mapping);
        }

        foreach (var (key, entries) in buttons)
        {
            var (button, code) = key;
            var label = MappingDescriptions.Button(button, code);

            foreach (var entry in entries)
            {
                var binding = (ButtonBinding)entry.Source;
                var gesture = binding.Gesture;

                // A leader key on the click switches from the mapping loop, so a gesture on
                // the same mapping delays the switch or repeats it. A precision key engages
                // from the raw button instead, so gestures leave it alone.
                if (entry.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader } && gesture is { } leaderGesture)
                {
                    if (leaderGesture.HasDoubleClick)
                    {
                        conflicts.Add(new SpecialKeyConflict(
                            SpecialKeyKind.Leader,
                            button,
                            code,
                            entry.Id,
                            entry.Id,
                            $"「Leader 键」设在{label}的单击上，但该按钮还配了双击手势：单击要等双击窗口（{Seconds(leaderGesture.DoubleClickWindow)}）结束才切换预设；窗口内按第二次时触发的是双击手势，双击若另配了动作，该次按下不会切换预设。"));
                    }

                    if (leaderGesture.HasClickRepeat)
                    {
                        conflicts.Add(new SpecialKeyConflict(
                            SpecialKeyKind.Leader,
                            button,
                            code,
                            entry.Id,
                            entry.Id,
                            $"「Leader 键」设在{label}的单击上，但该按钮开启了长按重复触发：按住期间会每隔 {Seconds(leaderGesture.ClickRepeatInterval)}反复切换一次预设。"));
                    }

                    // A long press with a repeat interval and no action of its own falls back
                    // to the mapping's own target on every repeat - the leader key - so the
                    // same repeated switching arrives through the other flag.
                    if (leaderGesture.HasEffectiveLongPress
                        && leaderGesture.LongPressRepeatInterval > 0f
                        && leaderGesture.LongPressAction is null)
                    {
                        conflicts.Add(new SpecialKeyConflict(
                            SpecialKeyKind.Leader,
                            button,
                            code,
                            entry.Id,
                            entry.Id,
                            $"「Leader 键」设在{label}的单击上，但该按钮的长按配了重复间隔（每 {Seconds(leaderGesture.LongPressRepeatInterval)}一次）：按住越过长按阈值后，长按会反复回落到该 Leader 键，预设随之反复切换。"));
                    }
                }

                // Another entry on the same button fires beside the global key. A leader key
                // triggers from any position, so the sibling's outputs - its own click and
                // either gesture action - are weighed against the trigger, and the wording
                // names which gesture resolves the global key. The engine swallows nothing,
                // so a sibling's quick press always fires beside the key. A precision key
                // engages from the click alone, so a gesture-position one is rule 6's
                // business alone; an entry's own click beside its own gesture key is a
                // deliberate pairing, not a collision with itself.
                if (KeyOf(entry) is { } own
                    && TriggerOf(entry) is { } trigger
                    && (own.Kind == SpecialKeyKind.Leader || trigger == SpecialKeyTrigger.Click))
                {
                    // The editor's own naming, so a layer leader reads as the 临时层 Leader 键
                    // its picker offers rather than as another action's label.
                    var keyName = MappingDescriptions.SpecialKey(own);

                    foreach (var other in entries)
                    {
                        if (ReferenceEquals(other, entry))
                        {
                            continue;
                        }

                        foreach (var (position, target) in OutputsOf(other))
                        {
                            // The compound tap-then-hold the two cross pairings name needs the
                            // long-press side to start a fresh cycle on the second press edge.
                            // When that mapping carries its own double-click window at least as
                            // wide as the double-click side's, its ClickPending resolves its own
                            // double-click on that edge instead - the editor writes equal
                            // windows, so enabling both gesture slots on one mapping is exactly
                            // this case - and no interaction fires the pair. A hand-edited
                            // narrower window still leaves a press in between that fires both,
                            // so the hint stays there.
                            if (CompoundBlocked(trigger, position, gesture, OtherGesture(other)))
                            {
                                continue;
                            }

                            var description = MappingDescriptions.Target(target);
                            conflicts.Add(new SpecialKeyConflict(
                                own.Kind,
                                button,
                                code,
                                entry.Id,
                                other.Id,
                                $"{label}上还有另一条映射（{PositionName(position)}：{description}）：{BesideTheKey(trigger, position, keyName, description)}"));
                        }

                        // A sibling carrying a global key of its own is another entry's
                        // business, so the delay and cancel rules below stay with siblings
                        // that carry none - the same fact is then reported exactly once.
                        if (KeyOf(other) is not null)
                        {
                            continue;
                        }

                        var siblingGesture = OtherGesture(other);

                        // 双击 delays the sibling's single: the click cannot fire until the
                        // window closes, and a second press inside it resolves the
                        // double-click instead - that single never fires at all.
                        if (siblingGesture is { HasDoubleClick: true } doubleClick
                            && other.Target is not NoneTarget)
                        {
                            conflicts.Add(new SpecialKeyConflict(
                                own.Kind,
                                button,
                                code,
                                entry.Id,
                                other.Id,
                                $"{label}上还有一条映射配了双击手势：它的单击要等双击窗口（{Seconds(doubleClick.DoubleClickWindow)}）"
                                + "结束才触发，窗口内按下第二次时触发的是双击、这一次单击不再触发。"));
                        }

                        // 长按 moves the sibling's single to the release: the click waits
                        // for the button to come up, and a hold past the threshold fires the
                        // long press instead - again with no single at all.
                        if (siblingGesture is { HasEffectiveLongPress: true } longPress
                            && other.Target is not NoneTarget)
                        {
                            conflicts.Add(new SpecialKeyConflict(
                                own.Kind,
                                button,
                                code,
                                entry.Id,
                                other.Id,
                                $"{label}上还有一条映射配了长按：它的单击要等松开才触发，"
                                + $"按住越过长按阈值（{Seconds(longPress.LongPressThreshold)}）时触发的是长按、这一次单击不再触发。"));
                        }

                        // A switch reloads the engine, and a reload cancels a gesture that
                        // has not resolved: an undecided click or double-click on the same
                        // button never fires, whichever way the leader switches - on the
                        // press, or on the release that hands the slot back. Only a sibling
                        // with a single of its own has something to lose; one that is
                        // 无功能 has no click waiting behind the window. A layer leader
                        // switches the temporary layer without reloading anything, so only
                        // a preset leader's switch cancels the pending gesture.
                        if (own is { Kind: SpecialKeyKind.Leader, Action: LeaderAction.Preset }
                            && siblingGesture is { HasDoubleClick: true }
                            && other.Target is not NoneTarget)
                        {
                            conflicts.Add(new SpecialKeyConflict(
                                own.Kind,
                                button,
                                code,
                                entry.Id,
                                other.Id,
                                $"{label}上有一条未决的双击判定：触发「{keyName}」会切换预设，"
                                + "切换引发的引擎重载会取消这次未决的判定，随之而来的单击与双击都不会触发。"));
                        }
                    }
                }

                // The engine registers a precision key from the mapping's own target alone:
                // one sitting on a gesture action never joins the button set the slowing
                // reads, so it engages nothing at all. The deprecated Layer 键 is read the
                // same way - the engine only rewrites one sitting on the mapping's own
                // target, so one a hand-edited profile left on a gesture action enters no
                // layer at all. The merged leader engages from any position, so a layer
                // leader on a gesture action is an ordinary leader here.
                if (gesture is { } gestureKeys)
                {
                    if (gestureKeys.DoubleClickAction is SpecialKeyTarget { Kind: SpecialKeyKind.Precision })
                    {
                        conflicts.Add(new SpecialKeyConflict(
                            SpecialKeyKind.Precision,
                            button,
                            code,
                            entry.Id,
                            entry.Id,
                            $"「精准键」设在{label}的双击上不会生效：精准减速只识别设在单击上的精准键，请把它改到单击位置。"));
                    }

                    if (gestureKeys.LongPressAction is SpecialKeyTarget { Kind: SpecialKeyKind.Precision })
                    {
                        conflicts.Add(new SpecialKeyConflict(
                            SpecialKeyKind.Precision,
                            button,
                            code,
                            entry.Id,
                            entry.Id,
                            $"「精准键」设在{label}的长按上不会生效：精准减速只识别设在单击上的精准键，请把它改到单击位置。"));
                    }

                    if (gestureKeys.DoubleClickAction is SpecialKeyTarget { Kind: SpecialKeyKind.Layer })
                    {
                        conflicts.Add(new SpecialKeyConflict(
                            SpecialKeyKind.Layer,
                            button,
                            code,
                            entry.Id,
                            entry.Id,
                            $"「Layer 键」设在{label}的双击上不会生效：旧版层键已合并为 Leader 键的「切换临时层」，请把它改到单击位置。"));
                    }

                    if (gestureKeys.LongPressAction is SpecialKeyTarget { Kind: SpecialKeyKind.Layer })
                    {
                        conflicts.Add(new SpecialKeyConflict(
                            SpecialKeyKind.Layer,
                            button,
                            code,
                            entry.Id,
                            entry.Id,
                            $"「Layer 键」设在{label}的长按上不会生效：旧版层键已合并为 Leader 键的「切换临时层」，请把它改到单击位置。"));
                    }
                }

            }
        }

        return conflicts;
    }

    /// <summary>The collisions that involve one entry, for the mapping row that shows it.</summary>
    public static IReadOnlyList<SpecialKeyConflict> Involving(
        IReadOnlyList<SpecialKeyConflict> conflicts,
        Guid entryId)
        => conflicts.Where(conflict => conflict.SpecialEntryId == entryId || conflict.OtherEntryId == entryId).ToArray();

    /// <summary>
    /// The first mapping that carries a special key of one kind, in any of the three
    /// positions, or null when the profile configures none. The 全局特殊键 area lists one
    /// row per kind through this; a profile carrying a second key of the same kind keeps it
    /// reachable through its own mapping row.
    /// </summary>
    public static MappingEntry? EntryFor(IReadOnlyList<MappingEntry> mappings, SpecialKeyKind kind)
        => mappings.FirstOrDefault(entry => KeyOf(entry) is { } key && key.Kind == kind);

    /// <summary>
    /// The special key one mapping carries, wherever it sits: its own target first - the
    /// position the engine registers precision from - then the gesture's two actions. Null
    /// when the mapping carries no global key at all.
    /// </summary>
    public static SpecialKeyTarget? KeyOf(MappingEntry entry)
    {
        if (entry.Target is SpecialKeyTarget target)
        {
            return target;
        }

        if (entry.Source is ButtonBinding { Gesture: { } gesture })
        {
            if (gesture.DoubleClickAction is SpecialKeyTarget doubleClick)
            {
                return doubleClick;
            }

            if (gesture.LongPressAction is SpecialKeyTarget longPress)
            {
                return longPress;
            }
        }

        return null;
    }

    /// <summary>
    /// Where the key <see cref="KeyOf"/> found sits, or null when the mapping carries none.
    /// The priority matches <see cref="KeyOf"/> - the click first, then the double-click,
    /// then the long press - so the position named is the one the key came from.
    /// </summary>
    public static SpecialKeyTrigger? TriggerOf(MappingEntry entry)
    {
        if (entry.Target is SpecialKeyTarget)
        {
            return SpecialKeyTrigger.Click;
        }

        if (entry.Source is ButtonBinding { Gesture: { } gesture })
        {
            if (gesture.DoubleClickAction is SpecialKeyTarget)
            {
                return SpecialKeyTrigger.DoubleClick;
            }

            if (gesture.LongPressAction is SpecialKeyTarget)
            {
                return SpecialKeyTrigger.LongPress;
            }
        }

        return null;
    }

    /// <summary>
    /// Every output a mapping emits, each with the gesture that resolves it: its own click
    /// unless it is 无功能, then the double-click and long-press actions that carry a real
    /// target. A gesture action left null re-fires the click - which is 无功能 here - and a
    /// NoneTarget action emits nothing, so neither is an output of its own.
    /// </summary>
    private static IEnumerable<(SpecialKeyTrigger Position, MappingTarget Target)> OutputsOf(MappingEntry entry)
    {
        if (entry.Target is not NoneTarget)
        {
            yield return (SpecialKeyTrigger.Click, entry.Target);
        }

        if (entry.Source is ButtonBinding { Gesture: { } gesture })
        {
            if (gesture.DoubleClickAction is { } doubleClick && doubleClick is not NoneTarget)
            {
                yield return (SpecialKeyTrigger.DoubleClick, doubleClick);
            }

            if (gesture.LongPressAction is { } longPress && longPress is not NoneTarget)
            {
                yield return (SpecialKeyTrigger.LongPress, longPress);
            }
        }
    }

    /// <summary>The gesture a position names, the way the editor's switcher names it.</summary>
    private static string PositionName(SpecialKeyTrigger position) => position switch
    {
        SpecialKeyTrigger.DoubleClick => "双击",
        SpecialKeyTrigger.LongPress => "长按",
        _ => "单击",
    };

    /// <summary>The gesture one mapping carries, or null when it carries none.</summary>
    private static ButtonGesture? OtherGesture(MappingEntry entry)
        => entry.Source is ButtonBinding { Gesture: { } gesture } ? gesture : null;

    /// <summary>
    /// True when the compound tap-then-hold the two cross pairings name cannot happen: the
    /// long-press-side mapping carries its own double-click window at least as wide as the
    /// double-click side's, so the second press resolves ITS double-click instead of starting
    /// a long-press cycle. The editor writes equal windows - enabling both gesture slots on
    /// one mapping is exactly this shape - and a hand-edited narrower window still leaves a
    /// press in between that fires both, which is why the comparison keeps the hint there.
    /// Only the two cross pairings are gated: the seven pairings that meet without the
    /// compound are unaffected by whichever side carries its own double-click.
    /// </summary>
    private static bool CompoundBlocked(
        SpecialKeyTrigger global,
        SpecialKeyTrigger sibling,
        ButtonGesture? globalGesture,
        ButtonGesture? siblingGesture)
        => (global, sibling) switch
        {
            (SpecialKeyTrigger.LongPress, SpecialKeyTrigger.DoubleClick) =>
                Blocks(longPressSide: globalGesture, doubleClickSide: siblingGesture),
            (SpecialKeyTrigger.DoubleClick, SpecialKeyTrigger.LongPress) =>
                Blocks(longPressSide: siblingGesture, doubleClickSide: globalGesture),
            _ => false,
        };

    private static bool Blocks(ButtonGesture? longPressSide, ButtonGesture? doubleClickSide)
        => longPressSide is { HasDoubleClick: true } longPress
        && doubleClickSide is { } doubleClick
        && longPress.DoubleClickWindow >= doubleClick.DoubleClickWindow;

    /// <summary>
    /// What it costs when the global key and the sibling's output fire together, worded by
    /// how each resolves: a click key answers every press, a double-click key the second
    /// press of a double, a long-press key the hold past the threshold. The sibling's click
    /// rides every press edge of its own, while its gesture actions wait for their gesture.
    /// The two cross pairings meet through the compound tap-then-hold: gesture state is
    /// per-mapping, so a double-click resolves on the second press edge while a long-press
    /// sibling starts a fresh cycle on that same edge and fires at its threshold mid-hold.
    /// </summary>
    private static string BesideTheKey(
        SpecialKeyTrigger global,
        SpecialKeyTrigger sibling,
        string key,
        string description) => (global, sibling) switch
    {
        (SpecialKeyTrigger.Click, SpecialKeyTrigger.Click) =>
            $"每次按下触发「{key}」的同时也会触发{description}。",
        (SpecialKeyTrigger.Click, SpecialKeyTrigger.DoubleClick) =>
            $"双击该按钮时，每次按下都会触发「{key}」，第二次按下还会触发{description}。",
        (SpecialKeyTrigger.Click, SpecialKeyTrigger.LongPress) =>
            $"长按该按钮时，「{key}」在该次按下时已经切换，按住期间还会触发{description}。",
        (SpecialKeyTrigger.DoubleClick, SpecialKeyTrigger.Click) =>
            $"双击触发「{key}」时，两次按下都会触发{description}，第二次按下与该切换同发。",
        (SpecialKeyTrigger.DoubleClick, SpecialKeyTrigger.DoubleClick) =>
            $"双击触发「{key}」的第二次按下，同时也会触发{description}。",
        (SpecialKeyTrigger.DoubleClick, SpecialKeyTrigger.LongPress) =>
            $"双击触发「{key}」后，若第二次按下按住越过长按阈值，还会触发{description}（切换引发的重载通常会取消这次长按）。",
        (SpecialKeyTrigger.LongPress, SpecialKeyTrigger.Click) =>
            $"长按触发「{key}」期间，{description}一直被按住，与该切换同发。",
        (SpecialKeyTrigger.LongPress, SpecialKeyTrigger.DoubleClick) =>
            $"双击触发{description}后，若第二次按下按住越过长按阈值，还会触发「{key}」切换预设。",
        (SpecialKeyTrigger.LongPress, SpecialKeyTrigger.LongPress) =>
            $"长按触发「{key}」时，同一按住也会触发{description}。",
        _ => $"触发「{key}」的同时也会触发{description}。",
    };

    /// <summary>A pinned timing as the user reads it: 0.25 秒, never 0.25000012 秒.</summary>
    private static string Seconds(float seconds)
        => $"{seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} 秒";
}

/// <summary>
/// Editing surface for one mapping: pick the source (button or stick) and the target
/// (single key, combo, mouse button, stick-to-mouse, stick-to-wheel, special key, or the
/// 复杂快捷键 program the long press runs).
/// </summary>
/// <remarks>
/// <para>
/// The source type is chosen first and only the matching picker is offered, so the editor
/// cannot present a stick picker next to a button picker. Every target kind is offered for
/// both source kinds: the engine honors all ten combinations, and the two that need extra
/// information - a stick driving a digital target, a button driving a continuous one - ask
/// for a <see cref="InputDirection"/> through <see cref="ShowDirectionPicker"/>.
/// </para>
/// <para>
/// Live capture is driven from the view (see <c>EditorCapture</c>): gamepad samples arrive
/// through <see cref="CaptureGamepadSample"/> and keyboard keys through
/// <see cref="CaptureKeyboardKey"/>/<see cref="EndKeyboardCapture"/>. This type stays
/// Avalonia-free, so all of it is unit-testable without a window.
/// </para>
/// </remarks>
public partial class MappingEditorViewModel : ObservableObject
{
    /// <summary>
    /// Every target kind, for both source kinds. The engine honors all source x target
    /// combinations, so narrowing the list per source would only hide mappings that work.
    /// </summary>
    private static readonly MappingTargetKind[] AllTargetKinds =
    [
        MappingTargetKind.KeyPress,
        MappingTargetKind.KeyCombo,
        MappingTargetKind.MouseButton,
        MappingTargetKind.MouseMove,
        MappingTargetKind.WheelScroll,
        MappingTargetKind.SpecialKey,
        MappingTargetKind.Text,
        MappingTargetKind.ComplexShortcut,
    ];

    /// <summary>
    /// The kinds a stick source offers. A stick has no gestures, so nothing can start a
    /// program: 复杂快捷键 stays off the list rather than offering a mapping the engine
    /// refuses.
    /// </summary>
    private static readonly MappingTargetKind[] StickTargetKinds =
    [
        MappingTargetKind.KeyPress,
        MappingTargetKind.KeyCombo,
        MappingTargetKind.MouseButton,
        MappingTargetKind.MouseMove,
        MappingTargetKind.WheelScroll,
        MappingTargetKind.SpecialKey,
        MappingTargetKind.Text,
    ];

    private readonly SortedSet<int> _extraButtonCodes = [];
    private readonly SortedSet<int> _previousCaptureExtra = [];
    private ButtonPickerItem? _selectedButtonOption;
    private KeyPickerItem? _selectedKeyOption;
    private KeyPickerItem? _activeSelectedKeyOption;
    private GestureActionOption? _selectedDoubleClickOption;
    private GestureActionOption? _selectedLongPressOption;
    private InputDirection? _loadedDirection;
    private GamepadButtonMask _previousCaptureButtons;

    /// <summary>The row that turns the gesture off: it never resolves, so the press fires at once.</summary>
    public static readonly GestureActionOption GestureOffOption = new("已关闭", null);

    /// <summary>The row that makes the gesture resolve and emit nothing (无功能).</summary>
    public static readonly GestureActionOption GestureNoneOption = new("无功能", new NoneTarget());

    /// <summary>
    /// The row that leaves the gesture without a function of its own: it re-fires the mapping's
    /// own target, the behaviour every profile written before gesture actions existed has.
    /// </summary>
    public static readonly GestureActionOption GestureRepeatOption = new("重复单击的功能", null);

    private static readonly GestureActionOption[] GestureActionRows = BuildGestureActionRows();

    /// <summary>Every target a gesture action can be set to, for the picker.</summary>
    public IReadOnlyList<GestureActionOption> GestureActionOptions { get; } = GestureActionRows;

    private static GestureActionOption[] BuildGestureActionRows()
    {
        var keys = Enum.GetValues<GamepadKey>().Select(
            key =>
            {
                var target = new KeyPressTarget(key);
                return new GestureActionOption(MappingDescriptions.Target(target), target);
            });

        var buttons = Enum.GetValues<MouseButton>().Select(
            button =>
            {
                var target = new MouseButtonTarget(button);
                return new GestureActionOption(MappingDescriptions.Target(target), target);
            });

        // The three non-action rows lead: off, 无功能 and re-fire are the states a gesture
        // itself can be in, and every real target follows.
        return [GestureOffOption, GestureNoneOption, GestureRepeatOption, .. keys, .. buttons];
    }

    public MappingEditorViewModel()
    {
        RebuildButtonOptions();
        RebuildKeyOptions();

        // Every draft is its own object, so the switcher moves between the three gestures
        // without any of them losing an edit; the editor keeps the pickers and the readouts
        // in step with whatever the drafts hold.
        ClickSlot.PropertyChanged += (_, e) => OnClickSlotChanged(e.PropertyName);
        DoubleClickSlot.PropertyChanged += (_, _) => OnDoubleClickSlotChanged();
        LongPressSlot.PropertyChanged += (_, _) => OnLongPressSlotChanged();

        // A fresh editor has both gestures off, so both pickers open on the 已关闭 row rather
        // than on an empty selection.
        SyncDoubleClickOption();
        SyncLongPressOption();
    }

    [ObservableProperty]
    private bool _sourceIsStick;

    [ObservableProperty]
    private GamepadButton _selectedButton = GamepadButton.A;

    [ObservableProperty]
    private int _selectedExtraCode;

    [ObservableProperty]
    private GamepadStick _selectedStick = GamepadStick.Right;

    /// <summary>
    /// The draft for the click: the mapping's own target. The double-click and the long-press
    /// drafts are edited through the same fields, so the switcher moves between three equal
    /// peers without any of them losing an edit.
    /// </summary>
    public GestureSlotEditor ClickSlot { get; } = new();

    /// <summary>The draft for the double-click gesture.</summary>
    public GestureSlotEditor DoubleClickSlot { get; } = new();

    /// <summary>The draft for the long-press gesture.</summary>
    public GestureSlotEditor LongPressSlot { get; } = new();

    /// <summary>The draft the switcher has selected, for a view that binds one set of fields.</summary>
    public GestureSlotEditor ActiveSlot => SelectedGestureKind switch
    {
        GestureKind.DoubleClick => DoubleClickSlot,
        GestureKind.LongPress => LongPressSlot,
        _ => ClickSlot,
    };

    /// <summary>The active slot's target kind.</summary>
    public MappingTargetKind ActiveTargetKind
    {
        get => ActiveSlot.TargetKind;
        set
        {
            ActiveSlot.TargetKind = value;

            if (value == MappingTargetKind.ComplexShortcut)
            {
                // A program only ever runs from the position that carries it, so choosing the
                // kind on a tab arms that tab's own trigger - the editor can then never write
                // a program whose trigger names a different position.
                ActiveSlot.Trigger = TriggerForGesture(SelectedGestureKind);
            }
        }
    }

    /// <summary>The active slot's target key.</summary>
    public GamepadKey ActiveSelectedKey
    {
        get => ActiveSlot.SelectedKey;
        set => ActiveSlot.SelectedKey = value;
    }

    /// <summary>The active slot's Ctrl modifier.</summary>
    public bool ActiveModifierCtrl
    {
        get => ActiveSlot.ModifierCtrl;
        set => ActiveSlot.ModifierCtrl = value;
    }

    /// <summary>The active slot's Shift modifier.</summary>
    public bool ActiveModifierShift
    {
        get => ActiveSlot.ModifierShift;
        set => ActiveSlot.ModifierShift = value;
    }

    /// <summary>The active slot's Alt modifier.</summary>
    public bool ActiveModifierAlt
    {
        get => ActiveSlot.ModifierAlt;
        set => ActiveSlot.ModifierAlt = value;
    }

    /// <summary>The active slot's Win modifier.</summary>
    public bool ActiveModifierWin
    {
        get => ActiveSlot.ModifierWin;
        set => ActiveSlot.ModifierWin = value;
    }

    /// <summary>The active slot's mouse button.</summary>
    public MouseButton ActiveSelectedMouseButton
    {
        get => ActiveSlot.SelectedMouseButton;
        set => ActiveSlot.SelectedMouseButton = value;
    }

    /// <summary>The active slot's stick-to-mouse settings.</summary>
    public MouseStickSettings ActiveMouseSettings
    {
        get => ActiveSlot.MouseSettings;
        set => ActiveSlot.MouseSettings = value;
    }

    /// <summary>
    /// The precision key of the active slot's mouse mapping. The key belongs to the mapping,
    /// so it is chosen here rather than on the sensitivity panel, which only reports it.
    /// </summary>
    public GamepadButton ActivePrecisionButton
    {
        get => ActiveSlot.MouseSettings.PrecisionButton;
        set
        {
            ActiveSlot.MouseSettings = ActiveSlot.MouseSettings with { PrecisionButton = value };
            NotifyActiveSlotChanged();
        }
    }

    /// <summary>Every button the precision key can be bound to.</summary>
    public IReadOnlyList<GamepadButton> PrecisionButtonOptions { get; } =
        Enum.GetValues<GamepadButton>().Where(button => button != GamepadButton.Extra).ToArray();

    /// <summary>
    /// The active slot's precision key as every readout names it. The key belongs to the
    /// mapping, so the semantic label - never the raw enum name - is what the user reads.
    /// </summary>
    public string ActivePrecisionButtonLabel => MappingDescriptions.Button(ActivePrecisionButton);

    /// <summary>The special-key sub-kinds, for the picker.</summary>
    public IReadOnlyList<SpecialKeyKindOption> SpecialKindOptions { get; } =
    [
        new(SpecialKeyKind.Precision, "精准键"),
        new(SpecialKeyKind.Leader, "Leader 键"),
        new(SpecialKeyKind.Keyboard, "屏幕键盘"),
    ];

    /// <summary>
    /// The precision modes a 精准键 offers, labelled exactly as the existing precision UI
    /// labels them, so the two surfaces never name the same mode differently.
    /// </summary>
    public IReadOnlyList<PrecisionModeOption> SpecialPrecisionModeOptions { get; } =
    [
        new(PrecisionMode.Hold, MappingDescriptions.PrecisionModeLabel(PrecisionMode.Hold)),
        new(PrecisionMode.Toggle, MappingDescriptions.PrecisionModeLabel(PrecisionMode.Toggle)),
    ];

    /// <summary>The leader switch modes, for the picker.</summary>
    public IReadOnlyList<LeaderSwitchModeOption> SpecialSwitchModeOptions { get; } =
    [
        new(LeaderSwitchMode.Press, MappingDescriptions.LeaderSwitchModeLabel(LeaderSwitchMode.Press)),
        new(LeaderSwitchMode.Hold, MappingDescriptions.LeaderSwitchModeLabel(LeaderSwitchMode.Hold)),
    ];

    /// <summary>
    /// What a Leader 键 switches, for the picker: 切换预设 flips between the two preset
    /// slots it names, 切换临时层 switches the one temporary layer the profile carries.
    /// </summary>
    public IReadOnlyList<LeaderActionOption> SpecialLeaderActionOptions { get; } =
    [
        new(LeaderAction.Preset, "切换预设"),
        new(LeaderAction.Layer, "切换临时层"),
    ];

    /// <summary>
    /// The preset slots a leader key can flip between. Starts at the initial four and is
    /// replaced by the window view model with the device's live slots, so a slot the user
    /// added is offered here too.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<int> _specialSlotOptions = [1, 2, 3, 4];

    /// <summary>
    /// The layers a mapping can sit on: 基础层, which always runs, and the one 临时层 above
    /// it, which runs only while a Leader 键 whose action switches the temporary layer has
    /// it in effect. There is exactly one temporary layer, so the picker carries two rows.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<MappingLayerOption> _layerOptions =
    [
        new(0, LayerLabel(0)),
        new(1, LayerLabel(1)),
    ];

    /// <summary>What a layer is called, the way every layer picker names it.</summary>
    public static string LayerLabel(int layer) => layer <= 0 ? "基础层" : "临时层";

    /// <summary>
    /// Which layer the mapping being edited belongs to: 0 for the base layer, which always
    /// runs, or the one temporary layer that only runs while a Leader 键 whose action
    /// switches the temporary layer has it in effect. The tag travels with the entry on
    /// commit and is restored on reload, so a mapping placed on the temporary layer stays
    /// there through an edit instead of collapsing back onto the base layer. It is also the
    /// one source of truth the 所属层 picker binds to - through the picker row's
    /// <see cref="MappingLayerOption.Value"/> - so what the picker displays and what the
    /// entry commits are the same number by construction.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LayerDescription))]
    [NotifyPropertyChangedFor(nameof(LayerSummary))]
    private int _selectedLayer;

    partial void OnSelectedLayerChanged(int value) => EnforceLayerLeaderPlacement();

    /// <summary>What layer the mapping being edited is on, for the header readout.</summary>
    public string LayerDescription => LayerLabel(SelectedLayer);

    /// <summary>
    /// The layer as the header names it, beside the source and the target: a mapping placed
    /// on the temporary layer must never read as an ordinary one.
    /// </summary>
    public string LayerSummary => $"所属层：{LayerDescription}";

    /// <summary>
    /// True while the mapping's own target is a Leader 键 whose action switches the
    /// temporary layer. Such a leader has to sit on the base layer: a temporary layer is
    /// entered from the base layer only, and the engine refuses a profile that names one
    /// from inside another - a refusal that would fail the whole profile rather than the
    /// one mapping. The editor snaps the layer back to 基础层 instead, and
    /// <see cref="LayerHint"/> says why.
    /// </summary>
    public bool LayerLeaderRequiresBaseLayer => ClickSlot.TargetKind == MappingTargetKind.SpecialKey
        && ClickSlot.SpecialKind == SpecialKeyKind.Leader
        && ClickSlot.SpecialLeaderAction == LeaderAction.Layer;

    /// <summary>
    /// What the layer picker means for the mapping being edited: the inheritance rule for an
    /// ordinary mapping, or why a layer-switching Leader 键 has to sit on the base layer.
    /// </summary>
    public string LayerHint => LayerLeaderRequiresBaseLayer
        ? "「切换临时层」的 Leader 键本身必须放在基础层：临时层只能从基础层进入，引擎会拒绝从其他层切换层的配置，因此该映射的所属层固定为基础层。"
        : "基础层的映射始终生效；临时层只在「切换临时层」的 Leader 键生效期间运行（按住切换：按住期间；按下切换：每次按下翻转），并覆盖同按钮的基础层映射，未覆盖的继承基础层。";

    private void EnforceLayerLeaderPlacement()
    {
        if (LayerLeaderRequiresBaseLayer && SelectedLayer != 0)
        {
            SelectedLayer = 0;
        }
    }

    /// <summary>The active slot's special-key sub-kind.</summary>
    public SpecialKeyKind ActiveSpecialKind
    {
        get => ActiveSlot.SpecialKind;
        set => ActiveSlot.SpecialKind = value;
    }

    /// <summary>The active slot's special-key precision mode.</summary>
    public PrecisionMode ActiveSpecialPrecisionMode
    {
        get => ActiveSlot.SpecialPrecisionMode;
        set => ActiveSlot.SpecialPrecisionMode = value;
    }

    /// <summary>The active slot's leader switch mode.</summary>
    public LeaderSwitchMode ActiveSpecialSwitchMode
    {
        get => ActiveSlot.SpecialSwitchMode;
        set => ActiveSlot.SpecialSwitchMode = value;
    }

    /// <summary>The first of the two slots a leader key flips between.</summary>
    public int ActiveSpecialSlotA
    {
        get => ActiveSlot.SpecialSlotA;
        set => ActiveSlot.SpecialSlotA = value;
    }

    /// <summary>The second of the two slots a leader key flips between.</summary>
    public int ActiveSpecialSlotB
    {
        get => ActiveSlot.SpecialSlotB;
        set => ActiveSlot.SpecialSlotB = value;
    }

    /// <summary>The active slot's leader action: 切换预设 or 切换临时层.</summary>
    public LeaderAction ActiveSpecialLeaderAction
    {
        get => ActiveSlot.SpecialLeaderAction;
        set => ActiveSlot.SpecialLeaderAction = value;
    }

    /// <summary>The selected sub-kind picker row, kept in step with <see cref="ActiveSpecialKind"/>.</summary>
    public SpecialKeyKindOption? ActiveSpecialKindOption
    {
        get => SpecialKindOptions.First(option => option.Value == ActiveSpecialKind);
        set
        {
            if (value is not null)
            {
                ActiveSpecialKind = value.Value;
            }
        }
    }

    /// <summary>The selected precision-mode picker row, kept in step with <see cref="ActiveSpecialPrecisionMode"/>.</summary>
    public PrecisionModeOption? ActiveSpecialPrecisionModeOption
    {
        get => SpecialPrecisionModeOptions.First(option => option.Value == ActiveSpecialPrecisionMode);
        set
        {
            if (value is not null)
            {
                ActiveSpecialPrecisionMode = value.Value;
            }
        }
    }

    /// <summary>The selected switch-mode picker row, kept in step with <see cref="ActiveSpecialSwitchMode"/>.</summary>
    public LeaderSwitchModeOption? ActiveSpecialSwitchModeOption
    {
        get => SpecialSwitchModeOptions.First(option => option.Value == ActiveSpecialSwitchMode);
        set
        {
            if (value is not null)
            {
                ActiveSpecialSwitchMode = value.Value;
            }
        }
    }

    /// <summary>The selected leader-action picker row, kept in step with <see cref="ActiveSpecialLeaderAction"/>.</summary>
    public LeaderActionOption? ActiveSpecialLeaderActionOption
    {
        get => SpecialLeaderActionOptions.First(option => option.Value == ActiveSpecialLeaderAction);
        set
        {
            if (value is not null)
            {
                ActiveSpecialLeaderAction = value.Value;
            }
        }
    }

    /// <summary>True when the special-key editor offers the leader controls: the action every leader names.</summary>
    public bool ShowSpecialLeaderControls => ActiveTargetKind == MappingTargetKind.SpecialKey
        && ActiveSpecialKind == SpecialKeyKind.Leader;

    /// <summary>
    /// True when the special-key editor offers the preset-slot controls: a leader that
    /// switches presets names the two slots it flips between, a layer leader names none.
    /// </summary>
    public bool ShowSpecialPresetControls => ShowSpecialLeaderControls
        && ActiveSpecialLeaderAction == LeaderAction.Preset;

    /// <summary>
    /// True when the special-key editor offers the switch-mode control: a leader key
    /// switches under either mode, whichever of the two it switches.
    /// </summary>
    public bool ShowSpecialSwitchControls => ActiveTargetKind == MappingTargetKind.SpecialKey
        && ActiveSpecialKind == SpecialKeyKind.Leader;

    /// <summary>
    /// True while the switch-mode picker is live: both leader actions choose freely between
    /// 按下切换 and 按住切换 - the engine runs a layer leader under either mode, toggling
    /// the layer on each press edge or holding it for the duration of the hold - so the
    /// picker never disables a choice that works. See <see cref="SpecialLayerHint"/>.
    /// </summary>
    public bool SpecialSwitchModeEditable => ActiveTargetKind != MappingTargetKind.SpecialKey
        || ActiveSpecialKind == SpecialKeyKind.Leader;

    /// <summary>True when the special-key editor offers the temporary-layer explanation.</summary>
    public bool ShowSpecialLayerControls => ShowSpecialLeaderControls
        && ActiveSpecialLeaderAction == LeaderAction.Layer;

    /// <summary>
    /// What a 切换临时层 leader does, in Chinese: it switches the one temporary layer the
    /// profile carries - 按住 enters it for as long as the key is held, 按下 toggles it on
    /// each press edge - the layer overrides the base mappings that listen on the same
    /// input and inherits the rest.
    /// </summary>
    public string SpecialLayerHint => ShowSpecialLayerControls
        ? "「切换临时层」切换配置文件里唯一的临时层：按住切换在按住期间进入，松开即回到基础层；按下切换每次按下翻转。临时层期间层内映射覆盖同按钮的基础层映射，未覆盖的继承基础层。"
        : string.Empty;

    /// <summary>True when the special-key editor offers the screen-keyboard explanation.</summary>
    public bool ShowSpecialKeyboardControls => ActiveTargetKind == MappingTargetKind.SpecialKey
        && ActiveSpecialKind == SpecialKeyKind.Keyboard;

    /// <summary>
    /// What a 屏幕键盘 key does, in Chinese: it toggles the floating on-screen keyboard, which
    /// types into whatever application has focus. The wording has to cover the one thing a
    /// user cannot guess - how it is put away again - because the window takes the pad over
    /// while it is open, so the engine never sees a second press of the same key.
    /// </summary>
    public string SpecialKeyboardHint => ShowSpecialKeyboardControls
        ? "「屏幕键盘」按下时开关一个独立浮窗键盘：它会直接在当前聚焦的应用里输入（搜索框、重命名、短网址等），因此不会抢走该应用的键盘焦点。键盘打开期间手柄由键盘接管——十字键移动光标、A 输入、LB / RB 换页、B 关闭，再按一次设了这个键的手柄键同样关闭；完整输入法（IME）暂不支持。"
        : string.Empty;

    /// <summary>True when the special-key editor offers the precision controls.</summary>
    public bool ShowSpecialPrecisionControls => ActiveTargetKind == MappingTargetKind.SpecialKey
        && ActiveSpecialKind == SpecialKeyKind.Precision;

    /// <summary>
    /// True when a leader key that switches presets names the same slot twice. Flipping
    /// between a slot and itself is not a switch, so the editor refuses the commit and says
    /// why rather than writing a mapping that can never do anything. A layer leader names
    /// no slots, so equal ones are not a conflict there.
    /// </summary>
    public bool SpecialKeySlotConflict => ShowSpecialPresetControls
        && ActiveSpecialSlotA == ActiveSpecialSlotB;

    /// <summary>
    /// The profile's other mappings, handed in by the window view model when an edit opens.
    /// They exist for the conflict hints alone: a special key this draft configures collides
    /// with what else sits on the same physical button, and the analysis needs to see it.
    /// The entry being edited is excluded, so the draft is never measured against itself.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<MappingEntry> _otherMappings = [];

    /// <summary>
    /// Every collision the current draft has with the profile's other mappings on the same
    /// button - its own gestures delaying or repeating a leader switch, another mapping
    /// firing beside the global key, a precision key the engine would never engage.
    /// Recomputed on every draft edit and every source move, so the hint below is live while
    /// the user configures.
    /// </summary>
    public IReadOnlyList<SpecialKeyConflict> DraftSpecialKeyConflicts { get; private set; } = [];

    /// <summary>True while the draft collides with something, so the hint has something to say.</summary>
    public bool HasSpecialKeyConflict => DraftSpecialKeyConflicts.Count > 0;

    /// <summary>
    /// The collisions as one Chinese paragraph, each sentence naming the setting, the global
    /// key it collides with and what the collision costs. Hints only: the draft is the user's
    /// to keep or change, so nothing here rewrites it.
    /// </summary>
    public string? SpecialKeyConflictHint => DraftSpecialKeyConflicts.Count > 0
        ? string.Join(" ", DraftSpecialKeyConflicts.Select(conflict => conflict.Message))
        : null;

    /// <summary>
    /// Re-reads the draft's collisions. Runs from <see cref="NotifyActiveSlotChanged"/> - the
    /// one chokepoint every slot edit, flag change and switcher move already passes through -
    /// and from the source handlers, so moving the edit to another button re-measures against
    /// that button's mappings instead of the previous one's.
    /// </summary>
    private void RefreshSpecialKeyConflicts()
    {
        var draft = BuildEntry();
        var measured = new List<MappingEntry>(OtherMappings.Count + 1) { draft };
        measured.AddRange(OtherMappings);

        var conflicts = SpecialKeyConflicts.Analyze(measured);
        if (conflicts.SequenceEqual(DraftSpecialKeyConflicts))
        {
            return;
        }

        DraftSpecialKeyConflicts = conflicts;
        OnPropertyChanged(nameof(DraftSpecialKeyConflicts));
        OnPropertyChanged(nameof(HasSpecialKeyConflict));
        OnPropertyChanged(nameof(SpecialKeyConflictHint));
    }

    /// <summary>The active slot's stick-to-wheel settings.</summary>
    public ScrollStickSettings ActiveScrollSettings
    {
        get => ActiveSlot.ScrollSettings;
        set => ActiveSlot.ScrollSettings = value;
    }

    /// <summary>
    /// The active slot's short text, as the 输入文本 box reads and writes it. Typed on the
    /// press edge and bounded by <see cref="GestureSlotEditor.TextMaxLength"/>.
    /// </summary>
    public string ActiveText
    {
        get => ActiveSlot.Text;
        set => ActiveSlot.Text = value.Length > TextMaxLength ? value[..TextMaxLength] : value;
    }

    /// <summary>How long the active slot's text is, for the counter beside the box.</summary>
    public int ActiveTextLength => ActiveSlot.Text.Length;

    /// <summary>The longest string a text target may carry, the Core's own bound.</summary>
    public static int TextMaxLength => GestureSlotEditor.TextMaxLength;

    /// <summary>
    /// True when the active slot's target is one this build does not know. The draft shows it
    /// as an unsupported mapping and commits it back exactly as the profile carries it; the
    /// only thing that replaces it is the user picking something else.
    /// </summary>
    public bool ActiveTargetUnsupported => ActiveSlot.Unsupported is not null;

    /// <summary>How the unsupported target reads, naming its discriminator.</summary>
    public string? ActiveTargetUnsupportedDescription => ActiveSlot.UnsupportedDescription;

    /// <summary>What the target area shows while the draft carries a target this build cannot read.</summary>
    public string UnsupportedTargetHint => ActiveTargetUnsupported
        ? "该映射使用的功能此版本不支持（可能是由更新版本写入的）。它会原样保留，不会被覆盖；重新选择功能即可替换它。"
        : string.Empty;

    // ---- 输入文本键盘: a gamepad-navigable on-screen keyboard --------------------

    /// <summary>
    /// The on-screen keyboard model that types the active slot's text. It exists so a short text
    /// target - a search query, a file name, a URL - can be entered with the gamepad alone;
    /// full IME composition is a follow-up, and the physical-keyboard capture stays the way
    /// to type anything its pages do not carry.
    /// <para>
    /// The model is shared with the floating screen keyboard
    /// (<see cref="MainWindowViewModel.ScreenKeyboard"/>), which is where the caps are now
    /// drawn: that window types into whatever application has focus, which a card inside this
    /// editor cannot reach, so the editor card carries the text box alone and this stays the
    /// draft's own keyboard - the same pages, the same cursor, the same edits.
    /// </para>
    /// </summary>
    public TextKeyboard TextKeyboard { get; } = new();

    /// <summary>True when the keyboard applies: the active slot's target is a text target.</summary>
    public bool ShowTextKeyboard => ActiveTargetKind == MappingTargetKind.Text && !ActiveTargetUnsupported;

    /// <summary>
    /// True while the gamepad may drive the on-screen keyboard: the mapping editor is open
    /// and the window's own gamepad navigation is in scope - the same lease every other
    /// editor press answers to. The view keeps it in step, because the window's scope and its
    /// navigation toggle are the window's to know, and <see cref="CaptureGamepadSample"/>
    /// reads it beside <see cref="ShowTextKeyboard"/> so a press spent on the keyboard is
    /// never also spent navigating.
    /// </summary>
    public bool TextKeyboardGamepadInScope { get; set; }

    /// <summary>
    /// Applies one pressed key of the on-screen keyboard to the active slot's text. An insert
    /// key types its text at the end - the draft has no cursor of its own - and an action key
    /// runs its own edit.
    /// </summary>
    public void ApplyTextKeyboardKey(TextKeyboardKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        switch (key.Action)
        {
            case TextKeyboardAction.Backspace:
                var text = ActiveSlot.Text;
                ActiveText = text.Length > 0 ? text[..^1] : text;
                break;
            case TextKeyboardAction.Clear:
                ActiveText = string.Empty;
                break;
            case TextKeyboardAction.Space:
            case TextKeyboardAction.Insert:
                ActiveText = AppendText(key.Text);
                break;
            case TextKeyboardAction.Confirm:
                break;
        }
    }

    /// <summary>Types more text, stopping at the Core's bound rather than refusing the key.</summary>
    private string AppendText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return ActiveSlot.Text;
        }

        var current = ActiveSlot.Text;
        var room = GestureSlotEditor.TextMaxLength - current.Length;
        return room <= 0 ? current : current + text[..(int)Math.Min(room, text.Length)];
    }

    /// <summary>Moves the on-screen keyboard's cursor; false when the cursor is already at that edge.</summary>
    public bool MoveTextKeyboardCursor(InputDirection direction) => TextKeyboard.Move(direction);

    /// <summary>Shows the on-screen keyboard's next page, wrapping around at the end.</summary>
    public void ShowNextTextKeyboardPage() => TextKeyboard.ShowPage(TextKeyboard.NextPage);

    /// <summary>Shows the on-screen keyboard's previous page, wrapping around at the start.</summary>
    public void ShowPreviousTextKeyboardPage() => TextKeyboard.ShowPage(TextKeyboard.PreviousPage);

    /// <summary>Presses the key the on-screen keyboard's cursor is on.</summary>
    public void ActivateTextKeyboardKey()
    {
        if (TextKeyboard.Activate() is { } key)
        {
            ApplyTextKeyboardKey(key);
        }
    }

    /// <summary>
    /// 无功能 for the active slot: it resolves and emits nothing.
    /// </summary>
    public bool ActiveIsNone
    {
        get => ActiveSlot.IsNone;
        set => ActiveSlot.IsNone = value;
    }

    /// <summary>
    /// 重复单击的功能 for the active slot: the legacy state where the gesture has no function
    /// of its own and re-fires the click. It stays reachable without dominating the editor.
    /// </summary>
    public bool ActiveRepeatsClick
    {
        get => ActiveSlot.RepeatsClick;
        set => ActiveSlot.RepeatsClick = value;
    }

    /// <summary>
    /// True when the active gesture resolves. The click always resolves - it is the mapping's
    /// own target - so only the double-click and the long-press carry an off state, and that
    /// state is 已关闭: the gesture never resolves, which is a different thing from resolving
    /// to 无功能.
    /// </summary>
    public bool ActiveGestureEnabled
    {
        get => SelectedGestureKind switch
        {
            GestureKind.DoubleClick => DoubleClickEnabled,
            GestureKind.LongPress => LongPressEnabled,
            _ => true,
        };
        set
        {
            switch (SelectedGestureKind)
            {
                case GestureKind.DoubleClick:
                    DoubleClickEnabled = value;
                    break;
                case GestureKind.LongPress:
                    LongPressEnabled = value;
                    break;
            }
        }
    }

    /// <summary>True when the active slot can be turned off, i.e. when it is a gesture at all.</summary>
    public bool ActiveGestureCanBeDisabled => SelectedGestureKind != GestureKind.Click;

    /// <summary>The active gesture's own name, for readouts.</summary>
    public string ActiveGestureName => SelectedGestureKind switch
    {
        GestureKind.DoubleClick => "双击",
        GestureKind.LongPress => "长按",
        _ => "单击",
    };

    /// <summary>
    /// True when the shared target editor's controls are live: the gesture resolves and the
    /// slot is not 无功能. A disabled gesture has nothing to configure until it is enabled.
    /// </summary>
    public bool ActiveTargetEditable => ActiveGestureEnabled && !ActiveSlot.IsNone;

    /// <summary>
    /// What the active slot does, for the header readout. The click is the mapping's own
    /// target, so it reads as the target alone; a gesture slot is prefixed with its name, and
    /// a disabled one reads 已关闭 rather than naming a target that never fires. The long
    /// press reads 复杂快捷键 while it runs a program, with the script's own summary in the
    /// card below.
    /// </summary>
    public string ActiveTargetDescription
    {
        get
        {
            if (ActiveTargetUnsupported)
            {
                return ActiveGestureCanBeDisabled
                    ? $"{ActiveGestureName}：{ActiveTargetUnsupportedDescription ?? "不支持的映射"}"
                    : ActiveTargetUnsupportedDescription ?? "不支持的映射";
            }

            if (ActiveGestureCanBeDisabled && !ActiveGestureEnabled)
            {
                return $"{ActiveGestureName}：已关闭";
            }

            var target = ActiveSlot.BuildTarget();
            var what = ActiveSlot.Function switch
            {
                GestureSlotFunction.None => "无功能",
                GestureSlotFunction.RepeatClick => "重复单击的功能",
                _ when ActiveRunsComplexShortcut => "复杂快捷键",
                _ => target is SpecialKeyTarget special
                    ? SpecialKeyDescription(special)
                    : MappingDescriptions.Target(target),
            };

            return ActiveGestureCanBeDisabled ? $"{ActiveGestureName}：{what}" : what;
        }
    }

    /// <summary>
    /// What a special key does, named the way its own picker names it: a leader reads as the
    /// Leader 键 its sub-kind picker offers, with 临时层 in front when it switches the
    /// temporary layer. The Core record has no description of its own, and its raw
    /// <c>ToString</c> would read as debug output in the editor's header.
    /// </summary>
    private static string SpecialKeyDescription(SpecialKeyTarget target)
        => MappingDescriptions.SpecialKey(target);

    /// <summary>
    /// The selected row of the categorized key list for the active slot. A category header is
    /// a label, not a choice, so assigning one snaps the selection back to the key that is
    /// actually selected - the rule <see cref="SelectedKeyOption"/> applies to the click alone.
    /// </summary>
    public KeyPickerItem? ActiveSelectedKeyOption
    {
        get => _activeSelectedKeyOption;
        set
        {
            if (EqualityComparer<KeyPickerItem?>.Default.Equals(value, _activeSelectedKeyOption))
            {
                return;
            }

            if (value is { IsHeader: true })
            {
                SetActiveSelectedKeyOption(CurrentActiveKeyOption());
                return;
            }

            SetActiveSelectedKeyOption(value);

            if (value is not null)
            {
                ActiveSlot.SelectedKey = value.Key;

                // Picking a key implies a key target; a mouse-button target would silently
                // drop it, so the kind follows the choice.
                if (ActiveSlot.TargetKind == MappingTargetKind.MouseButton)
                {
                    ActiveSlot.TargetKind = MappingTargetKind.KeyPress;
                }
            }
        }
    }

    /// <summary>
    /// Whether the categorized key list is expanded. It starts collapsed on every editor - the
    /// list is the long way to a key, so it stays out of the way until it is asked for - and
    /// the choice lives here so a new editor opens collapsed rather than wherever the last one
    /// left it.
    /// </summary>
    [ObservableProperty]
    private bool _keyPickerExpanded;

    [ObservableProperty]
    private IReadOnlyList<MappingTargetKind> _availableTargetKinds = AllTargetKinds;

    /// <summary>The click's target kind, edited on <see cref="ClickSlot"/>.</summary>
    public MappingTargetKind TargetKind
    {
        get => ClickSlot.TargetKind;
        set
        {
            ClickSlot.TargetKind = value;

            if (value == MappingTargetKind.ComplexShortcut)
            {
                ClickSlot.Trigger = TriggerForGesture(GestureKind.Click);
            }
        }
    }

    /// <summary>The trigger a gesture position carries, the one a program on it runs from.</summary>
    private static ActionTrigger TriggerForGesture(GestureKind gesture) => gesture switch
    {
        GestureKind.DoubleClick => ActionTrigger.DoubleClick,
        GestureKind.LongPress => ActionTrigger.LongPress,
        _ => ActionTrigger.Press,
    };

    // ---- 复杂快捷键: a DSL program any of the three positions can run ------------

    /// <summary>
    /// True when the click's own target is a program. A program at the click position runs
    /// from the button's own press - or its release - so the click target and the two
    /// gesture actions each carry their own program independently.
    /// </summary>
    public bool ClickRunsComplexShortcut => !SourceIsStick
        && ClickSlot.TargetKind == MappingTargetKind.ComplexShortcut
        && ClickSlot.Function == GestureSlotFunction.Target;

    /// <summary>True when the double-click's action is a program.</summary>
    public bool DoubleClickRunsComplexShortcut => !SourceIsStick
        && DoubleClickSlot.TargetKind == MappingTargetKind.ComplexShortcut
        && DoubleClickSlot.Function == GestureSlotFunction.Target;

    /// <summary>
    /// True when the long-press draft runs a complex shortcut program. The program is the
    /// long press's own action, so the click target and the double-click action stay
    /// independent of it: a quick press fires the click and a second press fires the
    /// double-click exactly as they would without a program at all. What the program cannot
    /// coexist with is whatever would stop the long press from firing it - click auto-repeat
    /// and a long-press repeat are refused, and the long press can no longer be switched
    /// off.
    /// </summary>
    public bool LongPressRunsComplexShortcut => !SourceIsStick
        && LongPressSlot.TargetKind == MappingTargetKind.ComplexShortcut
        && LongPressSlot.Function == GestureSlotFunction.Target;

    /// <summary>True when any of the three positions runs a program.</summary>
    public bool RunsComplexShortcut => ClickRunsComplexShortcut
        || DoubleClickRunsComplexShortcut
        || LongPressRunsComplexShortcut;

    /// <summary>
    /// True when the active slot runs a program: the slot the switcher has selected, which
    /// is the one the strategy pickers and the preview describe.
    /// </summary>
    public bool ActiveRunsComplexShortcut => !SourceIsStick
        && ActiveSlot.TargetKind == MappingTargetKind.ComplexShortcut
        && ActiveSlot.Function == GestureSlotFunction.Target;

    /// <summary>
    /// True when the 启用手势 checkbox is live. A slot running a complex shortcut cannot be
    /// switched off: the program is that position's own action, so a disabled gesture would
    /// leave it unreachable - the editor keeps the gesture on and says why instead of
    /// writing a mapping the engine refuses.
    /// </summary>
    public bool ActiveGestureToggleEnabled => !ActiveRunsComplexShortcut;

    /// <summary>The active slot's script, as the script card's text box reads and writes it.</summary>
    public string ActiveComplexShortcutScript
    {
        get => ActiveSlot.Script ?? string.Empty;
        set => ActiveSlot.Script = value;
    }

    /// <summary>
    /// The long-press script's parse error, in Chinese and naming the line and column, or
    /// null while it parses. The script is hand-edited, so the author needs the offending
    /// token found without counting characters.
    /// </summary>
    public string? ComplexShortcutScriptError
    {
        get => _complexShortcutScriptError;
        private set
        {
            _complexShortcutScriptError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasComplexShortcutScriptError));
            OnPropertyChanged(nameof(ComplexShortcutScriptInvalid));
        }
    }

    /// <summary>What the long-press script does in plain language, or null while it does not parse.</summary>
    public string? ComplexShortcutScriptSummary
    {
        get => _complexShortcutScriptSummary;
        private set
        {
            _complexShortcutScriptSummary = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasComplexShortcutScriptSummary));
        }
    }

    /// <summary>True while the script card shows the parse error rather than the summary.</summary>
    public bool HasComplexShortcutScriptError => ComplexShortcutScriptError is not null;

    /// <summary>True while the script card shows the plain-language summary.</summary>
    public bool HasComplexShortcutScriptSummary => ComplexShortcutScriptSummary is not null;

    /// <summary>
    /// True while the long press runs a script that does not parse. The commit refuses it:
    /// writing a program the engine cannot build would fail the whole profile at reload,
    /// so the editor stops here and the script card already says what is wrong.
    /// </summary>
    public bool ComplexShortcutScriptInvalid => ActiveRunsComplexShortcut
        && ComplexShortcutScriptError is not null;

    private string? _complexShortcutScriptError;

    private string? _complexShortcutScriptSummary;

    // ---- 触发方式 / 执行方式 / 重复方式: the strategy the GUI shares with the DSL ----

    /// <summary>A trigger as the picker shows it: the position's own word.</summary>
    public sealed record ActionTriggerOption(ActionTrigger Value, string Label);

    /// <summary>An execution strategy as the picker shows it.</summary>
    public sealed record ActionExecutionOption(ActionExecution Value, string Label);

    /// <summary>A repeat policy as the picker shows it.</summary>
    public sealed record ActionRepeatOption(ActionRepeat Value, string Label);

    /// <summary>The triggers a program on the click position can run from.</summary>
    private static readonly ActionTriggerOption[] ClickTriggerOptions =
    [
        new(ActionTrigger.Press, "单击"),
        new(ActionTrigger.Release, "松开"),
    ];

    /// <summary>The trigger a program on the double-click position runs from.</summary>
    private static readonly ActionTriggerOption[] DoubleClickTriggerOptions =
    [
        new(ActionTrigger.DoubleClick, "双击"),
    ];

    /// <summary>The trigger a program on the long-press position runs from.</summary>
    private static readonly ActionTriggerOption[] LongPressTriggerOptions =
    [
        new(ActionTrigger.LongPress, "长按"),
    ];

    /// <summary>Every execution strategy, in the picker's order.</summary>
    private static readonly ActionExecutionOption[] ExecutionOptions =
    [
        new(ActionExecution.WhileHeld, "按住期间执行"),
        new(ActionExecution.Once, "执行一次"),
        new(ActionExecution.Toggle, "再按切换"),
    ];

    /// <summary>Every repeat policy, in the picker's order.</summary>
    private static readonly ActionRepeatOption[] RepeatOptions =
    [
        new(ActionRepeat.Restart, "重新开始"),
        new(ActionRepeat.Ignore, "忽略"),
        new(ActionRepeat.Queue, "排队"),
    ];

    /// <summary>
    /// The triggers the active slot's program can run from. A program only ever starts from
    /// the position that carries it, so the list is that position's own - offering the
    /// others would write a mapping the engine refuses.
    /// </summary>
    public IReadOnlyList<ActionTriggerOption> ActiveActionTriggerOptions => SelectedGestureKind switch
    {
        GestureKind.DoubleClick => DoubleClickTriggerOptions,
        GestureKind.LongPress => LongPressTriggerOptions,
        _ => ClickTriggerOptions,
    };

    /// <summary>Every execution strategy, for the picker.</summary>
    public IReadOnlyList<ActionExecutionOption> ActionExecutionOptions { get; } = ExecutionOptions;

    /// <summary>Every repeat policy, for the picker.</summary>
    public IReadOnlyList<ActionRepeatOption> ActionRepeatOptions { get; } = RepeatOptions;

    /// <summary>The active slot's trigger.</summary>
    /// <remarks>
    /// A slot that is not running a program has no trigger of its own, so it reads as the
    /// position's - the trigger the kind would arm if it were chosen here.
    /// </remarks>
    public ActionTrigger ActiveActionTrigger
    {
        get => ActiveSlot.TargetKind == MappingTargetKind.ComplexShortcut
            ? ActiveSlot.Trigger
            : TriggerForGesture(SelectedGestureKind);
        set
        {
            ActiveSlot.Trigger = value;

            // A release-triggered program runs to its end: nothing is held once the button
            // is up, so the other two strategies have nothing to key off and are moved off
            // rather than offered.
            if (value == ActionTrigger.Release)
            {
                ActiveActionExecution = ActionExecution.Once;
            }
            else if (ActiveSlot.Trigger == ActionTrigger.DoubleClick
                     && ActiveActionExecution == ActionExecution.WhileHeld)
            {
                // A double-click holds nothing either.
                ActiveActionExecution = ActionExecution.Once;
            }
        }
    }

    /// <summary>The active slot's execution strategy.</summary>
    public ActionExecution ActiveActionExecution
    {
        get => ActiveSlot.Execution;
        set
        {
            ActiveSlot.Execution = value;

            // A queued run starts when the current one ends - which is the release that also
            // stops a held program - so the pairing is moved off rather than refused later.
            if (value == ActionExecution.WhileHeld && ActiveActionRepeat == ActionRepeat.Queue)
            {
                ActiveActionRepeat = ActionRepeat.Restart;
            }
        }
    }

    /// <summary>The active slot's repeat policy.</summary>
    public ActionRepeat ActiveActionRepeat
    {
        get => ActiveSlot.Repeat;
        set => ActiveSlot.Repeat = value;
    }

    /// <summary>The selected trigger picker row, kept in step with <see cref="ActiveActionTrigger"/>.</summary>
    public ActionTriggerOption? ActiveActionTriggerOption
    {
        get => ActiveActionTriggerOptions.FirstOrDefault(option => option.Value == ActiveActionTrigger);
        set
        {
            if (value is not null)
            {
                ActiveActionTrigger = value.Value;
            }
        }
    }

    /// <summary>The selected execution picker row, kept in step with <see cref="ActiveActionExecution"/>.</summary>
    public ActionExecutionOption? ActiveActionExecutionOption
    {
        get => ActionExecutionOptions.FirstOrDefault(option => option.Value == ActiveActionExecution);
        set
        {
            if (value is not null)
            {
                ActiveActionExecution = value.Value;
            }
        }
    }

    /// <summary>The selected repeat picker row, kept in step with <see cref="ActiveActionRepeat"/>.</summary>
    public ActionRepeatOption? ActiveActionRepeatOption
    {
        get => ActionRepeatOptions.FirstOrDefault(option => option.Value == ActiveActionRepeat);
        set
        {
            if (value is not null)
            {
                ActiveActionRepeat = value.Value;
            }
        }
    }

    /// <summary>
    /// True when a trigger picker offers a choice at all: the double-click and the long-press
    /// positions each have exactly one trigger, so their row is a readout rather than a picker.
    /// </summary>
    public bool ActionTriggerEditable => ActiveActionTriggerOptions.Count > 1;

    /// <summary>
    /// The repeat policies the current execution strategy can use. 排队 needs a run that ends
    /// on its own, so it is not offered beside 按住期间执行.
    /// </summary>
    public IReadOnlyList<ActionRepeatOption> AvailableActionRepeatOptions => ActiveActionExecution == ActionExecution.WhileHeld
        ? [.. RepeatOptions.Where(option => option.Value != ActionRepeat.Queue)]
        : RepeatOptions;

    /// <summary>True when the strategy card is shown at all: the active slot runs a program.</summary>
    public bool ShowActionStrategy => ActiveRunsComplexShortcut;

    /// <summary>
    /// What the three strategy fields mean for the program being edited, in Chinese. The
    /// wording names the position the program rides in, because that is what decides which
    /// triggers it can answer.
    /// </summary>
    public string ActionStrategyHint => SelectedGestureKind switch
    {
        GestureKind.DoubleClick => "该程序挂在双击上：第二次按下时开始执行。",
        GestureKind.LongPress => "该程序挂在长按上：按住越过长按阈值后开始执行。",
        _ => ActiveActionTrigger == ActionTrigger.Release
            ? "该程序挂在单击上：松开按键时开始执行。"
            : "该程序挂在单击上：按下按键时开始执行。",
    };

    /// <summary>
    /// The press/release order the script produces, as plain-language lines: every output in
    /// the order the engine emits it, with the keys a hold step pressed released at the end.
    /// The preview is the compiled program run without a clock, so it shows what the user
    /// actually gets rather than the script's own words.
    /// </summary>
    public IReadOnlyList<string> ActiveActionPreview
    {
        get
        {
            if (!ActiveRunsComplexShortcut)
            {
                return [];
            }

            try
            {
                var target = ActiveSlot.BuildTarget();
                if (target is not ComplexShortcutTarget program)
                {
                    return [];
                }

                return [.. ComplexShortcutRuntime.Preview(program).Select(DescribeCommand)];
            }
            catch (ComplexShortcutParseException)
            {
                // The script card already says what is wrong; the preview stays empty
                // rather than leaking a parse failure into a second place.
                return [];
            }
        }
    }

    /// <summary>One previewed output as the user reads it.</summary>
    private static string DescribeCommand(InputCommand command) => command switch
    {
        KeyDownCommand down => $"按下 {MappingDescriptions.Key(down.Key)}",
        KeyUpCommand up => $"松开 {MappingDescriptions.Key(up.Key)}",
        MouseDownCommand mouseDown => $"按住鼠标{ButtonName(mouseDown.Button)}",
        MouseUpCommand mouseUp => $"松开鼠标{ButtonName(mouseUp.Button)}",
        TextCommand text => $"输入「{text.Text}」",
        _ => command.ToString() ?? string.Empty,
    };

    private static string ButtonName(MouseButton button) => button switch
    {
        MouseButton.Left => "左键",
        MouseButton.Right => "右键",
        MouseButton.Middle => "中键",
        MouseButton.XButton1 => "侧键1",
        MouseButton.XButton2 => "侧键2",
        _ => "按键",
    };

    /// <summary>Fills the script box with the example program, so the grammar is one click from a working script.</summary>
    [RelayCommand]
    private void FillComplexShortcutExample() => ActiveComplexShortcutScript = GestureSlotEditor.ExampleScript;

    /// <summary>
    /// Parses the active slot's script and publishes the error or the plain-language summary
    /// the script card shows. Runs on every editor notification, so the card is live while
    /// the script is typed and stays in step when the gesture or the source changes.
    /// </summary>
    private void RefreshComplexShortcutScript()
    {
        if (!ActiveRunsComplexShortcut)
        {
            ComplexShortcutScriptError = null;
            ComplexShortcutScriptSummary = null;
            return;
        }

        try
        {
            ComplexShortcutScriptSummary = MappingDescriptions.ComplexShortcutSteps(
                ComplexShortcutDsl.Parse(ActiveSlot.Script ?? string.Empty));
            ComplexShortcutScriptError = null;
        }
        catch (ComplexShortcutParseException exception)
        {
            ComplexShortcutScriptError = exception.Message;
            ComplexShortcutScriptSummary = null;
        }
    }

    /// <summary>
    /// Direction for the two cross combinations. Ignored elsewhere, where a direction is
    /// meaningless: a stick driving mouse move / wheel consumes both axes, and a button
    /// driving a key / combo / mouse button has no axis to deflect. The source description
    /// names the direction, so it is notified here too: the editor header must follow the
    /// choice the moment it is made, not at the next unrelated notification.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDirectionOption))]
    [NotifyPropertyChangedFor(nameof(SourceDescription))]
    private InputDirection _selectedDirection = InputDirection.Up;

    /// <summary>
    /// Which gesture the switcher has selected. The click leads; the double-click and the
    /// long-press are reached through the same switcher rather than buried as extras.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveSlot))]
    [NotifyPropertyChangedFor(nameof(IsClickSelected))]
    [NotifyPropertyChangedFor(nameof(IsDoubleClickSelected))]
    [NotifyPropertyChangedFor(nameof(IsLongPressSelected))]
    private GestureKind _selectedGestureKind = GestureKind.Click;

    /// <summary>True when the double-click gesture resolves. Its timing is the pinned default.</summary>
    [ObservableProperty]
    private bool _doubleClickEnabled;

    /// <summary>True when the long-press gesture resolves. Its timing is the pinned default.</summary>
    [ObservableProperty]
    private bool _longPressEnabled;

    /// <summary>
    /// True when holding the button repeats the click ("长按重复触发"). Mutually exclusive
    /// with the long-press: enabling it drops the long-press, and configuring the long-press
    /// switches it back off.
    /// </summary>
    [ObservableProperty]
    private bool _clickRepeatEnabled;

    /// <summary>
    /// True when the click itself is set to 无功能: the mapping's own target becomes a
    /// <see cref="NoneTarget"/>, so a plain press emits nothing. The click is independent of
    /// whatever the long press runs, so this reads the click's own draft alone.
    /// </summary>
    public bool ClickIsNone
    {
        get => ClickSlot.IsNone;
        set => ClickSlot.IsNone = value;
    }

    /// <summary>
    /// Seconds between long-press activations while the button is held. Not user-editable -
    /// the timing sliders are gone - but a profile that carries one keeps it through a
    /// round trip.
    /// </summary>
    public float LongPressRepeatInterval { get; set; }

    /// <summary>The lowest click-repeat interval the editor offers, in seconds.</summary>
    public const double ClickRepeatIntervalMin = 0.05;

    /// <summary>The highest click-repeat interval the editor offers, in seconds.</summary>
    public const double ClickRepeatIntervalMax = 2.00;

    /// <summary>The click-repeat interval editor's step, in seconds.</summary>
    public const double ClickRepeatIntervalStep = 0.05;

    private float _clickRepeatInterval;

    /// <summary>
    /// Seconds between click auto-repeat activations while the button is held, as the
    /// profile carries it: 0 means the pinned
    /// <see cref="ButtonGesture.DefaultClickRepeatInterval"/>. The editor's interval field
    /// reads and writes it through <see cref="ClickRepeatIntervalSeconds"/>.
    /// </summary>
    public float ClickRepeatInterval
    {
        get => _clickRepeatInterval;
        set
        {
            _clickRepeatInterval = value;
            OnPropertyChanged(nameof(ClickRepeatIntervalSeconds));

            // The repeat interval is named in the leader-key collision hint, so an edit to it
            // re-measures the draft rather than leaving the old number on screen.
            RefreshSpecialKeyConflicts();
        }
    }

    /// <summary>
    /// The click-repeat interval the editor shows, in seconds: the pinned default while no
    /// custom value is carried, otherwise the loaded one. The setter clamps to the editable
    /// range and snaps to the step, so every value the editor writes is one the profile
    /// round-trips unchanged.
    /// </summary>
    public double ClickRepeatIntervalSeconds
    {
        get => ClickRepeatInterval > 0f ? ClickRepeatInterval : ButtonGesture.DefaultClickRepeatInterval;
        set
        {
            ClickRepeatInterval = (float)SnapClickRepeatInterval(value);
            OnPropertyChanged(nameof(ClickRepeatIntervalSeconds));
        }
    }

    /// <summary>
    /// Clamps to the editable range and snaps to the step: 0.05-2.00s in 0.05s steps, so a
    /// value typed or spun into the editor lands on the grid the engine and the profile
    /// both speak.
    /// </summary>
    internal static double SnapClickRepeatInterval(double seconds)
    {
        var clamped = Math.Clamp(seconds, ClickRepeatIntervalMin, ClickRepeatIntervalMax);
        return Math.Round(clamped / ClickRepeatIntervalStep, MidpointRounding.AwayFromZero) * ClickRepeatIntervalStep;
    }

    /// <summary>
    /// What the double-click emits. Null leaves the double-click without a function of its own,
    /// so it re-fires the mapping's target - the behaviour every profile written before this
    /// field existed already has. A disabled gesture carries no action either, so an off
    /// double-click never writes a dormant target into the profile.
    /// </summary>
    public MappingTarget? DoubleClickAction
    {
        get => DoubleClickEnabled ? DoubleClickSlot.BuildAction() : null;
        set => DoubleClickSlot.LoadAction(value);
    }

    /// <summary>
    /// What the long-press emits, and what auto-repeat re-fires. Null behaves as above.
    /// A long press running a complex shortcut reads the program itself: the program is
    /// the gesture's own action, written into <see cref="ButtonGesture.LongPressAction"/>
    /// on commit, so the click target and the double-click action stay independent.
    /// </summary>
    public MappingTarget? LongPressAction
    {
        get => LongPressEnabled ? LongPressSlot.BuildAction() : null;
        set => LongPressSlot.LoadAction(value);
    }

    /// <summary>The click's target key, edited on <see cref="ClickSlot"/>.</summary>
    public GamepadKey SelectedKey
    {
        get => ClickSlot.SelectedKey;
        set => ClickSlot.SelectedKey = value;
    }

    /// <summary>The click's Ctrl modifier, edited on <see cref="ClickSlot"/>.</summary>
    public bool ModifierCtrl
    {
        get => ClickSlot.ModifierCtrl;
        set => ClickSlot.ModifierCtrl = value;
    }

    /// <summary>The click's Shift modifier, edited on <see cref="ClickSlot"/>.</summary>
    public bool ModifierShift
    {
        get => ClickSlot.ModifierShift;
        set => ClickSlot.ModifierShift = value;
    }

    /// <summary>The click's Alt modifier, edited on <see cref="ClickSlot"/>.</summary>
    public bool ModifierAlt
    {
        get => ClickSlot.ModifierAlt;
        set => ClickSlot.ModifierAlt = value;
    }

    /// <summary>The click's Win modifier, edited on <see cref="ClickSlot"/>.</summary>
    public bool ModifierWin
    {
        get => ClickSlot.ModifierWin;
        set => ClickSlot.ModifierWin = value;
    }

    /// <summary>The click's mouse button, edited on <see cref="ClickSlot"/>.</summary>
    public MouseButton SelectedMouseButton
    {
        get => ClickSlot.SelectedMouseButton;
        set => ClickSlot.SelectedMouseButton = value;
    }

    /// <summary>The click's stick-to-mouse settings, edited on <see cref="ClickSlot"/>.</summary>
    public MouseStickSettings MouseSettings
    {
        get => ClickSlot.MouseSettings;
        set => ClickSlot.MouseSettings = value;
    }

    /// <summary>The click's stick-to-wheel settings, edited on <see cref="ClickSlot"/>.</summary>
    public ScrollStickSettings ScrollSettings
    {
        get => ClickSlot.ScrollSettings;
        set => ClickSlot.ScrollSettings = value;
    }

    [ObservableProperty]
    private bool _isCapturingButton;

    [ObservableProperty]
    private bool _isCapturingKey;

    /// <summary>Categorized rows for the button picker, headers included.</summary>
    public ObservableCollection<ButtonPickerItem> ButtonOptions { get; } = [];

    /// <summary>Categorized rows for the target-key picker, headers included.</summary>
    public ObservableCollection<KeyPickerItem> KeyOptions { get; } = [];

    /// <summary>
    /// The categorized key picker as the navigator and the grid show it: one group per
    /// category, in the picker's own order. The groups hold the very same
    /// <see cref="KeyPickerItem"/> instances the flat <see cref="KeyOptions"/> list carries, so
    /// the grid and the lookup never disagree about what a key is.
    /// </summary>
    public ObservableCollection<KeyCategoryGroup> KeyCategoryGroups { get; } = [];

    /// <summary>
    /// The category the key grid currently shows, and the one its navigator segment has
    /// checked. It follows the selection: the picker's job is to put the selected key in
    /// view, so a gesture switch or a reload landing on another slot's key opens that key's
    /// category rather than the one the previous browse left behind. A click on a segment
    /// moves it too - the segment writes its check onto its group, and the group's change is
    /// adopted here.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveKeyCategoryColumns))]
    private KeyCategoryGroup? _activeKeyCategory;

    /// <summary>
    /// How many columns the active category's grid lays its keys out in. A short category
    /// shares one row - 面部按键's four caps do not float in a half-empty grid - and a long
    /// one is capped at <see cref="KeyPickerMaxColumns"/> so the card's width is spent on the
    /// grid itself instead of on a handful of over-wide caps: a denser grid is a shorter one,
    /// and a shorter grid is a shorter scroll.
    /// </summary>
    public int ActiveKeyCategoryColumns =>
        Math.Min(Math.Max(ActiveKeyCategory?.Keys.Count ?? 1, 1), KeyPickerMaxColumns);

    /// <summary>The most columns a key-cap row may use, however many keys its category has.</summary>
    private const int KeyPickerMaxColumns = 6;

    /// <summary>Source type shown by the selector; the single place the user picks button vs stick.</summary>
    public MappingSourceKind SourceKind
    {
        get => SourceIsStick ? MappingSourceKind.Stick : MappingSourceKind.Button;
        set => SourceIsStick = value == MappingSourceKind.Stick;
    }

    public bool ShowButtonPicker => !SourceIsStick;

    public bool ShowStickPicker => SourceIsStick;

    /// <summary>Rows for the direction picker.</summary>
    public IReadOnlyList<DirectionPickerItem> DirectionOptions { get; } = DirectionCategories.All;

    /// <summary>
    /// True when the current source x target pair cannot work without a direction, so the
    /// picker has to be shown. A stick is an axis, so driving a digital target from it needs
    /// to know which deflection presses; a button is binary, so driving a continuous target
    /// from it needs to know which way it deflects. Every digital target counts for the
    /// stick - key, combo, mouse button, text and special key alike - because the engine
    /// rejects a directionless stick driving any of them.
    /// </summary>
    public bool NeedsDirection => SourceIsStick
        ? TargetKind is MappingTargetKind.KeyPress or MappingTargetKind.KeyCombo
            or MappingTargetKind.MouseButton or MappingTargetKind.Text or MappingTargetKind.SpecialKey
        : TargetKind is MappingTargetKind.MouseMove or MappingTargetKind.WheelScroll;

    public bool ShowDirectionPicker => NeedsDirection;

    /// <summary>
    /// The selected direction picker row, read from <see cref="SelectedDirection"/> rather
    /// than cached, so a loaded or reset direction shows its own row instead of the one the
    /// previous edit left behind: reopening a mapping always displays the direction it saved.
    /// </summary>
    public DirectionPickerItem? SelectedDirectionOption
    {
        get => DirectionOptions.FirstOrDefault(option => option.Direction == SelectedDirection);
        set
        {
            if (value is not null)
            {
                SelectedDirection = value.Direction;
            }
        }
    }

    /// <summary>True when the double-click gesture resolves. Its timing is the pinned default.</summary>
    public bool HasDoubleClick => DoubleClickEnabled;

    /// <summary>
    /// The long-press as it actually behaves: inert while click auto-repeat is enabled, exactly
    /// as <see cref="ButtonGesture.HasEffectiveLongPress"/> says.
    /// </summary>
    public bool HasLongPress => LongPressEnabled && !ClickRepeatEnabled;

    /// <summary>True when holding the button repeats the click.</summary>
    public bool HasClickRepeat => ClickRepeatEnabled;

    /// <summary>
    /// True when the click-repeat interval editor is offered: the click tab with repeat on.
    /// The interval belongs to the click's own repeat, so it waits for the same tab the
    /// checkbox lives on.
    /// </summary>
    public bool ShowClickRepeatInterval => IsClickSelected && ClickRepeatEnabled;

    /// <summary>
    /// True when any gesture is configured, i.e. when the engine must introduce a wait before
    /// the single action can fire. False keeps the zero-latency single-click path.
    /// </summary>
    public bool HasAnyGesture => HasDoubleClick || HasLongPress || HasClickRepeat;

    /// <summary>
    /// False while click auto-repeat is on: the two are mutually exclusive, so the long-press
    /// branch of the switcher is unavailable rather than configurable into silence.
    /// </summary>
    public bool LongPressAvailable => !ClickRepeatEnabled;

    /// <summary>True when the double-click has a function of its own rather than re-firing the single.</summary>
    public bool HasDoubleClickAction => DoubleClickAction is not null;

    /// <summary>True when the long-press has a function of its own rather than re-firing the single.</summary>
    public bool HasLongPressAction => LongPressAction is not null;

    /// <summary>What the double-click currently does, for the readout beside its picker.</summary>
    public string DoubleClickActionDescription => DoubleClickAction switch
    {
        null => DoubleClickEnabled ? GestureRepeatOption.Label : GestureOffOption.Label,
        NoneTarget => GestureNoneOption.Label,
        var action => MappingDescriptions.Target(action),
    };

    /// <summary>What the long-press currently does, for the readout beside its picker.</summary>
    public string LongPressActionDescription => LongPressRunsComplexShortcut
        ? "复杂快捷键"
        : LongPressAction switch
        {
            null => LongPressEnabled ? GestureRepeatOption.Label : GestureOffOption.Label,
            NoneTarget => GestureNoneOption.Label,
            var action => MappingDescriptions.Target(action),
        };

    /// <summary>The switcher flag for the click, kept in step with <see cref="SelectedGestureKind"/>.</summary>
    public bool IsClickSelected
    {
        get => SelectedGestureKind == GestureKind.Click;
        set
        {
            if (value)
            {
                SelectedGestureKind = GestureKind.Click;
            }
        }
    }

    /// <summary>The switcher flag for the double-click, kept in step with <see cref="SelectedGestureKind"/>.</summary>
    public bool IsDoubleClickSelected
    {
        get => SelectedGestureKind == GestureKind.DoubleClick;
        set
        {
            if (value)
            {
                SelectedGestureKind = GestureKind.DoubleClick;
            }
        }
    }

    /// <summary>The switcher flag for the long-press, kept in step with <see cref="SelectedGestureKind"/>.</summary>
    public bool IsLongPressSelected
    {
        get => SelectedGestureKind == GestureKind.LongPress;
        set
        {
            if (value)
            {
                SelectedGestureKind = GestureKind.LongPress;
            }
        }
    }

    /// <summary>
    /// The selected double-click picker row, kept in step with <see cref="DoubleClickAction"/>.
    /// Picking any row but 已关闭 configures the gesture; 已关闭 turns it off. A hand-edited
    /// profile may name a target the picker does not list, which leaves this null while the
    /// action itself survives the round-trip untouched.
    /// </summary>
    public GestureActionOption? SelectedDoubleClickOption
    {
        get => _selectedDoubleClickOption;
        set
        {
            if (EqualityComparer<GestureActionOption?>.Default.Equals(value, _selectedDoubleClickOption))
            {
                return;
            }

            DoubleClickEnabled = value is not null && !ReferenceEquals(value, GestureOffOption);
            DoubleClickAction = value?.Target;
            _selectedDoubleClickOption = value;
            OnPropertyChanged(nameof(SelectedDoubleClickOption));
        }
    }

    /// <summary>The selected long-press picker row, kept in step with <see cref="LongPressAction"/>.</summary>
    public GestureActionOption? SelectedLongPressOption
    {
        get => _selectedLongPressOption;
        set
        {
            if (EqualityComparer<GestureActionOption?>.Default.Equals(value, _selectedLongPressOption))
            {
                return;
            }

            LongPressEnabled = value is not null && !ReferenceEquals(value, GestureOffOption);
            LongPressAction = value?.Target;
            _selectedLongPressOption = value;
            OnPropertyChanged(nameof(SelectedLongPressOption));
        }
    }

    private void SyncDoubleClickOption()
    {
        // A complex shortcut is not one of the picker's rows, so it reads as unlisted - the
        // same shape a hand-edited profile naming a target the picker does not carry has.
        var option = DoubleClickRunsComplexShortcut
            ? null
            : DoubleClickAction is { } action
                ? GestureActionOptions.FirstOrDefault(
                    candidate => EqualityComparer<MappingTarget>.Default.Equals(candidate.Target, action))
                : DoubleClickEnabled
                    ? GestureRepeatOption
                    : GestureOffOption;

        if (!EqualityComparer<GestureActionOption?>.Default.Equals(option, _selectedDoubleClickOption))
        {
            _selectedDoubleClickOption = option;
            OnPropertyChanged(nameof(SelectedDoubleClickOption));
        }
    }

    private void SyncLongPressOption()
    {
        // A complex shortcut is not one of the picker's rows, so it reads as unlisted - the
        // same shape a hand-edited profile naming a target the picker does not carry has.
        var option = LongPressRunsComplexShortcut
            ? null
            : LongPressAction is { } action
                ? GestureActionOptions.FirstOrDefault(
                    candidate => EqualityComparer<MappingTarget>.Default.Equals(candidate.Target, action))
                : LongPressEnabled
                    ? GestureRepeatOption
                    : GestureOffOption;

        if (!EqualityComparer<GestureActionOption?>.Default.Equals(option, _selectedLongPressOption))
        {
            _selectedLongPressOption = option;
            OnPropertyChanged(nameof(SelectedLongPressOption));
        }
    }

    public string CaptureButtonLabel => IsCapturingButton ? "停止捕获" : "捕获按键";

    public string CaptureKeyLabel => IsCapturingKey ? "停止捕获" : "捕获键盘";

    /// <summary>What the model element being edited is called, e.g. "左肩键 LB".</summary>
    public string SourceDescription => MappingDescriptions.Source(BuildSource());

    /// <summary>What the chosen target does, e.g. "Ctrl + C", or 无功能 when the click does nothing.</summary>
    public string TargetDescription
    {
        get
        {
            if (ActiveTargetUnsupported)
            {
                return ActiveTargetUnsupportedDescription ?? "不支持的映射";
            }

            if (ClickIsNone)
            {
                return "无功能";
            }

            var target = BuildTarget();

            return target is SpecialKeyTarget special
                ? SpecialKeyDescription(special)
                : MappingDescriptions.Target(target);
        }
    }

    /// <summary>
    /// The selected picker row. A category header is a label, not a choice, so assigning one
    /// snaps the selection back to the button that is actually selected.
    /// </summary>
    public ButtonPickerItem? SelectedButtonOption
    {
        get => _selectedButtonOption;
        set
        {
            if (EqualityComparer<ButtonPickerItem?>.Default.Equals(value, _selectedButtonOption))
            {
                return;
            }

            if (value is { IsHeader: true })
            {
                SetSelectedButtonOption(CurrentOption());
                return;
            }

            SetSelectedButtonOption(value);

            if (value is not null)
            {
                SelectedButton = value.Button;
                SelectedExtraCode = value.Code;
            }
        }
    }

    /// <summary>The click's modifiers, read from <see cref="ClickSlot"/>.</summary>
    public KeyModifiers Modifiers => ClickSlot.Modifiers;

    /// <summary>
    /// The selected picker row for the target key. A category header is a label, not a choice,
    /// so assigning one snaps the selection back to the key that is actually selected.
    /// </summary>
    public KeyPickerItem? SelectedKeyOption
    {
        get => _selectedKeyOption;
        set
        {
            if (EqualityComparer<KeyPickerItem?>.Default.Equals(value, _selectedKeyOption))
            {
                return;
            }

            if (value is { IsHeader: true })
            {
                SetSelectedKeyOption(CurrentKeyOption());
                return;
            }

            SetSelectedKeyOption(value);

            if (value is not null)
            {
                SelectedKey = value.Key;

                // Picking a key implies a key target; a mouse-button target would silently
                // drop it, so the kind follows the choice.
                if (TargetKind == MappingTargetKind.MouseButton)
                {
                    TargetKind = MappingTargetKind.KeyPress;
                }
            }
        }
    }

    public InputBinding BuildSource()
    {
        // A direction is required for the two cross combinations; elsewhere the value the
        // profile carried is preserved so a round-trip never silently drops one.
        var direction = NeedsDirection ? SelectedDirection : _loadedDirection;

        return SourceIsStick
            ? new StickBinding(SelectedStick) { Direction = direction }
            : new ButtonBinding(SelectedButton, SelectedExtraCode) { Direction = direction, Gesture = BuildGesture() };
    }

    /// <summary>
    /// The gesture for the button being edited, or null when nothing is enabled. Null - rather
    /// than an all-zero <see cref="ButtonGesture"/> - is what keeps the press edge firing with
    /// no added latency and keeps ordinary profiles free of gesture JSON. The timings are the
    /// Core's pinned defaults: nothing here reads a user-facing slider.
    /// </summary>
    public ButtonGesture? BuildGesture()
    {
        var doubleClickWindow = HasDoubleClick ? ButtonGesture.DefaultDoubleClickWindow : 0f;
        var longPressThreshold = HasLongPress ? ButtonGesture.DefaultLongPressThreshold : 0f;

        // A carried interval wins; a freshly enabled repeat with nothing loaded falls back to
        // the pinned default, exactly as it did before profiles could carry their own.
        var clickRepeatInterval = ClickRepeatEnabled
            ? ClickRepeatInterval > 0f ? ClickRepeatInterval : ButtonGesture.DefaultClickRepeatInterval
            : 0f;

        if (doubleClickWindow <= 0f && longPressThreshold <= 0f && clickRepeatInterval <= 0f)
        {
            return null;
        }

        return new ButtonGesture
        {
            DoubleClickWindow = doubleClickWindow,
            LongPressThreshold = longPressThreshold,
            LongPressRepeatInterval = LongPressRepeatInterval,
            ClickRepeatInterval = clickRepeatInterval,
            DoubleClickAction = DoubleClickAction,
            LongPressAction = LongPressAction,
        };
    }

    /// <summary>
    /// The mapping's own target: the click draft's target, always. A complex shortcut is the
    /// long press's own action rather than the mapping's target, so the click - and the
    /// double-click - keep targets of their own and the program rides in the gesture.
    /// </summary>
    public MappingTarget BuildTarget() => ClickSlot.BuildTarget();

    public MappingEntry BuildEntry() => new()
    {
        Source = BuildSource(),
        Target = BuildTarget(),

        // The layer tag travels with the entry. Dropping it would move a mapping the user
        // placed on a temporary layer back onto the base layer on the next edit, and the
        // layer would quietly stop running.
        Layer = SelectedLayer,
    };

    public void LoadFrom(MappingEntry entry)
    {
        // The editor is reused across edits, and the previous edit may have left the
        // long-press draft running a complex shortcut - a state whose guards refuse click
        // auto-repeat and the long press itself, because each would leave the program
        // unreachable. Those guards answer interactive requests; a load replaces the draft
        // wholesale, so the state is stood down first and the incoming mapping's flags load
        // exactly as its gesture carries them.
        if (LongPressSlot.TargetKind == MappingTargetKind.ComplexShortcut)
        {
            LongPressSlot.Reset();
        }

        switch (entry.Source)
        {
            case ButtonBinding button:
                SourceIsStick = false;
                if (button.Button == GamepadButton.Extra)
                {
                    RegisterExtraButton(button.Code);
                }

                SelectedButton = button.Button;
                SelectedExtraCode = button.Code;
                _loadedDirection = button.Direction;
                SelectedDirection = button.Direction ?? InputDirection.Up;

                // The timings are pinned, so a loaded gesture is expressed as enabled flags.
                // Click auto-repeat is read first: the Core makes the long-press inert while it
                // runs, so an inert long-press action is dropped rather than loaded into a
                // gesture that can never fire it.
                ClickRepeatEnabled = button.Gesture is { HasClickRepeat: true };
                ClickRepeatInterval = button.Gesture?.ClickRepeatInterval ?? 0f;
                DoubleClickEnabled = button.Gesture is { HasDoubleClick: true };
                LongPressEnabled = button.Gesture is { HasEffectiveLongPress: true };
                LongPressRepeatInterval = button.Gesture?.LongPressRepeatInterval ?? 0f;

                // Assigned after the flags so a profile carrying an action at a zero timing is
                // repaired by the same rule the picker uses, rather than silently dropped.
                DoubleClickAction = button.Gesture?.DoubleClickAction;
                LongPressAction = button.Gesture is { HasEffectiveLongPress: true }
                    ? button.Gesture.LongPressAction
                    : null;
                break;
            case StickBinding stick:
                SourceIsStick = true;
                SelectedStick = stick.Stick;
                _loadedDirection = stick.Direction;
                SelectedDirection = stick.Direction ?? InputDirection.Up;
                break;
        }

        // The click draft takes the mapping's own target; the two gesture drafts were filled
        // from the button's gesture above. A v1 complex shortcut - the program as the mapping's
        // own target with a bare long press - is the exception: its program becomes the long
        // press's action and the click reads 无功能, which is what a quick press did when the
        // program owned the target.
        // The v1 shape is recognised from the target alone: a program sitting on the
        // mapping's own target with the long-press trigger every profile written before the
        // strategy fields existed has. One that also carries a long-press action of its own
        // is a profile the engine refuses, so it loads as it stands rather than having the
        // action silently dropped.
        var v1Program = entry.Target is ComplexShortcutTarget { Trigger: ActionTrigger.LongPress } program
            && entry.Source is ButtonBinding { Gesture: { LongPressAction: null } }
            ? program
            : null;

        if (v1Program is not null)
        {
            LongPressSlot.LoadAction(v1Program);
            ClickSlot.LoadAction(new NoneTarget());

            // A hand-edited profile may pair the program with gestures the engine refuses;
            // the editor repairs it to the bare long press the program runs from, exactly as
            // the picker repairs an action carried at a zero timing.
            ClickRepeatEnabled = false;
            LongPressEnabled = true;
            LongPressRepeatInterval = 0f;
        }
        else
        {
            ClickSlot.LoadAction(entry.Target);
        }

        // The optional key list is collapsed on every editor, so a reloaded mapping opens on
        // the compact surface rather than wherever the previous edit left the list.
        KeyPickerExpanded = false;

        // The layer the mapping sits on is part of the entry: a mapping placed on the
        // temporary layer has to come back on that layer, or the edit would move it to the
        // base layer. A profile written before the single-layer model may carry a higher
        // number - the engine reads any non-zero value as the temporary layer - so the
        // loaded layer is folded onto the one that remains.
        SelectedLayer = entry.Layer == 0 ? 0 : 1;

        // A load lands the whole draft at once, so the conflict hints are re-measured once
        // here rather than left to whatever the individual setters happened to raise.
        RefreshSpecialKeyConflicts();
    }

    public void Reset()
    {
        IsCapturingButton = false;
        IsCapturingKey = false;
        _previousCaptureButtons = GamepadButtonMask.None;
        _previousCaptureExtra.Clear();
        _loadedDirection = null;
        SourceIsStick = false;
        SelectedButton = GamepadButton.A;
        SelectedExtraCode = 0;
        SelectedStick = GamepadStick.Right;
        SelectedDirection = InputDirection.Up;
        SelectedGestureKind = GestureKind.Click;
        SelectedLayer = 0;
        KeyPickerExpanded = false;

        // The drafts go back to pristine first; the gesture flags and actions follow, so a
        // reset gesture ends up off rather than carrying a dormant target.
        ClickSlot.Reset();
        DoubleClickSlot.Reset();
        LongPressSlot.Reset();
        DoubleClickEnabled = false;
        LongPressEnabled = false;
        ClickRepeatEnabled = false;
        LongPressRepeatInterval = 0f;
        ClickRepeatInterval = 0f;
        DoubleClickAction = null;
        LongPressAction = null;

        // The picker's category follows the selection, so a reopened editor opens on the key
        // that is selected rather than on the browse the previous edit left behind. The active
        // option is often unchanged by a reset - the default key is the default key - and the
        // follow in SetActiveSelectedKeyOption would then never run, so it is called directly.
        FollowActiveKeyCategory(CurrentActiveKeyOption());

        RefreshSpecialKeyConflicts();
    }

    /// <summary>
    /// Arms the editor for one source, as implied by a click on the gamepad model. The source
    /// is never chosen here: the clicked element decides it, so this only resets the target
    /// and points the editor at that element.
    /// </summary>
    public void LoadSource(InputBinding source)
    {
        Reset();

        switch (source)
        {
            case ButtonBinding button:
                if (button.Button == GamepadButton.Extra)
                {
                    RegisterExtraButton(button.Code);
                }

                SelectedButton = button.Button;
                SelectedExtraCode = button.Code;
                break;
            case StickBinding stick:
                SourceIsStick = true;
                SelectedStick = stick.Stick;

                // A freshly armed stick starts on the target a stick is normally used for;
                // every other kind stays reachable through the target picker.
                TargetKind = MappingTargetKind.MouseMove;
                break;
        }
    }

    /// <summary>
    /// Adds a raw platform code to the picker's 扩展按键 group. Codes describe the connected
    /// device rather than one mapping, so they survive <see cref="Reset"/>.
    /// </summary>
    public void RegisterExtraButton(int code)
    {
        if (code <= 0 || !_extraButtonCodes.Add(code))
        {
            return;
        }

        RebuildButtonOptions();
    }

    /// <summary>
    /// Feeds one engine sample into button capture and into the on-screen keyboard, in that
    /// order. Only a press edge selects a button, so a button held down before capture was
    /// armed is ignored; samples are also tracked while capture is disarmed so arming it never
    /// fires on an already-held button. Returns true when this sample was spent here - as the
    /// mapping's new source, or on the keyboard's cursor, key or page - so the caller knows the
    /// press must not also drive navigation.
    /// </summary>
    public bool CaptureGamepadSample(IGamepadState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var pressed = state.PressedButtons;
        var edges = pressed & ~_previousCaptureButtons;
        var consumed = false;

        if (IsCapturingButton)
        {
            var edge = FirstPressed(edges);
            if (edge is not null)
            {
                SelectButton(edge.Value, 0);
                IsCapturingButton = false;
                consumed = true;
            }
            else
            {
                foreach (var code in state.ExtraButtons)
                {
                    if (!_previousCaptureExtra.Contains(code))
                    {
                        SelectButton(GamepadButton.Extra, code);
                        IsCapturingButton = false;
                        consumed = true;
                        break;
                    }
                }
            }
        }

        // The keyboard answers the pad only once the capture above has had its say: a press
        // that armed a capture has already named the source it wants.
        if (!consumed && TextKeyboardGamepadInScope && ShowTextKeyboard && CaptureTextKeyboard(edges))
        {
            consumed = true;
        }

        _previousCaptureButtons = pressed;
        _previousCaptureExtra.Clear();
        foreach (var code in state.ExtraButtons)
        {
            _previousCaptureExtra.Add(code);
        }

        return consumed;
    }

    /// <summary>
    /// Routes one sample's press edges to the on-screen keyboard: the D-pad moves the cursor,
    /// A presses the key the cursor sits on and the bumpers turn the page. Returns false when
    /// the keyboard spent nothing - an edge it does not own, or a push the cursor refused at
    /// the page's edge - so the caller knows the press may still navigate, exactly as an arrow
    /// key at the edge falls through to the body's scroll.
    /// </summary>
    private bool CaptureTextKeyboard(GamepadButtonMask edges)
    {
        // The bumpers turn the page and A presses the focused key: both are the keyboard's
        // own, so the press is spent even when there is only one page to show.
        if (edges.Has(GamepadButton.LeftBumper))
        {
            ShowPreviousTextKeyboardPage();
            return true;
        }

        if (edges.Has(GamepadButton.RightBumper))
        {
            ShowNextTextKeyboardPage();
            return true;
        }

        if (edges.Has(GamepadButton.A))
        {
            ActivateTextKeyboardKey();
            return true;
        }

        var direction = DirectionOf(edges);
        return direction is not null && MoveTextKeyboardCursor(direction.Value);
    }

    /// <summary>The direction a sample's edges name, or null when they name none.</summary>
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
    /// Records a captured keyboard key together with the modifiers held with it. A lone key
    /// becomes a single-key target; any modifier turns it into a combo target. The capture
    /// writes the slot the switcher has selected, so the same affordance configures the
    /// double-click and the long-press as peers of the click.
    /// </summary>
    public void CaptureKeyboardKey(GamepadKey key, KeyModifiers modifiers)
    {
        if (!IsCapturingKey)
        {
            return;
        }

        // A key target is only honored for a button source, so capturing one repairs a stick
        // source instead of producing a mapping the engine would silently drop.
        SourceIsStick = false;

        var slot = ActiveSlot;
        slot.TargetKind = modifiers == KeyModifiers.None ? MappingTargetKind.KeyPress : MappingTargetKind.KeyCombo;
        slot.SelectedKey = key;
        slot.ModifierCtrl = modifiers.HasFlag(KeyModifiers.Ctrl);
        slot.ModifierShift = modifiers.HasFlag(KeyModifiers.Shift);
        slot.ModifierAlt = modifiers.HasFlag(KeyModifiers.Alt);
        slot.ModifierWin = modifiers.HasFlag(KeyModifiers.Win);
    }

    /// <summary>Commits keyboard capture once every key has been released.</summary>
    public void EndKeyboardCapture()
    {
        if (!IsCapturingKey)
        {
            return;
        }

        IsCapturingKey = false;
    }

    [RelayCommand]
    private void ToggleCaptureButton() => IsCapturingButton = !IsCapturingButton;

    [RelayCommand]
    private void ToggleCaptureKey() => IsCapturingKey = !IsCapturingKey;

    partial void OnOtherMappingsChanged(IReadOnlyList<MappingEntry> value) => RefreshSpecialKeyConflicts();

    partial void OnSourceIsStickChanged(bool value)
    {
        OnPropertyChanged(nameof(SourceKind));
        OnPropertyChanged(nameof(ShowButtonPicker));
        OnPropertyChanged(nameof(ShowStickPicker));
        OnPropertyChanged(nameof(SourceDescription));
        OnPropertyChanged(nameof(NeedsDirection));
        OnPropertyChanged(nameof(ShowDirectionPicker));

        // Every kind is valid for both source kinds, so the list never has to be narrowed and
        // the chosen target survives a source switch. Only the direction picker comes and goes.
        AvailableTargetKinds = AvailableKindsForGesture(SelectedGestureKind);

        // A stick has no gestures, so a program the long press was running stops being one:
        // the switcher row is hidden and the kind leaves the list.
        NotifyActiveSlotChanged();
    }

    /// <summary>
    /// The kinds the 类型 picker offers for one source. A stick has no gestures, so nothing
    /// can start a program there and 复杂快捷键 stays off its list.
    /// </summary>
    private MappingTargetKind[] AvailableKindsForGesture(GestureKind gesture)
        => SourceIsStick ? StickTargetKinds : AllTargetKinds;

    partial void OnSelectedButtonChanged(GamepadButton value)
    {
        if (value != GamepadButton.Extra && SelectedExtraCode != 0)
        {
            SelectedExtraCode = 0;
        }

        SetSelectedButtonOption(CurrentOption());
        OnPropertyChanged(nameof(SourceDescription));
        RefreshSpecialKeyConflicts();
    }

    partial void OnSelectedExtraCodeChanged(int value)
    {
        SetSelectedButtonOption(CurrentOption());
        OnPropertyChanged(nameof(SourceDescription));
        RefreshSpecialKeyConflicts();
    }

    partial void OnSelectedStickChanged(GamepadStick value)
        => OnPropertyChanged(nameof(SourceDescription));

    partial void OnSelectedGestureKindChanging(GestureKind value)
    {
        // The picker's list follows the gesture, and it is swapped here - before the active
        // slot moves - on purpose. Replacing the collection makes the combo re-assert its
        // selection, and that write-back has to land on the slot the selection came from:
        // swapping after the move would copy the outgoing gesture's kind into the incoming
        // slot, silently rewriting a draft the user never touched.
        AvailableTargetKinds = AvailableKindsForGesture(value);
    }

    partial void OnSelectedGestureKindChanged(GestureKind value)
    {
        // Selecting the long-press switches auto-repeat off: the two are mutually exclusive,
        // so the long-press branch is only ever reachable with auto-repeat disarmed.
        if (value == GestureKind.LongPress)
        {
            ClickRepeatEnabled = false;
        }

        OnPropertyChanged(nameof(ShowClickRepeatInterval));
        NotifyActiveSlotChanged();
    }

    partial void OnClickRepeatEnabledChanged(bool value)
    {
        if (value && RunsComplexShortcut)
        {
            // The repeat makes the click re-fire whatever sits on it - a program included -
            // and makes the long press inert, so a program could never run as configured:
            // the request is refused rather than written into a mapping the engine rejects.
            ClickRepeatEnabled = false;
            return;
        }

        if (value)
        {
            // Auto-repeat makes the long-press inert, so the long-press is dropped rather than
            // left configured: the user must never see a long-press that silently does nothing.
            LongPressEnabled = false;
            LongPressAction = null;

            if (SelectedGestureKind == GestureKind.LongPress)
            {
                SelectedGestureKind = GestureKind.Click;
            }
        }

        OnPropertyChanged(nameof(LongPressAvailable));
        OnPropertyChanged(nameof(HasLongPress));
        OnPropertyChanged(nameof(HasAnyGesture));
        OnPropertyChanged(nameof(ShowClickRepeatInterval));

        // Auto-repeat on a leader key's button is a collision, and switching it off resolves
        // one: both directions re-measure the draft, so the hint never outruns the edit.
        RefreshSpecialKeyConflicts();
    }

    partial void OnDoubleClickEnabledChanged(bool value)
    {
        SyncDoubleClickOption();
        OnPropertyChanged(nameof(HasDoubleClick));
        OnPropertyChanged(nameof(HasAnyGesture));
        NotifyActiveSlotChanged();
    }

    partial void OnLongPressEnabledChanged(bool value)
    {
        if (!value && LongPressRunsComplexShortcut)
        {
            // The program is the long press's own action: a long press that cannot fire
            // would leave it unreachable, so switching the gesture off is refused. The
            // 启用手势 checkbox is disabled with a hint for the same reason.
            LongPressEnabled = true;
            return;
        }
        if (value)
        {
            // Configuring the long-press switches auto-repeat off, and vice versa.
            ClickRepeatEnabled = false;
        }

        SyncLongPressOption();
        OnPropertyChanged(nameof(HasLongPress));
        OnPropertyChanged(nameof(HasAnyGesture));
        NotifyActiveSlotChanged();
    }

    /// <summary>
    /// Keeps the click surface in step with its draft: the same notifications the old
    /// per-field handlers raised, now driven by the draft the fields live on.
    /// </summary>
    private void OnClickSlotChanged(string? name)
    {
        switch (name)
        {
            case nameof(GestureSlotEditor.TargetKind):
                OnPropertyChanged(nameof(TargetKind));
                OnPropertyChanged(nameof(TargetDescription));
                OnPropertyChanged(nameof(NeedsDirection));
                OnPropertyChanged(nameof(ShowDirectionPicker));
                break;
            case nameof(GestureSlotEditor.SelectedKey):
                SetSelectedKeyOption(CurrentKeyOption());
                OnPropertyChanged(nameof(SelectedKey));
                OnPropertyChanged(nameof(TargetDescription));
                break;
            case nameof(GestureSlotEditor.ModifierCtrl):
            case nameof(GestureSlotEditor.ModifierShift):
            case nameof(GestureSlotEditor.ModifierAlt):
            case nameof(GestureSlotEditor.ModifierWin):
                OnPropertyChanged(name);
                OnPropertyChanged(nameof(TargetDescription));
                break;
            case nameof(GestureSlotEditor.SelectedMouseButton):
                OnPropertyChanged(nameof(SelectedMouseButton));
                OnPropertyChanged(nameof(TargetDescription));
                break;
            case nameof(GestureSlotEditor.Text):
                OnPropertyChanged(nameof(ActiveText));
                OnPropertyChanged(nameof(ActiveTextLength));
                OnPropertyChanged(nameof(TargetDescription));
                break;
            case nameof(GestureSlotEditor.Unsupported):
                OnPropertyChanged(nameof(ActiveTargetUnsupported));
                OnPropertyChanged(nameof(ActiveTargetUnsupportedDescription));
                OnPropertyChanged(nameof(UnsupportedTargetHint));
                OnPropertyChanged(nameof(TargetDescription));
                break;
            case nameof(GestureSlotEditor.Trigger):
            case nameof(GestureSlotEditor.Execution):
            case nameof(GestureSlotEditor.Repeat):
                OnPropertyChanged(nameof(ActiveActionTrigger));
                OnPropertyChanged(nameof(ActiveActionTriggerOption));
                OnPropertyChanged(nameof(ActionTriggerEditable));
                OnPropertyChanged(nameof(ActiveActionExecution));
                OnPropertyChanged(nameof(ActiveActionExecutionOption));
                OnPropertyChanged(nameof(ActiveActionRepeat));
                OnPropertyChanged(nameof(ActiveActionRepeatOption));
                OnPropertyChanged(nameof(AvailableActionRepeatOptions));
                OnPropertyChanged(nameof(ActionStrategyHint));
                OnPropertyChanged(nameof(ActiveActionPreview));
                OnPropertyChanged(nameof(TargetDescription));
                break;
            case nameof(GestureSlotEditor.MouseSettings):
                OnPropertyChanged(nameof(MouseSettings));
                OnPropertyChanged(nameof(ActivePrecisionButton));
                break;
            case nameof(GestureSlotEditor.ScrollSettings):
                OnPropertyChanged(nameof(ScrollSettings));
                break;
            case nameof(GestureSlotEditor.SpecialKind):
            case nameof(GestureSlotEditor.SpecialLeaderAction):
                // A layer leader is the one special key that has to sit on the base layer,
                // so choosing the kind - or choosing what the leader switches - re-measures
                // where the mapping itself may live.
                EnforceLayerLeaderPlacement();
                OnPropertyChanged(nameof(TargetDescription));
                break;
            case nameof(GestureSlotEditor.SpecialPrecisionMode):
            case nameof(GestureSlotEditor.SpecialSwitchMode):
            case nameof(GestureSlotEditor.SpecialSlotA):
            case nameof(GestureSlotEditor.SpecialSlotB):
                OnPropertyChanged(nameof(TargetDescription));
                break;
            case nameof(GestureSlotEditor.IsNone):
            case nameof(GestureSlotEditor.Function):
                OnPropertyChanged(nameof(ClickIsNone));
                OnPropertyChanged(nameof(TargetDescription));
                break;
        }

        NotifyActiveSlotChanged();
    }

    /// <summary>
    /// Keeps the double-click surface in step with its draft. An action with no gesture can
    /// never resolve, so a draft that gained a function of its own enables the gesture.
    /// </summary>
    private void OnDoubleClickSlotChanged()
    {
        if (DoubleClickSlot.BuildAction() is not null)
        {
            DoubleClickEnabled = true;
        }

        SyncDoubleClickOption();
        OnPropertyChanged(nameof(DoubleClickAction));
        OnPropertyChanged(nameof(HasDoubleClickAction));
        OnPropertyChanged(nameof(DoubleClickActionDescription));
        OnPropertyChanged(nameof(DoubleClickRunsComplexShortcut));
        OnPropertyChanged(nameof(RunsComplexShortcut));
        NotifyActiveSlotChanged();
    }

    /// <summary>Keeps the long-press surface in step with its draft, as the double-click does.</summary>
    private void OnLongPressSlotChanged()
    {
        if (LongPressSlot.BuildAction() is not null)
        {
            LongPressEnabled = true;
            ClickRepeatEnabled = false;
        }

        SyncLongPressOption();
        OnPropertyChanged(nameof(LongPressAction));
        OnPropertyChanged(nameof(HasLongPressAction));
        OnPropertyChanged(nameof(LongPressActionDescription));
        OnPropertyChanged(nameof(LongPressRunsComplexShortcut));
        OnPropertyChanged(nameof(RunsComplexShortcut));
        NotifyActiveSlotChanged();
    }

    /// <summary>
    /// Re-reads every active-slot adapter. The adapters are thin views over
    /// <see cref="ActiveSlot"/>, so any slot edit, any enabled-flag change and any switcher
    /// move goes through here; the ones whose value did not change cost one binding
    /// evaluation and nothing else.
    /// </summary>
    private void NotifyActiveSlotChanged()
    {
        OnPropertyChanged(nameof(ActiveTargetKind));
        OnPropertyChanged(nameof(ActiveSelectedKey));
        OnPropertyChanged(nameof(ActiveModifierCtrl));
        OnPropertyChanged(nameof(ActiveModifierShift));
        OnPropertyChanged(nameof(ActiveModifierAlt));
        OnPropertyChanged(nameof(ActiveModifierWin));
        OnPropertyChanged(nameof(ActiveSelectedMouseButton));
        OnPropertyChanged(nameof(ActiveMouseSettings));
        OnPropertyChanged(nameof(ActivePrecisionButton));
        OnPropertyChanged(nameof(ActivePrecisionButtonLabel));
        OnPropertyChanged(nameof(ActiveScrollSettings));
        OnPropertyChanged(nameof(ActiveSpecialKind));
        OnPropertyChanged(nameof(ActiveSpecialKindOption));
        OnPropertyChanged(nameof(ActiveSpecialPrecisionMode));
        OnPropertyChanged(nameof(ActiveSpecialPrecisionModeOption));
        OnPropertyChanged(nameof(ActiveSpecialSwitchMode));
        OnPropertyChanged(nameof(ActiveSpecialSwitchModeOption));
        OnPropertyChanged(nameof(ActiveSpecialSlotA));
        OnPropertyChanged(nameof(ActiveSpecialSlotB));
        OnPropertyChanged(nameof(ActiveSpecialLeaderAction));
        OnPropertyChanged(nameof(ActiveSpecialLeaderActionOption));
        OnPropertyChanged(nameof(ShowSpecialLeaderControls));
        OnPropertyChanged(nameof(ShowSpecialPresetControls));
        OnPropertyChanged(nameof(ShowSpecialSwitchControls));
        OnPropertyChanged(nameof(SpecialSwitchModeEditable));
        OnPropertyChanged(nameof(ShowSpecialLayerControls));
        OnPropertyChanged(nameof(SpecialLayerHint));
        OnPropertyChanged(nameof(ShowSpecialPrecisionControls));
        OnPropertyChanged(nameof(SpecialKeySlotConflict));
        OnPropertyChanged(nameof(LayerLeaderRequiresBaseLayer));
        OnPropertyChanged(nameof(LayerHint));
        OnPropertyChanged(nameof(ActiveIsNone));
        OnPropertyChanged(nameof(ActiveRepeatsClick));
        OnPropertyChanged(nameof(ActiveGestureEnabled));
        OnPropertyChanged(nameof(ActiveGestureCanBeDisabled));
        OnPropertyChanged(nameof(ActiveGestureName));
        OnPropertyChanged(nameof(ActiveTargetDescription));
        OnPropertyChanged(nameof(ActiveTargetEditable));
        OnPropertyChanged(nameof(ActiveText));
        OnPropertyChanged(nameof(ActiveTextLength));
        OnPropertyChanged(nameof(ShowTextKeyboard));
        OnPropertyChanged(nameof(ActiveTargetUnsupported));
        OnPropertyChanged(nameof(ActiveTargetUnsupportedDescription));
        OnPropertyChanged(nameof(UnsupportedTargetHint));

        // The strategy surface: which tab can carry a program, the trigger/execution/repeat
        // pickers, and the press/release preview of the script.
        OnPropertyChanged(nameof(ClickRunsComplexShortcut));
        OnPropertyChanged(nameof(DoubleClickRunsComplexShortcut));
        OnPropertyChanged(nameof(LongPressRunsComplexShortcut));
        OnPropertyChanged(nameof(RunsComplexShortcut));
        OnPropertyChanged(nameof(ActiveRunsComplexShortcut));
        OnPropertyChanged(nameof(ShowActionStrategy));
        OnPropertyChanged(nameof(ActionTriggerEditable));
        OnPropertyChanged(nameof(ActionStrategyHint));
        OnPropertyChanged(nameof(ActiveActionTriggerOptions));
        OnPropertyChanged(nameof(ActiveActionPreview));

        // The conflict hints ride the same chokepoint: every draft edit re-measures the
        // draft against the profile's other mappings on its button.
        RefreshSpecialKeyConflicts();

        // The complex-shortcut surface: which tab offers the kind, whether the script card
        // is armed, and what its validation line says.
        OnPropertyChanged(nameof(LongPressRunsComplexShortcut));
        OnPropertyChanged(nameof(ActiveGestureToggleEnabled));
        OnPropertyChanged(nameof(ClickIsNone));
        OnPropertyChanged(nameof(ActiveComplexShortcutScript));
        RefreshComplexShortcutScript();

        SetActiveSelectedKeyOption(CurrentActiveKeyOption());
    }

    private KeyPickerItem? CurrentActiveKeyOption()
        => KeyOptions.FirstOrDefault(
            option => !option.IsHeader && option.Key == ActiveSlot.SelectedKey);

    private void SetActiveSelectedKeyOption(KeyPickerItem? option)
    {
        if (EqualityComparer<KeyPickerItem?>.Default.Equals(option, _activeSelectedKeyOption))
        {
            return;
        }

        _activeSelectedKeyOption = option;
        OnPropertyChanged(nameof(ActiveSelectedKeyOption));
        FollowActiveKeyCategory(option);
    }

    /// <summary>
    /// Opens the category the newly selected key lives in. Every path that moves the active
    /// selection - a cap click, a gesture switch, a load, a reset - lands here, so the
    /// selected key is always the one in view.
    /// </summary>
    private void FollowActiveKeyCategory(KeyPickerItem? option)
    {
        if (option is null)
        {
            return;
        }

        var group = KeyCategoryGroups.FirstOrDefault(
            candidate => candidate.Keys.Any(item => item.Key == option.Key));

        if (group is not null)
        {
            ActiveKeyCategory = group;
        }
    }

    partial void OnActiveKeyCategoryChanged(KeyCategoryGroup? value)
    {
        // The navigator mirrors the selection: the newly active category's segment is checked
        // and every other one is unchecked, so a programmatic move - a gesture switch landing
        // on a key in another category - marks the same segment a click would.
        foreach (var group in KeyCategoryGroups)
        {
            if (!ReferenceEquals(group, value))
            {
                group.IsActive = false;
            }
        }

        if (value is not null)
        {
            value.IsActive = true;
        }
    }

    /// <summary>
    /// A navigator segment was checked by pointer or by keyboard: its category becomes the one
    /// the grid shows. Unchecking is ignored - exactly one category is shown at a time, and
    /// <see cref="OnActiveKeyCategoryChanged"/> is what clears the rest.
    /// </summary>
    private void OnKeyCategoryGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KeyCategoryGroup.IsActive)
            && sender is KeyCategoryGroup { IsActive: true } group)
        {
            ActiveKeyCategory = group;
        }
    }

    [RelayCommand]
    private void ClearDoubleClickAction()
    {
        DoubleClickEnabled = false;
        DoubleClickAction = null;
    }

    [RelayCommand]
    private void ClearLongPressAction()
    {
        LongPressEnabled = false;
        LongPressAction = null;
    }

    partial void OnIsCapturingButtonChanged(bool value)
        => OnPropertyChanged(nameof(CaptureButtonLabel));

    partial void OnIsCapturingKeyChanged(bool value)
        => OnPropertyChanged(nameof(CaptureKeyLabel));

    private void SelectButton(GamepadButton button, int code)
    {
        if (button == GamepadButton.Extra)
        {
            RegisterExtraButton(code);
        }

        SelectedButton = button;
        SelectedExtraCode = code;
    }

    private void RebuildButtonOptions()
    {
        ButtonOptions.Clear();

        foreach (var (name, buttons) in ButtonCategories.Groups)
        {
            ButtonOptions.Add(new ButtonPickerItem { Label = name, IsHeader = true });
            foreach (var button in buttons)
            {
                ButtonOptions.Add(OptionFor(button, 0));
            }
        }

        if (_extraButtonCodes.Count > 0)
        {
            ButtonOptions.Add(new ButtonPickerItem { Label = ButtonCategories.ExtensionGroupName, IsHeader = true });
            foreach (var code in _extraButtonCodes)
            {
                ButtonOptions.Add(OptionFor(GamepadButton.Extra, code));
            }
        }

        SetSelectedButtonOption(CurrentOption());
    }

    private void RebuildKeyOptions()
    {
        KeyOptions.Clear();

        foreach (var (name, keys) in KeyCategories.Groups)
        {
            KeyOptions.Add(new KeyPickerItem { Label = name, IsHeader = true });
            foreach (var key in keys)
            {
                KeyOptions.Add(KeyOptionFor(key));
            }
        }

        var grouped = KeyCategories.Grouped;
        var extended = Enum.GetValues<GamepadKey>().Where(key => !grouped.Contains(key)).ToArray();

        if (extended.Length > 0)
        {
            KeyOptions.Add(new KeyPickerItem { Label = KeyCategories.ExtensionGroupName, IsHeader = true });
            foreach (var key in extended)
            {
                KeyOptions.Add(KeyOptionFor(key));
            }
        }

        RebuildKeyCategoryGroups();
        SetSelectedKeyOption(CurrentKeyOption());
        SetActiveSelectedKeyOption(CurrentActiveKeyOption());
    }

    /// <summary>
    /// Rebuilds the category groups from the flat <see cref="KeyOptions"/> rows. The groups
    /// hold the same item instances the flat list carries, so a selection made through one
    /// is the selection the other reports. The navigator subscribes to each new group, so a
    /// segment's check reaches the editor.
    /// </summary>
    private void RebuildKeyCategoryGroups()
    {
        foreach (var stale in KeyCategoryGroups)
        {
            stale.PropertyChanged -= OnKeyCategoryGroupPropertyChanged;
        }

        KeyCategoryGroups.Clear();

        KeyCategoryGroup? group = null;

        foreach (var item in KeyOptions)
        {
            if (item.IsHeader)
            {
                group = new KeyCategoryGroup(item.Label);
                group.PropertyChanged += OnKeyCategoryGroupPropertyChanged;
                KeyCategoryGroups.Add(group);
            }
            else
            {
                group?.Keys.Add(item);
            }
        }

        // The first category leads until a selection says otherwise; the active option is
        // synced right after this and follows its key into the right group.
        ActiveKeyCategory = KeyCategoryGroups.FirstOrDefault();
    }

    private static KeyPickerItem KeyOptionFor(GamepadKey key)
        => new() { Label = KeyLabel(key), Key = key };

    /// <summary>
    /// What a key is called in the picker. The shared description table names the keys it was
    /// written for; the media and system keys appended after it are named here so the picker
    /// stays Chinese-first rather than showing an enum name. Every other key delegates, so the
    /// two surfaces cannot drift apart on the keys they both cover.
    /// </summary>
    private static string KeyLabel(GamepadKey key) => key switch
    {
        GamepadKey.VolumeUp => "音量加",
        GamepadKey.VolumeDown => "音量减",
        GamepadKey.VolumeMute => "静音",
        GamepadKey.MediaPlayPause => "播放/暂停",
        GamepadKey.MediaStop => "停止播放",
        GamepadKey.MediaPrevTrack => "上一曲",
        GamepadKey.MediaNextTrack => "下一曲",
        GamepadKey.BrowserBack => "浏览器后退",
        GamepadKey.BrowserForward => "浏览器前进",
        GamepadKey.BrowserRefresh => "刷新",
        GamepadKey.BrowserStop => "停止加载",
        GamepadKey.BrowserHome => "浏览器主页",
        GamepadKey.BrowserSearch => "浏览器搜索",
        GamepadKey.BrowserFavorites => "收藏夹",
        GamepadKey.LaunchMail => "打开邮件",
        GamepadKey.LaunchApp1 => "启动应用1",
        GamepadKey.LaunchApp2 => "启动应用2",
        GamepadKey.Sleep => "睡眠",
        GamepadKey.PrintScreen => "截屏",
        _ => MappingDescriptions.Key(key),
    };

    private KeyPickerItem? CurrentKeyOption()
        => KeyOptions.FirstOrDefault(
            option => !option.IsHeader && option.Key == SelectedKey);

    private static ButtonPickerItem OptionFor(GamepadButton button, int code)
        => new() { Label = MappingDescriptions.Button(button, code), Button = button, Code = code };

    private ButtonPickerItem? CurrentOption()
        => ButtonOptions.FirstOrDefault(
            option => !option.IsHeader && option.Button == SelectedButton && option.Code == SelectedExtraCode);

    private void SetSelectedButtonOption(ButtonPickerItem? option)
    {
        if (EqualityComparer<ButtonPickerItem?>.Default.Equals(option, _selectedButtonOption))
        {
            return;
        }

        _selectedButtonOption = option;
        OnPropertyChanged(nameof(SelectedButtonOption));
    }

    private void SetSelectedKeyOption(KeyPickerItem? option)
    {
        if (EqualityComparer<KeyPickerItem?>.Default.Equals(option, _selectedKeyOption))
        {
            return;
        }

        _selectedKeyOption = option;
        OnPropertyChanged(nameof(SelectedKeyOption));
    }

    private static GamepadButton? FirstPressed(GamepadButtonMask mask)
    {
        foreach (var button in ButtonCategories.All)
        {
            if ((mask & button.ToMask()) != 0)
            {
                return button;
            }
        }

        return null;
    }
}
