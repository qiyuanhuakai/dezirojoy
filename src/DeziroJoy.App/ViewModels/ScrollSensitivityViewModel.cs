using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// Live calibration panel for stick-to-wheel. Same two-source approach as
/// <see cref="MouseSensitivityViewModel"/> - a scripted sweep that needs no gamepad, plus real
/// host samples that show the raw stick, the deadzone-filtered deflection and the final rate,
/// and measure the at-rest drift distribution for a confirmable deadzone suggestion - so the
/// offset readout is real engine output either way.
/// </summary>
public partial class ScrollSensitivityViewModel : ObservableObject
{
    private const float SweepPeriodSeconds = 4f;

    private const float SweepRadius = 0.8f;

    /// <summary>Samples below this count are too few to call an at-rest drift distribution.</summary>
    public const int MinDriftSamples = 20;

    /// <summary>
    /// Headroom the suggested deadzone keeps over the observed drift peak: a pad that only
    /// just clears its own noise still gives up a sliver of the center, which is the trade
    /// the suggestion makes so a resting stick never scrolls the wheel.
    /// </summary>
    private const double DriftSafetyFactor = 1.25;

    /// <summary>The suggestion is rounded up to this step, the deadzone slider's own tick.</summary>
    private const double DriftSuggestionStep = 0.01;

    /// <summary>The deadzone slider's maximum; a suggestion must stay reachable through it.</summary>
    private const double MaxSuggestedDeadZone = 0.6;

    private ScrollStickProcessor _processor = new(new ScrollStickSettings());
    private float _sweepSeconds;
    private float _realStickX;
    private float _realStickY;
    private double _driftMagnitudeSum;

    public ScrollSensitivityViewModel()
    {
    }

    [ObservableProperty]
    private float _maxTicksPerSecond = 12f;

    [ObservableProperty]
    private float _deadZone = 0.2f;

    [ObservableProperty]
    private ResponseCurve _curve = ResponseCurve.Precise;

    [ObservableProperty]
    private float _curveStrength = 1f;

    [ObservableProperty]
    private float _responseTime = 0.05f;

    [ObservableProperty]
    private bool _invertY;

    [ObservableProperty]
    private bool _enableHorizontal = true;

    [ObservableProperty]
    private double _wheelOffset;

    [ObservableProperty]
    private double _currentRate;

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
    /// for every sample, latest sample wins between preview ticks. Only the left stick - the
    /// stick the wheel mapping reads - is consumed.
    /// </summary>
    public void ApplySample(IGamepadState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _realStickX = state.LeftStickX;
        _realStickY = state.LeftStickY;

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

    /// <summary>Target rate in notches/second at the current deflection.</summary>
    [ObservableProperty]
    private double _dailyRate;

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
        "真实输入校准直接读取手柄采样（与手柄可视化同一来源），依次显示原始摇杆、死区过滤值与最终滚动速率，并采集静差给出建议死区。";

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
    /// Recomputes the calibration readout from the latest real sample: the raw stick, the
    /// deadzone-filtered deflection the response curve sees, and the target rate that
    /// deflection produces under the current settings.
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
        DailyRate = shaped * MaxTicksPerSecond;
    }

    public ObservableCollection<CurvePoint> ResponseCurvePoints { get; } = [];

    public string RateHint { get; } = MappingDescriptions.ScrollRate();

    public string DeadZoneHint { get; } = MappingDescriptions.ScrollDeadZone();

    public string CurveHint => MappingDescriptions.ScrollCurve(Curve);

    public string CurveStrengthHint { get; } = MappingDescriptions.MouseCurveStrength();

    public string ResponseTimeHint { get; } = MappingDescriptions.ScrollResponseTime();

    public string InvertYHint { get; } = MappingDescriptions.ScrollInvertY();

    public string EnableHorizontalHint { get; } = MappingDescriptions.ScrollEnableHorizontal();

    partial void OnMaxTicksPerSecondChanged(float value)
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

    partial void OnInvertYChanged(bool value) => ApplySettings();

    partial void OnEnableHorizontalChanged(bool value) => ApplySettings();

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
        ResetDriftMeasurement();
        RefreshRealInputReadouts();
    }

    public ScrollStickSettings BuildSettings() => new()
    {
        DeadZone = DeadZone,
        MaxTicksPerSecond = MaxTicksPerSecond,
        Curve = Curve,
        CurveStrength = CurveStrength,
        ResponseTime = ResponseTime,
        InvertY = InvertY,
        EnableHorizontal = EnableHorizontal,
    };

    public void LoadFrom(ScrollStickSettings settings)
    {
        DeadZone = settings.DeadZone;
        MaxTicksPerSecond = settings.MaxTicksPerSecond;
        Curve = settings.Curve;
        CurveStrength = settings.CurveStrength;
        ResponseTime = settings.ResponseTime;
        InvertY = settings.InvertY;
        EnableHorizontal = settings.EnableHorizontal;
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
            var (horizontal, vertical) = _processor.Update(_realStickX, _realStickY, dt);

            WheelOffset += vertical;
            CurrentRate = _processor.CurrentRate;
            _ = horizontal;
            return;
        }

        _sweepSeconds += dt;

        var angle = _sweepSeconds * (MathF.PI * 2f / SweepPeriodSeconds);
        var stickX = SweepRadius * MathF.Sin(angle);
        var stickY = SweepRadius * MathF.Cos(angle);

        var (sweepHorizontal, sweepVertical) = _processor.Update(stickX, stickY, dt);

        WheelOffset += sweepVertical;
        CurrentRate = _processor.CurrentRate;
        _ = sweepHorizontal;
    }

    public void Reset()
    {
        _sweepSeconds = 0;
        IsLive = false;
        IsRealInputMode = false;
        ResetDriftMeasurement();
        _processor = new ScrollStickProcessor(new ScrollStickSettings());
        WheelOffset = 0;
        CurrentRate = 0;

        MaxTicksPerSecond = 12f;
        DeadZone = 0.2f;
        Curve = ResponseCurve.Precise;
        CurveStrength = 1f;
        ResponseTime = 0.05f;
        InvertY = false;
        EnableHorizontal = true;
    }

    /// <summary>Drives the real processor with a centered stick so the reported rate decays
    /// through the engine and snaps to exact zero, holding the accumulated offset. The sweep
    /// phase is held, so resuming later continues where it paused.</summary>
    private void DecayToRest(float dt)
    {
        _ = _processor.Update(0f, 0f, dt);

        CurrentRate = _processor.CurrentRate;
    }

    private void ApplySettings()
    {
        _processor.UpdateSettings(BuildSettings());
    }

    /// <summary>
    /// Keeps the calibration readouts in step with a setting dragged mid-measurement, so the
    /// filtered value and the target rate answer the slider while the stick is held. In demo
    /// mode the readouts belong to the sweep, so nothing is recomputed.
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
}
