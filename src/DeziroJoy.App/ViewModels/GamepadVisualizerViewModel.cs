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

        LeftStickX = state.LeftStickX;
        LeftStickY = state.LeftStickY;
        RightStickX = state.RightStickX;
        RightStickY = state.RightStickY;
        LeftMagnitude = Math.Sqrt((state.LeftStickX * state.LeftStickX) + (state.LeftStickY * state.LeftStickY));
        RightMagnitude = Math.Sqrt((state.RightStickX * state.RightStickX) + (state.RightStickY * state.RightStickY));
    }

    public void SetConnected(bool connected)
    {
        IsConnected = connected;

        if (connected)
        {
            return;
        }

        Update(GamepadState.Neutral);
    }
}
