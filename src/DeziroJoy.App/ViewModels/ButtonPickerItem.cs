using DeziroJoy.Core.Input;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// One row of the categorized button picker: either a category header or a button. Avalonia's
/// ComboBox has no CollectionView grouping, so the categories travel in the item list itself
/// and the item template renders a header row differently from a button row.
/// </summary>
public sealed record ButtonPickerItem
{
    public required string Label { get; init; }

    public bool IsHeader { get; init; }

    public GamepadButton Button { get; init; }

    /// <summary>Raw platform code; only meaningful for <see cref="GamepadButton.Extra"/>.</summary>
    public int Code { get; init; }
}

/// <summary>The category layout of the button picker, in physical order.</summary>
public static class ButtonCategories
{
    public const string ExtensionGroupName = "扩展按键";

    public static readonly (string Name, GamepadButton[] Buttons)[] Groups =
    [
        ("面部按键", [GamepadButton.A, GamepadButton.B, GamepadButton.X, GamepadButton.Y]),
        ("肩键与扳机", [GamepadButton.LeftBumper, GamepadButton.RightBumper, GamepadButton.LeftTrigger, GamepadButton.RightTrigger]),
        ("摇杆", [GamepadButton.LeftStick, GamepadButton.RightStick]),
        ("十字键", [GamepadButton.DPadUp, GamepadButton.DPadDown, GamepadButton.DPadLeft, GamepadButton.DPadRight]),
        ("系统键", [GamepadButton.Back, GamepadButton.Start, GamepadButton.Guide]),
    ];

    /// <summary>Every button with a fixed logical position, in category order.</summary>
    public static readonly GamepadButton[] All = Groups.SelectMany(group => group.Buttons).ToArray();
}
