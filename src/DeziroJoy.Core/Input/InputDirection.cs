namespace DeziroJoy.Core.Input;

/// <summary>
/// Cardinal direction a digital source implies.
/// </summary>
/// <remarks>
/// <para>
/// A button is binary, so a button driving a continuous target (mouse move / wheel) names one
/// direction and implies full deflection in it. A stick is an axis, so a stick driving a
/// digital target (key / combo / mouse button) names one direction and acts as a switch for
/// it: deflecting past the threshold presses, returning releases.
/// </para>
/// </remarks>
public enum InputDirection
{
    Up,
    Down,
    Left,
    Right,
}
