using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Engine;

public readonly record struct ScrollStickDelta(int HorizontalNotches, int VerticalNotches);

/// <summary>
/// Converts stick deflection into wheel notches (120 units per notch downstream).
/// Rates are smoothed over <see cref="ScrollStickSettings.ResponseTime"/> and fractional
/// notches accumulate so slow deflections still produce whole scroll steps.
/// </summary>
public sealed class ScrollStickProcessor
{
    private ScrollStickSettings _settings;
    private double _rateX;
    private double _rateY;
    private double _residualX;
    private double _residualY;

    public ScrollStickProcessor(ScrollStickSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>
    /// Replaces the settings without discarding the smoothed rate or the sub-notch
    /// residual, so applying a setting mid-drag never makes the wheel stutter.
    /// </summary>
    public void UpdateSettings(ScrollStickSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>Current smoothed scroll rate in notches/second (live UI readout).</summary>
    public double CurrentRate => Math.Sqrt((_rateX * _rateX) + (_rateY * _rateY));

    public ScrollStickDelta Update(float stickX, float stickY, float deltaSeconds)
    {
        var dt = MouseStickProcessor.SanitizeDeltaSeconds(deltaSeconds);

        var (deflectionX, deflectionY) = StickMath.ApplyRadialDeadzone(stickX, stickY, _settings.DeadZone);
        var magnitude = Math.Sqrt((deflectionX * deflectionX) + (deflectionY * deflectionY));

        double targetX = 0;
        double targetY = 0;
        if (magnitude > 0f)
        {
            var shaped = ResponseCurves.Shape((float)magnitude, _settings.Curve, _settings.CurveStrength);
            var rate = shaped * _settings.MaxTicksPerSecond;
            targetY = deflectionY / magnitude * rate;
            targetX = _settings.EnableHorizontal ? deflectionX / magnitude * rate : 0;
        }

        var alpha = MouseStickProcessor.SmoothingAlpha(_settings.ResponseTime, dt);
        _rateX += (targetX - _rateX) * alpha;
        _rateY += (targetY - _rateY) * alpha;

        SnapToRest(ref _rateX, ref _residualX, targetX);
        SnapToRest(ref _rateY, ref _residualY, targetY);

        var verticalSign = _settings.InvertY ? -1.0 : 1.0;
        _residualX += _rateX * dt;
        _residualY += _rateY * dt * verticalSign;

        var horizontal = (int)_residualX;
        var vertical = (int)_residualY;
        _residualX -= horizontal;
        _residualY -= vertical;

        return new ScrollStickDelta(horizontal, vertical);
    }

    /// <summary>
    /// Once the target is zero and the smoothed rate has decayed below
    /// <see cref="StickMath.RestEpsilon"/>, the axis is at rest: the smoothed value and any
    /// pending sub-notch residual snap to exact zero so a released stick stops emitting
    /// notches after a finite number of ticks instead of trailing forever.
    /// </summary>
    private static void SnapToRest(ref double rate, ref double residual, double target)
    {
        if (target == 0 && Math.Abs(rate) < StickMath.RestEpsilon)
        {
            rate = 0;
            residual = 0;
        }
    }

    public void Reset()
    {
        _rateX = 0;
        _rateY = 0;
        _residualX = 0;
        _residualY = 0;
    }
}
