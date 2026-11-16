using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Input;

/// <summary>
/// What one device declares it has, in the Core's logical vocabulary. A platform fills this in
/// from its own capability query - SDL from <c>SDL_GamepadHasButton</c>/<c>SDL_GamepadHasAxis</c>,
/// evdev from <c>EVIOCGBIT</c> - and <see cref="GamepadControlSet.Build"/> turns it into the
/// described controls.
/// </summary>
/// <remarks>
/// A declaration is a claim about a mapping, not an observation: "the mapping says this control
/// exists" is all it means. Whether a physical control is behind it is a separate question the
/// roadmap answers with a display policy, not with this record.
/// </remarks>
public sealed record GamepadControlDeclaration
{
    /// <summary>
    /// Standard buttons the device declares. The trigger bits belong here too: a trigger is a
    /// logical button the mapping engine presses, even though the device declares it as a single
    /// axis. The builder is what decides it is described as an <see cref="GamepadControlKind.Axis"/>.
    /// </summary>
    public GamepadButtonMask Buttons { get; init; } = GamepadButtonMask.None;

    /// <summary>
    /// Sticks the device declares. Both of a stick's axes have to be declared for the stick to
    /// count; a lone X axis is not a stick, and describing it as one would offer a two-axis
    /// editor for a control that has one axis.
    /// </summary>
    public IReadOnlyList<GamepadStick> Sticks { get; init; } = [];

    /// <summary>
    /// Raw codes with no <see cref="GamepadButton"/> position - a misc key, a back paddle, a
    /// touchpad click - in the platform's own numbering, matching what
    /// <see cref="IGamepadState.ExtraButtons"/> reports so a described control is always the one
    /// a mapping binds to. The builder emits them in ascending code order.
    /// </summary>
    public IReadOnlyList<int> ExtraButtons { get; init; } = [];

    /// <summary>
    /// Display label per standard button, when the platform can supply one - SDL's face-button
    /// glyphs, for instance. A missing or blank entry falls back to the logical name in
    /// <see cref="GamepadControlLabels"/>.
    /// </summary>
    public IReadOnlyDictionary<GamepadButton, string> Labels { get; init; }
        = new Dictionary<GamepadButton, string>();
}

/// <summary>
/// Builds the described controls of one device from its declaration.
/// <para>
/// The arrangement is fixed here rather than in any one platform, because it is a panel contract:
/// direction inputs first, then the face diamond, then shoulders and triggers, then function
/// keys, then extensions. A device that declares only a d-pad and two bumpers still lands in the
/// same regions, in the same order, as a full pad - which is what keeps one generic panel usable
/// without a per-model layout.
/// </para>
/// </summary>
public static class GamepadControlSet
{
    /// <summary>Face keys, in the diamond's reading order.</summary>
    private static readonly GamepadButton[] FaceButtons =
        [GamepadButton.A, GamepadButton.B, GamepadButton.X, GamepadButton.Y];

    /// <summary>Shoulder buttons; the triggers follow them in the same zone.</summary>
    private static readonly GamepadButton[] ShoulderButtons =
        [GamepadButton.LeftBumper, GamepadButton.RightBumper];

    /// <summary>Triggers: single-axis controls, described as such even though they press a button.</summary>
    private static readonly GamepadButton[] Triggers =
        [GamepadButton.LeftTrigger, GamepadButton.RightTrigger];

    /// <summary>Function keys: back, start, guide and the two stick clicks.</summary>
    private static readonly GamepadButton[] FunctionButtons =
        [GamepadButton.Back, GamepadButton.Start, GamepadButton.Guide, GamepadButton.LeftStick, GamepadButton.RightStick];

    /// <summary>D-pad directions, in the order the panel reads them.</summary>
    private static readonly GamepadButton[] DPadButtons =
        [GamepadButton.DPadUp, GamepadButton.DPadDown, GamepadButton.DPadLeft, GamepadButton.DPadRight];

    /// <summary>
    /// The described controls of one device, in the fixed zone order. Empty when the device
    /// declares nothing - which is a description the platform has not written yet, not a device
    /// with no controls.
    /// </summary>
    public static IReadOnlyList<GamepadControlDescriptor> Build(GamepadControlDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        var controls = new List<GamepadControlDescriptor>();

        foreach (var stick in OrderedSticks(declaration.Sticks))
        {
            controls.Add(new GamepadControlDescriptor(
                new StickBinding(stick),
                GamepadControlLabels.Stick(stick),
                GamepadControlKind.Direction,
                GamepadControlZone.Directions));
        }

        foreach (var button in DPadButtons)
        {
            if (declaration.Buttons.Has(button))
            {
                controls.Add(Describe(declaration, button, GamepadControlKind.Button, GamepadControlZone.Directions));
            }
        }

        foreach (var button in FaceButtons)
        {
            if (declaration.Buttons.Has(button))
            {
                controls.Add(Describe(declaration, button, GamepadControlKind.Button, GamepadControlZone.MainButtons));
            }
        }

        foreach (var button in ShoulderButtons)
        {
            if (declaration.Buttons.Has(button))
            {
                controls.Add(Describe(declaration, button, GamepadControlKind.Button, GamepadControlZone.Shoulders));
            }
        }

        foreach (var button in Triggers)
        {
            if (declaration.Buttons.Has(button))
            {
                controls.Add(Describe(declaration, button, GamepadControlKind.Axis, GamepadControlZone.Shoulders));
            }
        }

        foreach (var button in FunctionButtons)
        {
            if (declaration.Buttons.Has(button))
            {
                controls.Add(Describe(declaration, button, GamepadControlKind.Button, GamepadControlZone.Functions));
            }
        }

        foreach (var code in declaration.ExtraButtons.Distinct().OrderBy(code => code))
        {
            controls.Add(new GamepadControlDescriptor(
                new ButtonBinding(GamepadButton.Extra, code),
                GamepadControlLabels.Generic(code),
                GamepadControlKind.Other,
                GamepadControlZone.Extensions));
        }

        return controls;
    }

    /// <summary>
    /// Groups the described controls by zone while keeping the build order inside each zone, so
    /// a panel that renders regions in <see cref="GamepadControlZone"/> order gets a stable
    /// arrangement without a second ordering rule to keep in step.
    /// </summary>
    public static IReadOnlyList<GamepadControlZone> ZoneOrder { get; } =
        [
            GamepadControlZone.Directions,
            GamepadControlZone.MainButtons,
            GamepadControlZone.Shoulders,
            GamepadControlZone.Functions,
            GamepadControlZone.Extensions,
        ];

    private static GamepadControlDescriptor Describe(
        GamepadControlDeclaration declaration,
        GamepadButton button,
        GamepadControlKind kind,
        GamepadControlZone zone)
        => new(new ButtonBinding(button), LabelOf(declaration, button), kind, zone);

    private static string LabelOf(GamepadControlDeclaration declaration, GamepadButton button)
        => declaration.Labels.TryGetValue(button, out var label) && !string.IsNullOrWhiteSpace(label)
            ? label
            : GamepadControlLabels.Button(button);

    /// <summary>
    /// Left stick before right stick, each at most once, whatever order the declaration listed
    /// them in - the arrangement is fixed, so the panel does not depend on a platform's
    /// enumeration order.
    /// </summary>
    private static IEnumerable<GamepadStick> OrderedSticks(IReadOnlyList<GamepadStick> sticks)
        => sticks.Distinct().OrderBy(stick => stick == GamepadStick.Left ? 0 : 1);
}
