using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// Live calibration panel for stick-to-wheel. Same scripted-sweep approach as
/// <see cref="MouseSensitivityViewModel"/> so the offset readout is real engine output.
/// </summary>
public partial class ScrollSensitivityViewModel : ObservableObject
{
    private const float SweepPeriodSeconds = 4f;

    private const float SweepRadius = 0.8f;

    private ScrollStickProcessor _processor = new(new ScrollStickSettings());
    private float _sweepSeconds;

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

    public ObservableCollection<CurvePoint> ResponseCurvePoints { get; } = [];

    partial void OnMaxTicksPerSecondChanged(float value) => RebuildProcessor();

    partial void OnDeadZoneChanged(float value) => RebuildProcessor();

    partial void OnCurveChanged(ResponseCurve value)
    {
        RebuildProcessor();
        RebuildCurvePoints();
    }

    partial void OnCurveStrengthChanged(float value)
    {
        RebuildProcessor();
        RebuildCurvePoints();
    }

    partial void OnResponseTimeChanged(float value) => RebuildProcessor();

    partial void OnEnableHorizontalChanged(bool value) => RebuildProcessor();

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
    /// is a stand-in for that input, so it only runs while this is true; otherwise the panel
    /// decays to rest instead of sweeping until the window closes.
    /// </summary>
    public bool IsLive { get; set; }

    public void Advance(TimeSpan delta)
    {
        var dt = (float)delta.TotalSeconds;

        if (!IsLive)
        {
            DecayToRest(dt);
            return;
        }

        _sweepSeconds += dt;

        var angle = _sweepSeconds * (MathF.PI * 2f / SweepPeriodSeconds);
        var stickX = SweepRadius * MathF.Sin(angle);
        var stickY = SweepRadius * MathF.Cos(angle);

        var (horizontal, vertical) = _processor.Update(stickX, stickY, dt);

        WheelOffset += vertical;
        CurrentRate = _processor.CurrentRate;
        _ = horizontal;
    }

    public void Reset()
    {
        _sweepSeconds = 0;
        IsLive = false;
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

    private void RebuildProcessor() => _processor = new ScrollStickProcessor(BuildSettings());

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
