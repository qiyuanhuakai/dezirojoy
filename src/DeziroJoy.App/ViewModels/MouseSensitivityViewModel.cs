using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

public readonly record struct CurvePoint(double X, double Y);

public sealed record PrecisionModeOption(PrecisionMode Value, string Label);

/// <summary>
/// Live calibration panel for stick-to-mouse. Drives the real
/// <see cref="MouseStickProcessor"/> so the preview, the speed readout and the response graph
/// all reflect the actual engine behavior. Two input sources feed it: the scripted sweep,
/// which needs no gamepad and is the default, and real host samples - the same
/// <see cref="IGamepadState"/> stream the visualizer reads - which show the raw stick, the
/// deadzone-filtered deflection and the final speed, measure the at-rest drift distribution
/// for a confirmable deadzone suggestion, and split the daily and precision speed readouts so
/// the two can be tuned separately.
/// </summary>
public partial class MouseSensitivityViewModel : ObservableObject
{
    public const double PreviewCenter = 100.0;

    public const double PreviewSize = 200.0;

    private const double PixelsToPreview = 0.05;

    private const float SweepPeriodSeconds = 4f;

    private const float SweepRadius = 0.8f;

    private const double RestGlideSpeed = 400.0;

    /// <summary>Samples below this count are too few to call an at-rest drift distribution.</summary>
    public const int MinDriftSamples = 20;

    /// <summary>
    /// Headroom the suggested deadzone keeps over the observed drift peak: a pad that only
    /// just clears its own noise still gives up a sliver of the center, which is the trade
    /// the suggestion makes so a resting stick never walks the cursor.
    /// </summary>
    private const double DriftSafetyFactor = 1.25;

    /// <summary>The suggestion is rounded up to this step, the deadzone slider's own tick.</summary>
    private const double DriftSuggestionStep = 0.01;

    /// <summary>The deadzone slider's maximum; a suggestion must stay reachable through it.</summary>
    private const double MaxSuggestedDeadZone = 0.6;

    private MouseStickProcessor _processor = new(new MouseStickSettings());
    private float _sweepSeconds;
    private float _realStickX;
    private float _realStickY;
    private double _driftMagnitudeSum;

    public MouseSensitivityViewModel()
    {
        RebuildCurvePoints();
    }

    [ObservableProperty]
    private float _maxSpeed = 1600f;

    [ObservableProperty]
    private float _deadZone = 0.15f;

    [ObservableProperty]
    private ResponseCurve _curve = ResponseCurve.Precise;

    [ObservableProperty]
    private float _curveStrength = 1f;

    [ObservableProperty]
    private float _responseTime = 0.08f;

    [ObservableProperty]
    private float _precisionScale = 0.35f;

    [ObservableProperty]
    private PrecisionMode _precisionMode = PrecisionMode.Hold;

    /// <summary>
    /// The precision mode as the panel reports it. The trigger method is chosen on the
    /// mapping surface because it belongs to the mapping, so this is a read-only label
    /// over <see cref="PrecisionMode"/> rather than a selection the panel can change. The
    /// labels match the mapping surface's precision-mode picker, so the two surfaces never
    /// name the same mode differently.
    /// </summary>
    public string PrecisionModeLabel => PrecisionMode switch
    {
        PrecisionMode.Hold => "一直按住才触发",
        PrecisionMode.Toggle => "按一下启用，再按一下取消",
        _ => PrecisionMode.ToString(),
    };

    [ObservableProperty]
    private bool _invertY = true;

    [ObservableProperty]
    private float _scaleX = 1f;

    [ObservableProperty]
    private float _scaleY = 1f;

    /// <summary>
    /// Button that temporarily slows the stick for fine aiming. Per-mapping and profile-stored,
    /// so the panel has to carry it both ways: dropping it here silently reset every custom
    /// precision key back to the default whenever sensitivity settings were applied.
    /// </summary>
    [ObservableProperty]
    private GamepadButton _precisionButton = GamepadButton.LeftStick;

    /// <summary>
    /// The precision key as the panel reports it. The key is chosen on the mapping surface
    /// because it belongs to the mapping, so this is a read-only label over
    /// <see cref="PrecisionButton"/> rather than a selection the panel can change.
    /// </summary>
    public string PrecisionButtonLabel => MappingDescriptions.Button(PrecisionButton);

    [ObservableProperty]
    private double _previewX = PreviewCenter;

    [ObservableProperty]
    private double _previewY = PreviewCenter;

    [ObservableProperty]
    private double _currentSpeed;

    [ObservableProperty]
    private double _travelDistance;

