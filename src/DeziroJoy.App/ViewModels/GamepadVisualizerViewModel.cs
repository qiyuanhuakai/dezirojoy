using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// Live gamepad state for the on-screen controller visualizer.
/// </summary>
public partial class GamepadVisualizerViewModel : ObservableObject
{
    /// <summary>
    /// Raw deflection that arms a stick. Below it the cap is parked at rest; at or above it a
    /// push takes hold. The ramp the armed cap tracks reuses the mapping path's default radial
    /// deadzone (this same 0.15, <see cref="DeziroJoy.Core.Mapping.MouseStickSettings.DeadZone"/>),
    /// so the preview and the mapping agree on where a stick starts to count.
    /// </summary>
    public const float PreviewEnterDeadZone = 0.15f;

    /// <summary>
    /// Raw deflection that releases an armed stick. It sits below
    /// <see cref="PreviewEnterDeadZone"/> so a magnitude hovering on the line - or a stick that
    /// dips back toward centre after a push - stays armed instead of dropping out and re-arming
    /// on the next sample. That gap is what stops the cap flickering at the edge of the deadzone.
    /// </summary>
    public const float PreviewExitDeadZone = 0.10f;

    /// <summary>
    /// Fraction of the way to the ramped target the cap travels per sample while a stick is
    /// armed. A decisive push arms the stick and jumps straight to the target (timely response),
    /// then this short time constant rides out the raw sensor noise while the stick is held and
    /// glides the cap back to centre on release instead of snapping.
    /// </summary>
    public const double PreviewSmoothing = 0.5;

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

    // The stick frame is published as one unit (see PublishSticks): the view redraws every cap
    // layer from a single consistent snapshot. These are plain properties rather than
    // [ObservableProperty] on purpose - the per-axis notifications they used to raise fired up to
    // six times per sample and redrew the caps on half-old, half-new values, which is the layer
    // desync the single-frame notification replaces.
    public double LeftStickX { get; private set; }

    public double LeftStickY { get; private set; }

    public double RightStickX { get; private set; }

    public double RightStickY { get; private set; }

    public double LeftMagnitude { get; private set; }

    public double RightMagnitude { get; private set; }

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

    // Per-stick arm state and smoothed position, carried across samples so the hysteresis and the
    // filter have memory. Both sticks start disarmed and centred.
    private bool _leftArmed;

    private bool _rightArmed;

    private double _leftSmoothX;

    private double _leftSmoothY;

    private double _rightSmoothX;

    private double _rightSmoothY;

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

        AdvanceStick(state.LeftStickX, state.LeftStickY, ref _leftArmed, ref _leftSmoothX, ref _leftSmoothY);
        AdvanceStick(state.RightStickX, state.RightStickY, ref _rightArmed, ref _rightSmoothX, ref _rightSmoothY);

        PublishSticks(_leftSmoothX, _leftSmoothY, _rightSmoothX, _rightSmoothY);
    }

    /// <summary>
    /// Advances one stick's arm state and smoothed position by a single sample. The raw axes pass
    /// through a hysteresis gate (arm at <see cref="PreviewEnterDeadZone"/>, stay armed until the
    /// magnitude falls under <see cref="PreviewExitDeadZone"/>); the armed cap then tracks the
    /// shared radial-deadzone ramp, smoothed so resting noise cannot twitch it and a release
    /// glides it home. Position and magnitude both read the smoothed vector, so the brightness
    /// that rides the magnitude can never flicker out of step with the cap.
    /// </summary>
    private static void AdvanceStick(
        float rawX, float rawY,
        ref bool armed, ref double smoothX, ref double smoothY)
    {
        var magnitude = Math.Sqrt((rawX * rawX) + (rawY * rawY));

        // An exactly-centred reading is mechanical rest - the neutral and disconnected pose. Park
        // the cap instantly rather than gliding, so a dropped connection clears in a single frame
        // and a stick resting dead-centre shows no residual drift.
        if (rawX == 0f && rawY == 0f)
        {
            armed = false;
            smoothX = 0;
            smoothY = 0;
            return;
        }

        // Hysteresis: the threshold a stick is measured against depends on the state it is already
        // in, so a magnitude sitting on the enter line cannot toggle the cap on and off.
        var wasArmed = armed;
        armed = magnitude >= (wasArmed ? PreviewExitDeadZone : PreviewEnterDeadZone);

        var (targetX, targetY) = armed
            ? StickMath.ApplyRadialDeadzone(rawX, rawY, PreviewEnterDeadZone)
            : (0.0, 0.0);

        if (armed && !wasArmed)
        {
            // Arming is a decisive push: jump straight to the ramped deflection so the cap answers
            // on the sample the push lands instead of easing in over several samples.
            smoothX = targetX;
            smoothY = targetY;
        }
        else
        {
            smoothX += (targetX - smoothX) * PreviewSmoothing;
            smoothY += (targetY - smoothY) * PreviewSmoothing;
        }
    }

    /// <summary>
    /// Publishes the smoothed stick frame as a single consistent snapshot and raises one change
    /// notification for the whole frame. The view turns that into exactly one redraw, so every
    /// material layer is placed from the same position and magnitude. An unchanged frame (a stick
    /// parked at rest) raises nothing, which keeps the idle preview off the canvas entirely.
    /// </summary>
    private void PublishSticks(double leftX, double leftY, double rightX, double rightY)
    {
        var leftMagnitude = Math.Sqrt((leftX * leftX) + (leftY * leftY));
        var rightMagnitude = Math.Sqrt((rightX * rightX) + (rightY * rightY));

        if (leftX == LeftStickX && leftY == LeftStickY
            && rightX == RightStickX && rightY == RightStickY
            && leftMagnitude == LeftMagnitude && rightMagnitude == RightMagnitude)
        {
            return;
        }

        LeftStickX = leftX;
        LeftStickY = leftY;
        RightStickX = rightX;
        RightStickY = rightY;
        LeftMagnitude = leftMagnitude;
        RightMagnitude = rightMagnitude;

        // An empty property name is the INotifyPropertyChanged convention for "the object as a
        // whole refreshed"; here it means the stick frame was republished as one unit.
        OnPropertyChanged(string.Empty);
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
