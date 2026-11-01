using System.Text.Json.Serialization;

namespace DeziroJoy.Core.Mapping;

/// <summary>
/// Optional per-button gesture configuration (双击 / 长按). A button whose
/// <see cref="ButtonBinding.Gesture"/> is null - or holds nothing enabled - fires on the
/// press edge with zero added latency; enabling 双击 or 长按 introduces a wait, because the
/// single action cannot fire until it is known which gesture resolves.
/// </summary>
/// <remarks>
/// Every threshold defaults to 0, which disables that gesture, so <c>new ButtonGesture()</c>
/// behaves exactly like no gesture at all and keeps the zero-latency single-click path.
/// <para>
/// The two action fields are additive: they say what a gesture emits, not whether it is enabled.
/// Left null a gesture re-fires the mapping's own target, which is the behaviour every profile
/// written before they existed already has, so an old profile loads and runs unchanged.
/// </para>
/// </remarks>
public sealed record ButtonGesture
{
    /// <summary>Fixed default double-click window, in seconds.</summary>
    public const float DefaultDoubleClickWindow = 0.25f;

    /// <summary>Fixed default long-press threshold, in seconds.</summary>
    public const float DefaultLongPressThreshold = 0.50f;

    /// <summary>Fixed default click auto-repeat interval, in seconds.</summary>
    public const float DefaultClickRepeatInterval = 0.10f;

    /// <summary>
    /// Seconds within which a second press counts as a double-click. 0 = the double-click
    /// gesture is disabled and no double-click wait is introduced.
    /// </summary>
    public float DoubleClickWindow { get; init; }

    /// <summary>
    /// Seconds the button must be held before the long-press gesture fires. 0 = disabled.
    /// </summary>
    public float LongPressThreshold { get; init; }

    /// <summary>
    /// Seconds between auto-repeat activations once the long-press has fired
    /// ("长按时连续触发"). 0 = the long-press fires exactly once.
    /// </summary>
    public float LongPressRepeatInterval { get; init; }

    /// <summary>Seconds between activations while the button is held ("长按重复触发"). 0 = the click fires once.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float ClickRepeatInterval { get; init; }

    /// <summary>
    /// What the double-click emits. Null - the default - leaves the double-click without a
    /// function of its own: it re-fires the mapping's own <see cref="MappingEntry.Target"/>,
    /// which is exactly how the gesture behaved before this field existed.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MappingTarget? DoubleClickAction { get; init; }

    /// <summary>
    /// What the long-press emits, and what auto-repeat re-fires. Null - the default - leaves
    /// the long-press without a function of its own: it re-fires the mapping's own
    /// <see cref="MappingEntry.Target"/>.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MappingTarget? LongPressAction { get; init; }

    /// <summary>True when a second press inside the window fires the double-click.</summary>
    [JsonIgnore]
    public bool HasDoubleClick => DoubleClickWindow > 0f;

    /// <summary>True when holding past the threshold fires the long-press.</summary>
    [JsonIgnore]
    public bool HasLongPress => LongPressThreshold > 0f;

    /// <summary>True when holding the button repeats the click.</summary>
    [JsonIgnore]
    public bool HasClickRepeat => ClickRepeatInterval > 0f;

    /// <summary>
    /// The long-press as it actually behaves: inert while click auto-repeat is enabled.
    /// Click auto-repeat and the long-press are mutually exclusive (they must not be merged);
    /// when both are configured the click auto-repeat wins and the long-press does nothing.
    /// </summary>
    [JsonIgnore]
    public bool HasEffectiveLongPress => HasLongPress && !HasClickRepeat;

    /// <summary>True when any gesture is configured, i.e. when a wait must be introduced.</summary>
    [JsonIgnore]
    public bool HasAnyGesture => HasDoubleClick || HasEffectiveLongPress || HasClickRepeat;

    /// <summary>True when the double-click has a function of its own.</summary>
    [JsonIgnore]
    public bool HasDoubleClickAction => DoubleClickAction is not null;

    /// <summary>True when the long-press has a function of its own.</summary>
    [JsonIgnore]
    public bool HasLongPressAction => LongPressAction is not null;

    /// <summary>
    /// True when the long-press carries a real function of its own (not 无功能). A long-press
    /// whose action is a <see cref="NoneTarget"/> does not disambiguate a second function, so
    /// it must not consume the press the way a real action does.
    /// </summary>
    [JsonIgnore]
    public bool HasLongPressFunction => LongPressAction is not null and not NoneTarget;
}
