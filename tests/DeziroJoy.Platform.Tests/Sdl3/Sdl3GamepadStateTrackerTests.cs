using DeziroJoy.Core.Input;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Sdl3;
using DeziroJoy.Platform.Windows.XInput;
using SDL3;
using Xunit;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// Normalization contract of the SDL3 tracker: every standard button lands on its logical
/// position, positionless buttons stay bindable as raw codes, sticks land on the Core's
/// positive-up scale, and the triggers hold the same hysteresis band the XInput and evdev
/// layers use. All pure - no SDL runtime involved.
/// </summary>
public class Sdl3GamepadStateTrackerTests
{
    private static readonly SDL.GamepadButton[] StandardButtons =
    [
        SDL.GamepadButton.South,
        SDL.GamepadButton.East,
        SDL.GamepadButton.West,
        SDL.GamepadButton.North,
        SDL.GamepadButton.Back,
        SDL.GamepadButton.Guide,
        SDL.GamepadButton.Start,
        SDL.GamepadButton.LeftStick,
        SDL.GamepadButton.RightStick,
        SDL.GamepadButton.LeftShoulder,
        SDL.GamepadButton.RightShoulder,
        SDL.GamepadButton.DPadUp,
        SDL.GamepadButton.DPadDown,
        SDL.GamepadButton.DPadLeft,
        SDL.GamepadButton.DPadRight,
    ];

    [Fact]
    public void Every_Standard_Button_Is_Mapped_From_The_Sdl_Button_Set()
    {
        var tracker = new Sdl3GamepadStateTracker();

        foreach (var button in StandardButtons)
        {
            tracker.ApplyButton(button, pressed: true);
        }

        // Triggers are axes, not buttons, on the SDL side; full travel presses both.
        tracker.ApplyAxis(SDL.GamepadAxis.LeftTrigger, 32767);
        tracker.ApplyAxis(SDL.GamepadAxis.RightTrigger, 32767);

        var state = tracker.Snapshot();

        foreach (var button in Enum.GetValues<GamepadButton>().Where(b => b != GamepadButton.Extra))
        {
            Assert.True(state.IsPressed(button), $"{button} is not mapped from SDL");
        }

        Assert.Empty(state.ExtraButtons);
    }

    [Fact]
    public void Positionless_Buttons_Are_Kept_As_Raw_Sdl_Codes()
    {
        // Paddles, the touchpad click and the misc cluster have no logical position, so they
        // travel as raw SDL button ordinals through ExtraButtons - bindable, but never
        // dressed up as A/B/X/Y.
        var tracker = new Sdl3GamepadStateTracker();

        tracker.ApplyButton(SDL.GamepadButton.RightPaddle1, pressed: true);
        tracker.ApplyButton(SDL.GamepadButton.LeftPaddle2, pressed: true);
        tracker.ApplyButton(SDL.GamepadButton.Touchpad, pressed: true);
        tracker.ApplyButton(SDL.GamepadButton.Misc1, pressed: true);

        var state = tracker.Snapshot();

        Assert.Equal(GamepadButtonMask.None, state.PressedButtons);
        // Raw codes come back in code order, like the evdev tracker's sorted set.
        Assert.Equal(
            new[]
            {
                (int)SDL.GamepadButton.Misc1,
                (int)SDL.GamepadButton.RightPaddle1,
                (int)SDL.GamepadButton.LeftPaddle2,
                (int)SDL.GamepadButton.Touchpad,
            },
            state.ExtraButtons);

        // Releasing one extra button removes exactly that code and nothing else.
        tracker.ApplyButton(SDL.GamepadButton.Touchpad, pressed: false);
        Assert.DoesNotContain((int)SDL.GamepadButton.Touchpad, tracker.Snapshot().ExtraButtons);
        Assert.Contains((int)SDL.GamepadButton.Misc1, tracker.Snapshot().ExtraButtons);
    }

    [Fact]
    public void A_Neutral_Pad_Produces_A_Neutral_State()
    {
        var tracker = new Sdl3GamepadStateTracker();

        var state = tracker.Snapshot();

        Assert.Equal(GamepadButtonMask.None, state.PressedButtons);
        Assert.Equal(0f, state.LeftStickX);
        Assert.Equal(0f, state.LeftStickY);
        Assert.Equal(0f, state.RightStickX);
        Assert.Equal(0f, state.RightStickY);
        Assert.Empty(state.ExtraButtons);
    }

