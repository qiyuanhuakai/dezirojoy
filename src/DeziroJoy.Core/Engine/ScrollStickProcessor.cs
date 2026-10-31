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
    private readonly ScrollStickSettings _settings;
    private double _rateX;
    private double _rateY;
    private double _residualX;
    private double _residualY;

    public ScrollStickProcessor(ScrollStickSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

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

        var verticalSign = _settings.InvertY ? -1.0 : 1.0;
        _residualX += _rateX * dt;
        _residualY += _rateY * dt * verticalSign;

        var horizontal = (int)_residualX;
        var vertical = (int)_residualY;
        _residualX -= horizontal;
        _residualY -= vertical;

        return new ScrollStickDelta(horizontal, vertical);
    }

    public void Reset()
    {
        _rateX = 0;
        _rateY = 0;
        _residualX = 0;
        _residualY = 0;
    }
}
