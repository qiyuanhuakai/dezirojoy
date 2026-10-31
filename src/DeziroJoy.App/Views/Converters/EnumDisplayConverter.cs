using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Views.Converters;

/// <summary>Chinese label for every enum shown in an editor combo box.</summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            GamepadButton button => MappingDescriptions.Button(button),
            GamepadStick stick => MappingDescriptions.Stick(stick),
            GamepadKey key => MappingDescriptions.Key(key),
            MouseButton mouse => mouse switch
            {
                MouseButton.Left => "鼠标左键",
                MouseButton.Right => "鼠标右键",
                MouseButton.Middle => "鼠标中键",
                MouseButton.XButton1 => "鼠标侧键1",
                MouseButton.XButton2 => "鼠标侧键2",
                _ => mouse.ToString(),
            },
            MappingTargetKind kind => kind switch
            {
                MappingTargetKind.KeyPress => "单键",
                MappingTargetKind.KeyCombo => "组合键",
                MappingTargetKind.MouseButton => "鼠标按键",
                MappingTargetKind.MouseMove => "鼠标移动",
                MappingTargetKind.WheelScroll => "滚轮滚动",
                _ => kind.ToString(),
            },
            MappingSourceKind source => source switch
            {
                MappingSourceKind.Button => "按键",
                MappingSourceKind.Stick => "摇杆",
                _ => source.ToString(),
            },
            ResponseCurve curve => curve switch
            {
                ResponseCurve.Linear => "线性",
                ResponseCurve.Precise => "精准",
                ResponseCurve.Aggressive => "激进",
                ResponseCurve.Responsive => "灵敏",
                _ => curve.ToString(),
            },
            _ => value,
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
