using DeziroJoy.Core.Input;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// One row of the direction picker. A direction is only meaningful for the two cross
/// combinations the engine rejects without one: a stick driving a digital target (key / combo /
/// mouse button) and a button driving a continuous one (mouse move / wheel).
/// </summary>
public sealed record DirectionPickerItem
{
    public required string Label { get; init; }

    public InputDirection Direction { get; init; }
}

/// <summary>The four cardinal directions, in physical order.</summary>
public static class DirectionCategories
{
    public static readonly DirectionPickerItem[] All =
    [
        new() { Label = "上", Direction = InputDirection.Up },
        new() { Label = "下", Direction = InputDirection.Down },
        new() { Label = "左", Direction = InputDirection.Left },
        new() { Label = "右", Direction = InputDirection.Right },
    ];

    public static string Label(InputDirection direction) => direction switch
    {
        InputDirection.Up => "上",
        InputDirection.Down => "下",
        InputDirection.Left => "左",
        InputDirection.Right => "右",
        _ => direction.ToString(),
    };
}
