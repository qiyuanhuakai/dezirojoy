using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

public readonly record struct CurvePoint(double X, double Y);

/// <summary>
/// Live calibration panel for stick-to-mouse. Drives the real
/// <see cref="MouseStickProcessor"/> with a scripted sweep so the preview, the speed
/// readout and the response graph all reflect the actual engine behavior.
/// </summary>
public partial class MouseSensitivityViewModel : ObservableObject
{
    public const double PreviewCenter = 100.0;

    public const double PreviewSize = 200.0;

    private const double PixelsToPreview = 0.05;

    private const float SweepPeriodSeconds = 4f;

    private const float SweepRadius = 0.8f;

    private const double RestGlideSpeed = 400.0;

    private MouseStickProcessor _processor = new(new MouseStickSettings());
    private float _sweepSeconds;

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
    private bool _invertY = true;

    [ObservableProperty]
    private double _previewX = PreviewCenter;

    [ObservableProperty]
    private double _previewY = PreviewCenter;

    [ObservableProperty]
    private double _currentSpeed;

    [ObservableProperty]
    private double _travelDistance;

    public ObservableCollection<CurvePoint> ResponseCurvePoints { get; } = [];

    /// <summary>Panel copy for the physical-units differentiator; see <see cref="MappingDescriptions.PhysicalSpeedUnit"/>.</summary>
    public string PhysicalUnitHint { get; } = MappingDescriptions.PhysicalSpeedUnit();

    public string MaxSpeedHint { get; } = MappingDescriptions.MouseMaxSpeed();

    public string DeadZoneHint { get; } = MappingDescriptions.MouseDeadZone();

    public string CurveHint => MappingDescriptions.MouseCurve(Curve);

    public string CurveStrengthHint { get; } = MappingDescriptions.MouseCurveStrength();

    public string ResponseTimeHint { get; } = MappingDescriptions.MouseResponseTime();

    public string PrecisionScaleHint { get; } = MappingDescriptions.MousePrecisionScale();

    public string InvertYHint { get; } = MappingDescriptions.MouseInvertY();

    partial void OnMaxSpeedChanged(float value) => RebuildProcessor();

    partial void OnDeadZoneChanged(float value) => RebuildProcessor();

    partial void OnCurveChanged(ResponseCurve value)
    {
        RebuildProcessor();
        RebuildCurvePoints();
        OnPropertyChanged(nameof(CurveHint));
    }

    partial void OnCurveStrengthChanged(float value)
    {
        RebuildProcessor();
        RebuildCurvePoints();
    }

    partial void OnResponseTimeChanged(float value) => RebuildProcessor();

    partial void OnInvertYChanged(bool value) => RebuildProcessor();

    public MouseStickSettings BuildSettings() => new()
    {
        DeadZone = DeadZone,
        MaxSpeed = MaxSpeed,
        Curve = Curve,
        CurveStrength = CurveStrength,
        ResponseTime = ResponseTime,
        PrecisionScale = PrecisionScale,
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
        InvertY = settings.InvertY;
    }

    /// <summary>
    /// Whether live stick input is available for the preview to reflect. The scripted sweep
    /// is a stand-in for that input, so it only runs while this is true; otherwise the panel
    /// decays to rest instead of sweeping until the window closes.
    /// </summary>
    public bool IsLive { get; set; }

    /// <summary>Advances the scripted sweep by one tick and refreshes the readouts.</summary>
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

        var (dx, dy) = _processor.Update(stickX, stickY, precisionActive: false, dt);

        TravelDistance += Math.Abs(dx) + Math.Abs(dy);
        PreviewX = Clamp(PreviewX + (dx * PixelsToPreview));
        PreviewY = Clamp(PreviewY + (dy * PixelsToPreview));
        CurrentSpeed = _processor.CurrentSpeed;
    }

    public void Reset()
    {
        _sweepSeconds = 0;
        IsLive = false;
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
    }

    private static double GlideHome(double value, float dt)
    {
        var offset = value - PreviewCenter;
        var step = RestGlideSpeed * dt;

        return Math.Abs(offset) <= step
            ? PreviewCenter
            : value - (Math.Sign(offset) * step);
    }

    private void RebuildProcessor() => _processor = new MouseStickProcessor(BuildSettings());

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