    /// <summary>
    /// Whether the preview reads real host samples instead of the scripted sweep. The sweep
    /// is the default because it needs no gamepad; the calibration switches this on to
    /// measure the pad the player actually holds.
    /// </summary>
    [ObservableProperty]
    private bool _isRealInputMode;

    /// <summary>
    /// Feeds one real host sample into the calibration, exactly the way
    /// <see cref="GamepadVisualizerViewModel.Update"/> receives them: called on the UI thread
    /// for every sample, latest sample wins between preview ticks. Only the right stick - the
    /// stick the mouse mapping reads - and the precision button are consumed.
    /// </summary>
    public void ApplySample(IGamepadState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _realStickX = state.RightStickX;
        _realStickY = state.RightStickY;

        // A hand-edited profile could name Extra as the precision key; it carries no mask
        // bit, so asking the mask for it would throw on every single sample.
        PrecisionActive = PrecisionButton != GamepadButton.Extra && state.PressedButtons.Has(PrecisionButton);

        if (IsMeasuringDrift)
        {
            RecordDriftSample(Math.Sqrt((_realStickX * _realStickX) + (_realStickY * _realStickY)));
        }

        if (IsRealInputMode)
        {
            RefreshRealInputReadouts();
        }
    }

    /// <summary>Stage 1 of the calibration readout: the raw stick, as the pad reported it.</summary>
    [ObservableProperty]
    private double _rawStickX;

    [ObservableProperty]
    private double _rawStickY;

    [ObservableProperty]
    private double _rawMagnitude;

    /// <summary>Stage 2: the deadzone-filtered deflection the response curve actually sees.</summary>
    [ObservableProperty]
    private double _filteredStickX;

    [ObservableProperty]
    private double _filteredStickY;

    [ObservableProperty]
    private double _filteredMagnitude;

    /// <summary>Target speed in px/s at the current deflection with the precision key up.</summary>
    [ObservableProperty]
    private double _dailySpeed;

    /// <summary>Target speed in px/s at the current deflection with the precision key held.</summary>
    [ObservableProperty]
    private double _precisionSpeed;

    /// <summary>Whether the precision key is down in the latest real sample.</summary>
    [ObservableProperty]
    private bool _precisionActive;

    [ObservableProperty]
    private bool _isMeasuringDrift;

    public int DriftSampleCount { get; private set; }

    public double DriftMeanMagnitude { get; private set; }

    public double DriftMaxMagnitude { get; private set; }

    /// <summary>
    /// Deadzone the at-rest drift distribution suggests, or null while there is not enough
    /// data to suggest one. Applying it is the user's choice - see
    /// <see cref="ApplySuggestedDeadZoneCommand"/> - so a measurement never rewrites a
    /// configured deadzone on its own.
    /// </summary>
    public double? SuggestedDeadZone { get; private set; }

    public string SuggestedDeadZoneText
        => SuggestedDeadZone is { } value ? MagnitudeText(value) : "—";

    public bool CanApplySuggestion => SuggestedDeadZone is not null;

    /// <summary>
    /// What the drift measurement is doing right now: the sample count while collecting, and
    /// the observed mean and peak magnitudes once there is enough data to describe.
    /// </summary>
    public string DriftStatus
    {
        get
        {
            if (IsMeasuringDrift)
            {
                return DriftSampleCount < MinDriftSamples
                    ? $"采集中… 已记录 {DriftSampleCount} 个样本（至少 {MinDriftSamples} 个）"
                    : $"采集中… 已记录 {DriftSampleCount} 个样本：平均 {MagnitudeText(DriftMeanMagnitude)}，最大 {MagnitudeText(DriftMaxMagnitude)}";
            }

            if (DriftSampleCount == 0)
            {
                return "尚未采集静差";
            }

            if (DriftSampleCount < MinDriftSamples)
            {
                return $"样本不足（{DriftSampleCount}/{MinDriftSamples}），请重新采集";
            }

            return $"已采集 {DriftSampleCount} 个样本：平均 {MagnitudeText(DriftMeanMagnitude)}，最大 {MagnitudeText(DriftMaxMagnitude)}";
        }
    }

    public string RealInputHint { get; } =
        "真实输入校准直接读取手柄采样（与手柄可视化同一来源），依次显示原始摇杆、死区过滤值与最终速度；日常移动、精准模式分别用「日常速度」「精准速度」两个读数调节。px/s 是输出的相对位移，系统指针速度设置会再缩放它，不保证等于实际屏幕速度。";

    public string DriftHint { get; } =
        "开始采集后请勿触碰摇杆，保持 1–2 秒再停止；系统按采集到的静差分布给出建议死区，确认后采用。";

