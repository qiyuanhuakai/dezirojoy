using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Views;

/// <summary>
/// Static option lists for the editor combo boxes. Kept as fields so XAML can reach them
/// through <c>x:Static</c> without a view-model round trip. The button picker is the
/// exception: it is categorized and grows with the device's unmapped buttons, so it lives on
/// <see cref="MappingEditorViewModel.ButtonOptions"/>.
/// </summary>
public static class EnumOptions
{
    public static readonly MappingSourceKind[] SourceKinds = Enum.GetValues<MappingSourceKind>();

    public static readonly GamepadStick[] Sticks = Enum.GetValues<GamepadStick>();

    public static readonly MappingTargetKind[] TargetKinds = Enum.GetValues<MappingTargetKind>();

    public static readonly GamepadKey[] Keys = Enum.GetValues<GamepadKey>();

    public static readonly MouseButton[] MouseButtons = Enum.GetValues<MouseButton>();

    public static readonly ResponseCurve[] Curves = Enum.GetValues<ResponseCurve>();
}
