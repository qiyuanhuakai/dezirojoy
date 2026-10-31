using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// One row of the categorized target-key picker: either a category header or a key. Mirrors
/// <see cref="ButtonPickerItem"/> so the same item-template trick renders both lists.
/// </summary>
/// <remarks>
/// The categories are the gamepad's own groups rather than a keyboard layout: the user is
/// holding a gamepad, so "面部按键" (A/B/X/Y) and "十字键" (the arrow keys) are the names they
/// already know. A key that fits no group lands in 扩展按键, which is where every function key,
/// digit and symbol lives.
/// </remarks>
public sealed record KeyPickerItem
{
    public required string Label { get; init; }

    public bool IsHeader { get; init; }

    public GamepadKey Key { get; init; }
}

/// <summary>The category layout of the target-key picker, in the order the list shows them.</summary>
public static class KeyCategories
{
    public const string ExtensionGroupName = "扩展按键";

    public static readonly (string Name, GamepadKey[] Keys)[] Groups =
    [
        ("面部按键", [GamepadKey.A, GamepadKey.B, GamepadKey.X, GamepadKey.Y]),
        ("肩键与扳机", [GamepadKey.LeftCtrl, GamepadKey.LeftShift, GamepadKey.LeftAlt, GamepadKey.LeftWin]),
        ("摇杆", [GamepadKey.ArrowUp, GamepadKey.ArrowDown, GamepadKey.ArrowLeft, GamepadKey.ArrowRight]),
        ("十字键", [GamepadKey.Numpad0, GamepadKey.Numpad1, GamepadKey.Numpad2, GamepadKey.Numpad3,
            GamepadKey.Numpad4, GamepadKey.Numpad5, GamepadKey.Numpad6, GamepadKey.Numpad7,
            GamepadKey.Numpad8, GamepadKey.Numpad9]),
        ("系统键", [GamepadKey.Escape, GamepadKey.Enter, GamepadKey.Space, GamepadKey.Tab,
            GamepadKey.Backspace, GamepadKey.Delete, GamepadKey.Insert, GamepadKey.Home,
            GamepadKey.End, GamepadKey.PageUp, GamepadKey.PageDown]),
    ];

    /// <summary>Every key with a fixed group, in category order.</summary>
    public static readonly GamepadKey[] Grouped = Groups.SelectMany(group => group.Keys).ToArray();

    /// <summary>
    /// Every key the picker offers: the grouped ones in category order, then everything else
    /// (function keys, digits, letters, symbols) under 扩展按键.
    /// </summary>
    public static readonly GamepadKey[] All =
    [
        .. Grouped,
        .. Enum.GetValues<GamepadKey>().Where(key => !Grouped.Contains(key)),
    ];
}