    [RelayCommand]
    private void StartDriftMeasurement()
    {
        _driftMagnitudeSum = 0;
        DriftSampleCount = 0;
        DriftMeanMagnitude = 0;
        DriftMaxMagnitude = 0;
        SetSuggestedDeadZone(null);
        IsMeasuringDrift = true;
        OnPropertyChanged(nameof(DriftStatus));
    }

    [RelayCommand]
    private void StopDriftMeasurement()
    {
        IsMeasuringDrift = false;
        OnPropertyChanged(nameof(DriftStatus));
    }

    [RelayCommand]
    private void ApplySuggestedDeadZone()
    {
        // The suggestion is a multiple of the deadzone step, so the narrowing cast is exact.
        if (SuggestedDeadZone is { } value)
        {
            DeadZone = (float)value;
        }
    }

    private static string MagnitudeText(double magnitude)
        => magnitude.ToString("F2", CultureInfo.InvariantCulture);

    private void RecordDriftSample(double magnitude)
    {
        _driftMagnitudeSum += magnitude;
        DriftSampleCount++;
        DriftMeanMagnitude = _driftMagnitudeSum / DriftSampleCount;
        DriftMaxMagnitude = Math.Max(DriftMaxMagnitude, magnitude);

        OnPropertyChanged(nameof(DriftSampleCount));
        OnPropertyChanged(nameof(DriftMeanMagnitude));
        OnPropertyChanged(nameof(DriftMaxMagnitude));
        OnPropertyChanged(nameof(DriftStatus));

        RefreshDriftSuggestion();
    }

    /// <summary>
    /// Recomputes the suggestion from the distribution collected so far. It stays null until
    /// the sample count can describe a rest distribution, and a perfectly clean stick - no
    /// drift at all - honestly suggests no deadzone rather than a floor nobody needs.
    /// </summary>
    private void RefreshDriftSuggestion()
    {
        SetSuggestedDeadZone(
            DriftSampleCount >= MinDriftSamples && DriftMaxMagnitude > 0
                ? Math.Clamp(
                    Math.Ceiling(DriftMaxMagnitude * DriftSafetyFactor / DriftSuggestionStep) * DriftSuggestionStep,
                    DriftSuggestionStep,
                    MaxSuggestedDeadZone)
                : null);
    }

    private void SetSuggestedDeadZone(double? value)
    {
        if (SuggestedDeadZone == value)
        {
            return;
        }

        SuggestedDeadZone = value;

        OnPropertyChanged(nameof(SuggestedDeadZone));
        OnPropertyChanged(nameof(SuggestedDeadZoneText));
        OnPropertyChanged(nameof(CanApplySuggestion));
    }

    private void ResetDriftMeasurement()
    {
        _driftMagnitudeSum = 0;
        DriftSampleCount = 0;
        DriftMeanMagnitude = 0;
        DriftMaxMagnitude = 0;
        SetSuggestedDeadZone(null);
        OnPropertyChanged(nameof(DriftStatus));
    }

    /// <summary>
    /// Recomputes the three-stage calibration readout from the latest real sample: the raw
    /// stick, the deadzone-filtered deflection the response curve sees, and the daily and
    /// precision target speeds that deflection produces under the current settings.
    /// </summary>
    private void RefreshRealInputReadouts()
    {
        var (filteredX, filteredY) = StickMath.ApplyRadialDeadzone(_realStickX, _realStickY, DeadZone);
        var magnitude = Math.Sqrt((filteredX * filteredX) + (filteredY * filteredY));
        var shaped = ResponseCurves.Shape((float)magnitude, Curve, CurveStrength);

        RawStickX = _realStickX;
        RawStickY = _realStickY;
        RawMagnitude = Math.Sqrt((_realStickX * _realStickX) + (_realStickY * _realStickY));
        FilteredStickX = filteredX;
        FilteredStickY = filteredY;
        FilteredMagnitude = magnitude;
        DailySpeed = shaped * MaxSpeed;
        PrecisionSpeed = shaped * MaxSpeed * PrecisionScale;
    }

    public ObservableCollection<CurvePoint> ResponseCurvePoints { get; } = [];

    /// <summary>Panel copy for the physical-units differentiator; see <see cref="MappingDescriptions.PhysicalSpeedUnit"/>.</summary>
    public string PhysicalUnitHint { get; } = MappingDescriptions.PhysicalSpeedUnit();

    public string MaxSpeedHint { get; } = MappingDescriptions.MouseMaxSpeed();

    public string DeadZoneHint { get; } = MappingDescriptions.MouseDeadZone();

