using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Input;

/// <summary>
/// What kind of input a described control is. This decides which editor fits it, and nothing
/// else: a <see cref="Direction"/> is edited as a two-axis control, an <see cref="Axis"/> as a
/// single-axis one, and <see cref="Other"/> has no standard editor shape at all.
/// </summary>
public enum GamepadControlKind
{
    /// <summary>
    /// A binary control: a face key, a d-pad direction, a shoulder, a function key, a stick
    /// click. It has a pressed and a released state and nothing between.
    /// </summary>
    Button,

    /// <summary>
    /// A two-axis control: an analog stick. It is edited as one control with two axes, not as
    /// two separate single-axis controls.
    /// </summary>
    Direction,

    /// <summary>A single-axis control: a trigger. Edited as one axis with a travel range.</summary>
    Axis,

    /// <summary>
    /// A control with no standard name or editor shape: a misc key, a back paddle, a touchpad
    /// click. It stays bindable through its raw code; it just has no standard widget.
    /// </summary>
    Other,
}

/// <summary>
/// The fixed region of the control panel a described control belongs to. The roadmap fixes the
/// arrangement - direction inputs on the left, main buttons on the right, shoulders and
/// triggers on top, function and extension keys on their own - so a device never has to be
/// drawn per model for its controls to land somewhere predictable.
/// </summary>
public enum GamepadControlZone
{
    /// <summary>Direction inputs: the sticks and the d-pad.</summary>
    Directions,

    /// <summary>The face-button diamond.</summary>
    MainButtons,

    /// <summary>Shoulders and triggers.</summary>
    Shoulders,

    /// <summary>Back / start / guide and the stick clicks.</summary>
    Functions,

    /// <summary>Everything with no standard position: misc keys, paddles, touchpad.</summary>
    Extensions,
}

/// <summary>
/// One described control: what it binds to, what it is called, and where it belongs.
/// <para>
/// This is a *description*, not a reading. It carries no pressed state, no "has this ever been
/// seen" flag and no live value - <see cref="IGamepadState"/> is the only place input lives.
/// A platform's <c>HasButton</c>/<c>HasAxis</c> answer is a claim about what the mapping
/// declares, which is not the same as a physical control existing (the roadmap's U2 rule), and
/// a description that smuggled in observed state would silently turn one into the other.
/// </para>
/// <para>
/// The descriptor is deliberately free of any device knowledge: the same record describes an
/// Xbox pad, a DualSense and an unknown third-party pad, which is what lets one generic panel
/// serve all of them without a model database.
/// </para>
/// </summary>
/// <param name="Source">
/// What a mapping for this control binds to. It is the same <see cref="InputBinding"/> the
/// mapping engine keys on, so a described control is always bindable and a bound control is
/// always described.
/// </param>
/// <param name="Label">What the panel calls it. See <see cref="GamepadControlLabels"/>.</param>
/// <param name="Kind">What kind of input it is.</param>
/// <param name="Zone">Which fixed region of the panel it belongs to.</param>
public sealed record GamepadControlDescriptor(
    InputBinding Source,
    string Label,
    GamepadControlKind Kind,
    GamepadControlZone Zone);
