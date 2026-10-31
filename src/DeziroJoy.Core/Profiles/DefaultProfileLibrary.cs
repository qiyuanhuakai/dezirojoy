using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Profiles;

/// <summary>
/// Built-in default profiles per gamepad layout family. Button semantics follow physical
/// positions (A = bottom, B = right, X = left, Y = top), so the same profile serves Xbox
/// and DualShock pads once platform layers normalize their raw codes.
/// </summary>
public static class DefaultProfileLibrary
{
    public static GamepadProfile CreateDefault(GamepadDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return CreateForLayout(descriptor.Layout) with { DeviceId = descriptor.DeviceId };
    }

    public static GamepadProfile CreateForLayout(GamepadLayout layout) => new()
    {
        Name = LayoutName(layout),
        Layout = layout,
        Mappings =
        [
            // 左摇杆 -> 滚轮
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Left),
                Target = new WheelScrollTarget(new ScrollStickSettings()),
            },
            // 右摇杆 -> 鼠标
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Right),
                Target = new MouseMoveTarget(new MouseStickSettings()),
            },
            // 十字键 -> 方向键
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.DPadUp),
                Target = new KeyPressTarget(GamepadKey.ArrowUp),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.DPadDown),
                Target = new KeyPressTarget(GamepadKey.ArrowDown),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.DPadLeft),
                Target = new KeyPressTarget(GamepadKey.ArrowLeft),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.DPadRight),
                Target = new KeyPressTarget(GamepadKey.ArrowRight),
            },
            // 功能键
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.B),
                Target = new KeyPressTarget(GamepadKey.Enter),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.A),
                Target = new KeyPressTarget(GamepadKey.Escape),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.X),
                Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Y),
                Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.V),
            },
        ],
    };

    private static string LayoutName(GamepadLayout layout) => layout switch
    {
        GamepadLayout.Xbox => "Xbox 布局默认配置",
        GamepadLayout.DualShock => "DualShock 布局默认配置",
        _ => "通用默认配置",
    };
}