    [Fact]
    public void Stick_Axes_Are_Normalized_To_The_XInput_Scale()
    {
        var tracker = new Sdl3GamepadStateTracker();

        tracker.ApplyAxis(SDL.GamepadAxis.LeftX, -32768);
        tracker.ApplyAxis(SDL.GamepadAxis.LeftY, short.MinValue);
        tracker.ApplyAxis(SDL.GamepadAxis.RightX, 32767);
        tracker.ApplyAxis(SDL.GamepadAxis.RightY, short.MinValue);

        var state = tracker.Snapshot();

        Assert.Equal(-1f, state.LeftStickX);
        Assert.Equal(1f, state.LeftStickY);
        Assert.Equal(32767f / 32768f, state.RightStickX, 5);
        Assert.Equal(1f, state.RightStickY);
    }

    [Fact]
    public void Sdl_Reports_Positive_Stick_Y_When_Pushed_Up_After_Negation()
    {
        // SDL, like evdev, reports -32768 for "up" and +32767 for "down" - the opposite of
        // the Core contract. The tracker is the single place that is reconciled.
        var up = new Sdl3GamepadStateTracker();
        up.ApplyAxis(SDL.GamepadAxis.LeftY, -32768);
        up.ApplyAxis(SDL.GamepadAxis.RightY, -32768);

        Assert.Equal(1f, up.Snapshot().LeftStickY);
        Assert.Equal(1f, up.Snapshot().RightStickY);

        var down = new Sdl3GamepadStateTracker();
        down.ApplyAxis(SDL.GamepadAxis.LeftY, 32767);
        down.ApplyAxis(SDL.GamepadAxis.RightY, 32767);

        Assert.Equal(-32767f / 32768f, down.Snapshot().LeftStickY, 5);
        Assert.Equal(-32767f / 32768f, down.Snapshot().RightStickY, 5);
    }

