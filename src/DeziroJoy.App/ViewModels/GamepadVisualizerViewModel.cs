using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeziroJoy.Core.Input;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// Live gamepad state for the on-screen controller visualizer.
/// </summary>
public partial class GamepadVisualizerViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isAPressed;

    [ObservableProperty]
    private bool _isBPressed;

    [ObservableProperty]
    private bool _isXPressed;

    [ObservableProperty]
    private bool _isYPressed;

    [ObservableProperty]
    private bool _isLeftBumperPressed;

    [ObservableProperty]
    private bool _isRightBumperPressed;

    [ObservableProperty]
    private bool _isLeftTriggerPressed;

    [ObservableProperty]
    private bool _isRightTriggerPressed;

    [ObservableProperty]
    private bool _isBackPressed;

    [ObservableProperty]
    private bool _isStartPressed;

    [ObservableProperty]
    private bool _isGuidePressed;

    [ObservableProperty]
    private bool _isLeftStickPressed;

    [ObservableProperty]
    private bool _isRightStickPressed;

    [ObservableProperty]
    private bool _isDPadUpPressed;

    [ObservableProperty]
    private bool _isDPadDownPressed;

    [ObservableProperty]
    private bool _isDPadLeftPressed;

    [ObservableProperty]
    private bool _isDPadRightPressed;

    [ObservableProperty]
    private double _leftStickX;

    [ObservableProperty]
    private double _leftStickY;

    [ObservableProperty]
    private double _rightStickX;

    [ObservableProperty]
    private double _rightStickY;

    [ObservableProperty]
    private double _leftMagnitude;

    [ObservableProperty]
    private double _rightMagnitude;

    /// <summary>
    /// Number of buttons the connected device declares, or null when it does not say. XInput's
    /// 14-button set is fixed and closed so Windows always reports null; evdev fills it in from
    /// EVIOCGBIT(EV_KEY). The UI shows "unknown" rather than guessing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonCountText))]
    private int? _buttonCount;

    /// <summary>What the 手柄状态 card shows next to the device name.</summary>
    public string ButtonCountText => ButtonCount is { } count ? $"检测到 {count} 个按键" : "按键数未知";

    /// <summary>
    /// Raw platform codes of unmapped buttons the device has reported. They are drawn on the
    /// model as "按键 N" pills so they are as clickable as any fixed element.
    /// </summary>
    public ObservableCollection<int> ExtraButtonCodes { get; } = [];

    public void Update(IGamepadState state)
    {
        var buttons = state.PressedButtons;

        IsAPressed = buttons.Has(GamepadButton.A);
        IsBPressed = buttons.Has(GamepadButton.B);
        IsXPressed = buttons.Has(GamepadButton.X);
        IsYPressed = buttons.Has(GamepadButton.Y);
        IsLeftBumperPressed = buttons.Has(GamepadButton.LeftBumper);
        IsRightBumperPressed = buttons.Has(GamepadButton.RightBumper);
        IsLeftTriggerPressed = buttons.Has(GamepadButton.LeftTrigger);
        IsRightTriggerPressed = buttons.Has(GamepadButton.RightTrigger);
        IsBackPressed = buttons.Has(GamepadButton.Back);
        IsStartPressed = buttons.Has(GamepadButton.Start);
        IsGuidePressed = buttons.Has(GamepadButton.Guide);
        IsLeftStickPressed = buttons.Has(GamepadButton.LeftStick);
        IsRightStickPressed = buttons.Has(GamepadButton.RightStick);
        IsDPadUpPressed = buttons.Has(GamepadButton.DPadUp);
        IsDPadDownPressed = buttons.Has(GamepadButton.DPadDown);
        IsDPadLeftPressed = buttons.Has(GamepadButton.DPadLeft);
        IsDPadRightPressed = buttons.Has(GamepadButton.DPadRight);

        foreach (var code in state.ExtraButtons)
        {
            RegisterExtraButton(code);
        }

        LeftStickX = state.LeftStickX;
        LeftStickY = state.LeftStickY;
        RightStickX = state.RightStickX;
        RightStickY = state.RightStickY;
        LeftMagnitude = Math.Sqrt((state.LeftStickX * state.LeftStickX) + (state.LeftStickY * state.LeftStickY));
        RightMagnitude = Math.Sqrt((state.RightStickX * state.RightStickX) + (state.RightStickY * state.RightStickY));
    }

    /// <summary>
    /// Adds a raw platform code to the model's extension row. Codes describe the connected
    /// device rather than one mapping, so they survive <see cref="SetConnected"/>.
    /// </summary>
    public void RegisterExtraButton(int code)
    {
        if (code <= 0 || ExtraButtonCodes.Contains(code))
        {
            return;
        }

        ExtraButtonCodes.Add(code);
    }

    /// <summary>
    /// Points the model at the connected device so the pad's real button count can be shown.
    /// A null descriptor means no device, which reports the count as unknown.
    /// </summary>
    public void SetDevice(GamepadDescriptor? descriptor)
        => ButtonCount = descriptor?.ButtonCount;

    public void SetConnected(bool connected)
    {
        IsConnected = connected;

        if (connected)
        {
            return;
        }

        // The count describes a device, so it cannot outlive the connection.
        ButtonCount = null;
        Update(GamepadState.Neutral);
    }
}
