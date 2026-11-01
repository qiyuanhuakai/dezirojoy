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
/// </remarks>
public sealed record ButtonGesture
{
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

    /// <summary>True when a second press inside the window fires the double-click.</summary>
    [JsonIgnore]
    public bool HasDoubleClick => DoubleClickWindow > 0f;

    /// <summary>True when holding past the threshold fires the long-press.</summary>
    [JsonIgnore]
    public bool HasLongPress => LongPressThreshold > 0f;

    /// <summary>True when any gesture is configured, i.e. when a wait must be introduced.</summary>
    [JsonIgnore]
    public bool HasAnyGesture => HasDoubleClick || HasLongPress;
}