    [Fact]
    public void Both_Platforms_Agree_That_A_Pushed_Up_Stick_Is_Positive()
    {
        var sdl = new Sdl3GamepadStateTracker();
        sdl.ApplyAxis(SDL.GamepadAxis.LeftY, -32768);

        var windows = XInputStateMapper.ToGamepadState(new XInputGamepad { ThumbLY = 32767 });

        var evdev = new EvdevGamepadStateTracker();
        evdev.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsY, -32768));

        Assert.True(sdl.Snapshot().LeftStickY > 0.9f, "SDL: a pushed-up stick must be positive");
        Assert.True(windows.LeftStickY > 0.9f, "XInput: a pushed-up stick must be positive");
        Assert.True(evdev.Snapshot().LeftStickY > 0.9f, "evdev: a pushed-up stick must be positive");
    }

    [Fact]
    public void Both_Platforms_Press_The_Trigger_At_The_Same_Travel_Fraction()
    {
        // 140/255 of travel is the shared press point: byte 140 on XInput, the same fraction
        // of SDL's 0..32767 trigger range.
        var sdl = new Sdl3GamepadStateTracker();
        sdl.ApplyAxis(SDL.GamepadAxis.LeftTrigger, PressBoundary());

        Assert.True(sdl.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        var below = new Sdl3GamepadStateTracker();
        below.ApplyAxis(SDL.GamepadAxis.LeftTrigger, (short)(PressBoundary() - 1));

        Assert.False(below.Snapshot().IsPressed(GamepadButton.LeftTrigger));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(14777, false)]
    [InlineData(14778, false)] // inside the hysteresis band, starts released
    [InlineData(17989, false)] // inside the hysteresis band
    [InlineData(17990, true)]
    [InlineData(20000, true)]
    [InlineData(32767, true)]
    public void Triggers_Press_Only_Above_The_Hysteresis_Band(short triggerValue, bool expectedPressed)
    {
        // A fresh tracker per case: each row starts released, like the theory's per-case
        // device, so the expectations never depend on declaration order.
        var tracker = new Sdl3GamepadStateTracker();
        tracker.ApplyAxis(SDL.GamepadAxis.LeftTrigger, triggerValue);

        Assert.Equal(expectedPressed, tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.RightTrigger));
    }

    [Fact]
    public void Trigger_Holds_Its_State_Inside_The_Hysteresis_Band()
    {
        var tracker = new Sdl3GamepadStateTracker();

        Assert.True(Apply(tracker, SDL.GamepadAxis.LeftTrigger, 32767).IsPressed(GamepadButton.LeftTrigger));

        // Easing into the band keeps the trigger pressed: only crossing the release point
        // (<= 14777) lets go.
        Assert.True(Apply(tracker, SDL.GamepadAxis.LeftTrigger, 17989).IsPressed(GamepadButton.LeftTrigger));
        Assert.True(Apply(tracker, SDL.GamepadAxis.LeftTrigger, 14778).IsPressed(GamepadButton.LeftTrigger));
        Assert.False(Apply(tracker, SDL.GamepadAxis.LeftTrigger, 14777).IsPressed(GamepadButton.LeftTrigger));

        // Re-entering the band from below keeps it released until the press point.
        Assert.False(Apply(tracker, SDL.GamepadAxis.LeftTrigger, 14778).IsPressed(GamepadButton.LeftTrigger));
        Assert.False(Apply(tracker, SDL.GamepadAxis.LeftTrigger, 17989).IsPressed(GamepadButton.LeftTrigger));
        Assert.True(Apply(tracker, SDL.GamepadAxis.LeftTrigger, 17990).IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void Midpoint_Noise_Does_Not_Chatter_The_Trigger()
    {
        // M29 on the XInput path: 126/128 chatter flipped the pressed flag 9 times in 10
        // samples, and the edge-driven mapping engine injected BeginHold + EndHold pairs at
        // up to 500 Hz. Inside the hysteresis band the state must not move, so neither
        // symptom can be produced downstream.
        var tracker = new Sdl3GamepadStateTracker();

        for (var i = 0; i < 50; i++)
        {
            var value = (short)(i % 2 == 0 ? 16383 : 16384); // ~0.5 travel
            Assert.False(Apply(tracker, SDL.GamepadAxis.LeftTrigger, value).IsPressed(GamepadButton.LeftTrigger));
        }
    }

    [Fact]
    public void Band_Noise_Does_Not_Release_A_Held_Trigger()
    {
        var tracker = new Sdl3GamepadStateTracker();

        Assert.True(Apply(tracker, SDL.GamepadAxis.LeftTrigger, 32767).IsPressed(GamepadButton.LeftTrigger));

        for (var value = 14778; value <= 17989; value++)
        {
            Assert.True(Apply(tracker, SDL.GamepadAxis.LeftTrigger, (short)value).IsPressed(GamepadButton.LeftTrigger));
        }
    }

    [Fact]
    public void Each_Tracker_Keeps_Its_Own_Trigger_State()
    {
        // The engine polls every connected pad once per tick on one thread; per-device
        // trackers are what keep one pad's rest value from resetting another pad's trigger.
        var padA = new Sdl3GamepadStateTracker();
        var padB = new Sdl3GamepadStateTracker();

        Assert.True(Apply(padA, SDL.GamepadAxis.LeftTrigger, 32767).IsPressed(GamepadButton.LeftTrigger));

        for (var i = 0; i < 10; i++)
        {
            Assert.False(Apply(padB, SDL.GamepadAxis.LeftTrigger, 0).IsPressed(GamepadButton.LeftTrigger));
            Assert.True(Apply(padA, SDL.GamepadAxis.LeftTrigger, 16000).IsPressed(GamepadButton.LeftTrigger));
        }

        Assert.False(Apply(padA, SDL.GamepadAxis.LeftTrigger, 0).IsPressed(GamepadButton.LeftTrigger));
        Assert.True(Apply(padB, SDL.GamepadAxis.LeftTrigger, 32767).IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void Reset_Drops_Every_Held_Input()
    {
        var tracker = new Sdl3GamepadStateTracker();

        tracker.ApplyButton(SDL.GamepadButton.South, pressed: true);
        tracker.ApplyButton(SDL.GamepadButton.Misc1, pressed: true);
        tracker.ApplyAxis(SDL.GamepadAxis.LeftX, 32767);
        tracker.ApplyAxis(SDL.GamepadAxis.RightTrigger, 32767);

        tracker.Reset();

        var state = tracker.Snapshot();
        Assert.Equal(GamepadButtonMask.None, state.PressedButtons);
        Assert.Equal(0f, state.LeftStickX);
        Assert.False(state.IsPressed(GamepadButton.RightTrigger));
        Assert.Empty(state.ExtraButtons);
    }

    /// <summary>Smallest SDL trigger value that reaches the 140/255 press point.</summary>
    private static short PressBoundary() => (short)MathF.Ceiling(Sdl3GamepadStateTracker.TriggerPressPoint * 32767f);

    private static GamepadState Apply(Sdl3GamepadStateTracker tracker, SDL.GamepadAxis axis, short value)
    {
        tracker.ApplyAxis(axis, value);
        return tracker.Snapshot();
    }
}
