using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Engine;

public readonly record struct MouseStickDelta(int Dx, int Dy);

/// <summary>
/// Converts stick deflection into pointer movement using physical units (px/s) with
/// deadzone, response curve, exponential speed smoothing and sub-pixel accumulation.
/// Stateful: one instance per mapped stick; feed it a fixed timestep from the host loop.
/// </summary>
public sealed class MouseStickProcessor
{
    private readonly MouseStickSettings _settings;
    private double _velocityX;
    private double _velocityY;
    private double _residualX;
    private double _residualY;

    public MouseStickProcessor(MouseStickSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>Current smoothed pointer speed in px/s (diagnostics and live UI readout).</summary>
    public double CurrentSpeedX => _velocityX;

    public double CurrentSpeedY => _velocityY;

    public double CurrentSpeed => Math.Sqrt((_velocityX * _velocityX) + (_velocityY * _velocityY));

    public MouseStickDelta Update(float stickX, float stickY, bool precisionActive, float deltaSeconds)
    {
        var dt = SanitizeDeltaSeconds(deltaSeconds);

        var (deflectionX, deflectionY) = StickMath.ApplyRadialDeadzone(stickX, stickY, _settings.DeadZone);
        var magnitude = Math.Sqrt((deflectionX * deflectionX) + (deflectionY * deflectionY));

        double targetX = 0;
        double targetY = 0;
        if (magnitude > 0f)
        {
            var shaped = ResponseCurves.Shape((float)magnitude, _settings.Curve, _settings.CurveStrength);
            var speed = shaped * _settings.MaxSpeed * (precisionActive ? _settings.PrecisionScale : 1f);
            targetX = deflectionX / magnitude * speed;
            targetY = deflectionY / magnitude * speed;
        }

        var alpha = SmoothingAlpha(_settings.ResponseTime, dt);
        _velocityX += (targetX - _velocityX) * alpha;
        _velocityY += (targetY - _velocityY) * alpha;

        var verticalSign = _settings.InvertY ? -1.0 : 1.0;
        _residualX += _velocityX * dt * _settings.ScaleX;
        _residualY += _velocityY * dt * _settings.ScaleY * verticalSign;

        var moveX = (int)_residualX;
        var moveY = (int)_residualY;
        _residualX -= moveX;
        _residualY -= moveY;

        return new MouseStickDelta(moveX, moveY);
    }

    public void Reset()
    {
        _velocityX = 0;
        _velocityY = 0;
        _residualX = 0;
        _residualY = 0;
    }

    internal static double SmoothingAlpha(float responseTime, double dt)
    {
        if (dt <= 0)
        {
            return 0;
        }

        if (responseTime <= 0f)
        {
            return 1;
        }

        return 1 - Math.Exp(-dt / responseTime);
    }

    internal static double SanitizeDeltaSeconds(float deltaSeconds)
        => float.IsNaN(deltaSeconds) || deltaSeconds < 0 ? 0 : deltaSeconds;
}