    public string CurveHint => MappingDescriptions.MouseCurve(Curve);

    public string CurveStrengthHint { get; } = MappingDescriptions.MouseCurveStrength();

    public string ResponseTimeHint { get; } = MappingDescriptions.MouseResponseTime();

    public string PrecisionScaleHint { get; } = MappingDescriptions.MousePrecisionScale();

    public string ScaleXHint { get; } = MappingDescriptions.MouseScaleX();

    public string ScaleYHint { get; } = MappingDescriptions.MouseScaleY();

    public string InvertYHint { get; } = MappingDescriptions.MouseInvertY();

    public string PrecisionButtonHint { get; } =
        "精准按键在映射界面为每个映射单独设置；这里只显示当前鼠标映射使用哪一个。";

    /// <summary>
    /// Every button the precision key can be bound to. <see cref="GamepadButton.Extra"/> is
    /// absent on purpose: it has no mask bit and <c>ToMask()</c> throws for it, so the engine
    /// could never read its pressed state.
    /// </summary>
    public IReadOnlyList<GamepadButton> PrecisionButtonOptions { get; } =
        Enum.GetValues<GamepadButton>().Where(button => button != GamepadButton.Extra).ToArray();

    partial void OnMaxSpeedChanged(float value)
    {
        ApplySettings();
        RefreshCalibrationReadouts();
    }

    partial void OnDeadZoneChanged(float value)
    {
        ApplySettings();
        RefreshCalibrationReadouts();
    }

    partial void OnCurveChanged(ResponseCurve value)
    {
        ApplySettings();
        RebuildCurvePoints();
        RefreshCalibrationReadouts();
        OnPropertyChanged(nameof(CurveHint));
    }

    partial void OnCurveStrengthChanged(float value)
    {
        if (value is < 0f or > 1f)
        {
            // Snap back into the legal [0,1] range; re-assigning re-enters this hook in
            // range, so the processor and graph rebuild once with the clamped value.
            CurveStrength = Math.Clamp(value, 0f, 1f);
            return;
        }

        ApplySettings();
        RebuildCurvePoints();
        RefreshCalibrationReadouts();
    }

    partial void OnResponseTimeChanged(float value) => ApplySettings();

    partial void OnPrecisionScaleChanged(float value)
    {
        ApplySettings();
        RefreshCalibrationReadouts();
    }

    partial void OnIsRealInputModeChanged(bool value)
    {
        if (value)
        {
            return;
        }

        // Leaving the calibration stops any measurement and parks the sample at rest, so
        // re-entering starts a clean session instead of confirming stale numbers.
        IsMeasuringDrift = false;
        _realStickX = 0f;
        _realStickY = 0f;
        PrecisionActive = false;
        ResetDriftMeasurement();
        RefreshRealInputReadouts();
    }

    partial void OnPrecisionModeChanged(PrecisionMode value)
    {
        ApplySettings();
        OnPropertyChanged(nameof(PrecisionModeLabel));
    }

    partial void OnScaleXChanged(float value) => ApplySettings();

    partial void OnScaleYChanged(float value) => ApplySettings();

    partial void OnInvertYChanged(bool value) => ApplySettings();

    partial void OnPrecisionButtonChanged(GamepadButton value)
        => OnPropertyChanged(nameof(PrecisionButtonLabel));

    public MouseStickSettings BuildSettings() => new()
    {
        DeadZone = DeadZone,
        MaxSpeed = MaxSpeed,
        Curve = Curve,
        CurveStrength = CurveStrength,
        ResponseTime = ResponseTime,
        PrecisionScale = PrecisionScale,
        PrecisionButton = PrecisionButton,
        PrecisionMode = PrecisionMode,
        ScaleX = ScaleX,
        ScaleY = ScaleY,
        InvertY = InvertY,
    };

    public void LoadFrom(MouseStickSettings settings)
    {
        DeadZone = settings.DeadZone;
        MaxSpeed = settings.MaxSpeed;
        Curve = settings.Curve;
        CurveStrength = settings.CurveStrength;
        ResponseTime = settings.ResponseTime;
        PrecisionScale = settings.PrecisionScale;
        PrecisionButton = settings.PrecisionButton;
        PrecisionMode = settings.PrecisionMode;
        ScaleX = settings.ScaleX;
        ScaleY = settings.ScaleY;
        InvertY = settings.InvertY;
    }

    /// <summary>
    /// Whether live stick input is available for the preview to reflect. The scripted sweep
    /// and the real samples are both stand-ins gated on this: while it is false the panel
    /// decays to rest instead of previewing until the window closes.
    /// </summary>
    public bool IsLive { get; set; }

