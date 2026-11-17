using DeziroJoy.Core.Input;
using SDL3;

namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// One device's face-button mapping: which logical <see cref="GamepadButton"/> each of SDL's
/// four face positions is.
/// <para>
/// SDL's south/east/west/north name the four *positions* in the diamond, and SDL's own gamepad
/// API is positional throughout - but the glyph actually printed on the device is not the same
/// letter at the same position on every pad. An Xbox pad and a DualSense both put "bottom = A /
/// Cross" in the south slot; a Nintendo pad puts B there, and a GameCube pad puts A in the south
/// slot, X in the east slot and B in the west slot. Mapping the position to a fixed logical
/// button therefore makes "the button labelled X" execute a different logical input depending on
/// the pad - on a Nintendo pad the physical X sits in the north slot, so it executed logical Y
/// while the panel called it X.
/// </para>
/// <para>
/// So the mapping is read from the device: SDL reports the glyph at each position
/// (<c>SDL_GetGamepadButtonLabel</c>, recorded in <see cref="Sdl3GamepadInfo.FaceButtonLabels"/>
/// in south/east/west/north order), and the glyph decides the logical button. A device SDL cannot
/// read a glyph from falls back to the positional table, which is the only answer left and the
/// one every layout shares.
/// </para>
/// <para>
/// The tracker and the control description are both built from this one map, so a control the
/// panel labels "X" is bound to the input that pressing the physical X produces. Fixing only the
/// label would leave the mapping executing a different button than the one it names.
/// </para>
/// </summary>
public sealed record Sdl3FaceButtonMap
{
    private Sdl3FaceButtonMap()
    {
    }

    /// <summary>The logical button in SDL's south (bottom) slot.</summary>
    public required GamepadButton South { get; init; }

    /// <summary>The logical button in SDL's east (right) slot.</summary>
    public required GamepadButton East { get; init; }

    /// <summary>The logical button in SDL's west (left) slot.</summary>
    public required GamepadButton West { get; init; }

    /// <summary>The logical button in SDL's north (top) slot.</summary>
    public required GamepadButton North { get; init; }

    /// <summary>
    /// The positional table: south = A, east = B, west = X, north = Y - the Xbox convention,
    /// which is also the only interpretation every layout SDL recognizes shares.
    /// </summary>
    public static Sdl3FaceButtonMap Positional { get; } = new()
    {
        South = GamepadButton.A,
        East = GamepadButton.B,
        West = GamepadButton.X,
        North = GamepadButton.Y,
    };

    /// <summary>
    /// The logical button one standard SDL button is on this device. The four face positions
    /// follow the device's own glyphs; every other standard button has no glyph to read and
    /// keeps the positional table.
    /// </summary>
    public bool TryLogical(SDL.GamepadButton button, out GamepadButton logical)
    {
        var face = button switch
        {
            SDL.GamepadButton.South => South,
            SDL.GamepadButton.East => East,
            SDL.GamepadButton.West => West,
            SDL.GamepadButton.North => North,
            _ => (GamepadButton?)null,
        };

        if (face is { } mapped)
        {
            logical = mapped;
            return true;
        }

        return Sdl3ButtonMap.TryToLogical(button, out logical);
    }

    /// <summary>
    /// Builds the map from the glyphs SDL read off one device.
    /// <para>
    /// A position whose glyph SDL could not read - or whose glyph this build does not recognize -
    /// takes the positional answer for that slot, so one unreadable label costs one position
    /// rather than the whole device. If the result is not four distinct buttons the device is
    /// reporting something unusable, and the whole map falls back to the positional table: a
    /// mapping that collapses two positions onto one logical button would make the other one
    /// unbindable, and silently losing a button is worse than the positional guess.
    /// </para>
    /// </summary>
    public static Sdl3FaceButtonMap From(Sdl3GamepadInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var labels = info.FaceButtonLabels;

        var map = new Sdl3FaceButtonMap
        {
            South = LogicalFor(labels, 0, GamepadButton.A),
            East = LogicalFor(labels, 1, GamepadButton.B),
            West = LogicalFor(labels, 2, GamepadButton.X),
            North = LogicalFor(labels, 3, GamepadButton.Y),
        };

        return map.IsDegenerate ? Positional : map;
    }

    /// <summary>True when two positions landed on the same logical button.</summary>
    private bool IsDegenerate => new[] { South, East, West, North }.Distinct().Count() != 4;

    private static GamepadButton LogicalFor(IReadOnlyList<string> labels, int index, GamepadButton positional)
    {
        if (index >= labels.Count)
        {
            return positional;
        }

        var glyph = labels[index];
        if (string.IsNullOrWhiteSpace(glyph))
        {
            return positional;
        }

        return Glyphs.TryGetValue(glyph.Trim(), out var logical) ? logical : positional;
    }

    /// <summary>
    /// The glyph SDL reports at each face position, and the logical button it means. The four
    /// letters are the Xbox convention; the four PlayStation glyphs land on the same logical
    /// buttons because they sit in the same positions; the Nintendo and GameCube spellings land
    /// elsewhere, which is the whole reason this table exists.
    /// </summary>
    private static readonly Dictionary<string, GamepadButton> Glyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = GamepadButton.A,
        ["B"] = GamepadButton.B,
        ["X"] = GamepadButton.X,
        ["Y"] = GamepadButton.Y,
        ["Cross"] = GamepadButton.A,
        ["Circle"] = GamepadButton.B,
        ["Square"] = GamepadButton.X,
        ["Triangle"] = GamepadButton.Y,
    };
}