    /// <summary>
    /// Advances the preview by one tick. Live input runs either the scripted sweep (demo) or
    /// the latest real sample (calibration); without live input the preview decays to rest.
    /// The tick cadence is the window's preview timer - samples arrive on the host's own
    /// cadence through <see cref="ApplySample"/> and are held until the next tick.
    /// </summary>
    public void Advance(TimeSpan delta)
    {
        var dt = (float)delta.TotalSeconds;

        if (!IsLive)
        {
            DecayToRest(dt);
            return;
        }

        if (IsRealInputMode)
        {
            var (dx, dy) = _processor.Update(_realStickX, _realStickY, PrecisionActive, dt);

            TravelDistance += Math.Abs(dx) + Math.Abs(dy);
            PreviewX = Clamp(PreviewX + (dx * PixelsToPreview));
            PreviewY = Clamp(PreviewY + (dy * PixelsToPreview));
            CurrentSpeed = _processor.CurrentSpeed;
            return;
        }

        _sweepSeconds += dt;

        var angle = _sweepSeconds * (MathF.PI * 2f / SweepPeriodSeconds);
        var stickX = SweepRadius * MathF.Sin(angle);
        var stickY = SweepRadius * MathF.Cos(angle);

        var (sweepDx, sweepDy) = _processor.Update(stickX, stickY, precisionActive: false, dt);

        TravelDistance += Math.Abs(sweepDx) + Math.Abs(sweepDy);
        PreviewX = Clamp(PreviewX + (sweepDx * PixelsToPreview));
        PreviewY = Clamp(PreviewY + (sweepDy * PixelsToPreview));
        CurrentSpeed = _processor.CurrentSpeed;
    }

    public void Reset()
    {
        _sweepSeconds = 0;
        IsLive = false;
        IsRealInputMode = false;
        ResetDriftMeasurement();
        _processor = new MouseStickProcessor(new MouseStickSettings());
        PreviewX = PreviewCenter;
        PreviewY = PreviewCenter;
        CurrentSpeed = 0;
        TravelDistance = 0;

        MaxSpeed = 1600f;
        DeadZone = 0.15f;
        Curve = ResponseCurve.Precise;
        CurveStrength = 1f;
        ResponseTime = 0.08f;
        PrecisionScale = 0.35f;
        PrecisionButton = GamepadButton.LeftStick;
        PrecisionMode = PrecisionMode.Hold;
        ScaleX = 1f;
        ScaleY = 1f;
        InvertY = true;
    }

    /// <summary>Drives the real processor with a centered stick so the reported speed decays
    /// through the engine and snaps to exact zero, and glides the dot back to center. The
    /// sweep phase is held, so resuming later continues where it paused.</summary>
    private void DecayToRest(float dt)
    {
        _ = _processor.Update(0f, 0f, precisionActive: false, dt);

        PreviewX = GlideHome(PreviewX, dt);
        PreviewY = GlideHome(PreviewY, dt);
        CurrentSpeed = _processor.CurrentSpeed;

        // Once fully at rest the cumulative displacement reads zero, so "settled" always
        // means dot centered, 0 px/s and 0 px of accumulated travel.
        if (PreviewX == PreviewCenter && PreviewY == PreviewCenter && CurrentSpeed == 0)
        {
            TravelDistance = 0;
        }
    }

    private static double GlideHome(double value, float dt)
    {
        var offset = value - PreviewCenter;
        var step = RestGlideSpeed * dt;

        return Math.Abs(offset) <= step
            ? PreviewCenter
            : value - (Math.Sign(offset) * step);
    }

    private void ApplySettings()
    {
        _processor.UpdateSettings(BuildSettings());
    }

    /// <summary>
    /// Keeps the calibration readouts in step with a setting dragged mid-measurement, so the
    /// filtered value and both target speeds answer the slider while the stick is held. In
    /// demo mode the readouts belong to the sweep, so nothing is recomputed.
    /// </summary>
    private void RefreshCalibrationReadouts()
    {
        if (IsRealInputMode)
        {
            RefreshRealInputReadouts();
        }
    }

    private void RebuildCurvePoints()
    {
        ResponseCurvePoints.Clear();

        for (var i = 0; i <= 32; i++)
        {
            var x = i / 32.0;
            ResponseCurvePoints.Add(new CurvePoint(x, ResponseCurves.Shape((float)x, Curve, CurveStrength)));
        }
    }

    private static double Clamp(double value)
        => value < 0 ? 0 : value > PreviewSize ? PreviewSize : value;
}
