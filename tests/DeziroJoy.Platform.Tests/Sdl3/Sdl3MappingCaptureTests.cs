using DeziroJoy.Platform.Sdl3;
using Xunit;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// The capture state machine and its activation detection: which control a new sample means,
/// which token that control produces for each kind of step, and what the flow does when a
/// control is missing or the operator gives up. Deterministic by construction - the snapshots
/// are plain data, so no SDL and no controller are involved.
/// </summary>
public sealed class Sdl3MappingCaptureTests
{
    private const string Guid = "03000000790000000006000000000000";

    private static Sdl3JoystickSnapshot Snapshot(
        bool[]? buttons = null,
        short[]? axes = null,
        byte[]? hats = null)
        => new(buttons ?? [], axes ?? [], hats ?? []);

    private static Sdl3MappingStep Step(string key)
        => Sdl3MappingStep.CreateDefault().Single(step => step.Key == key);

    [Fact]
    public void A_Newly_Pressed_Button_Is_Detected_Once()
    {
        var baseline = Snapshot(buttons: [false, false, false]);
        var pressed = Snapshot(buttons: [false, false, true]);

        var found = Sdl3MappingActivation.Detect(baseline, pressed);

        var control = Assert.Single(found);
        Assert.Equal(Sdl3MappingControlKind.Button, control.Kind);
        Assert.Equal(2, control.Index);
        Assert.Equal("b2", control.Token);

        // The same held button is not a second activation.
        Assert.Empty(Sdl3MappingActivation.Detect(pressed, pressed));
    }

    [Fact]
    public void An_Axis_That_Travels_Past_The_Deadzone_Is_Detected_With_Its_Direction()
    {
        var baseline = Snapshot(axes: [0, 0]);
        var pushed = Snapshot(axes: [-20000, 0]);

        var found = Sdl3MappingActivation.Detect(baseline, pushed);

        var control = Assert.Single(found);
        Assert.Equal(Sdl3MappingControlKind.Axis, control.Kind);
        Assert.Equal(0, control.Index);
        Assert.Equal(-1, control.AxisSign);

        // A resting wobble inside the deadzone is not an activation.
        Assert.Empty(Sdl3MappingActivation.Detect(baseline, Snapshot(axes: [300, 0])));
    }

    [Fact]
    public void A_Hat_Direction_Is_Detected_As_A_Hat_Token()
    {
        var baseline = Snapshot(hats: [0]);
        var up = Snapshot(hats: [Sdl3MappingDefaults.HatUp]);

        var found = Sdl3MappingActivation.Detect(baseline, up);

        var control = Assert.Single(found);
        Assert.Equal(Sdl3MappingControlKind.Hat, control.Kind);
        Assert.Equal("h0.1", control.Token);
    }

    [Fact]
    public void A_Hat_Value_Outside_The_Direction_Bitmask_Is_Not_A_Hat()
        => Assert.Empty(Sdl3MappingActivation.Detect(
            Snapshot(hats: [0]),
            Snapshot(hats: [0x80])));

    [Fact]
    public void A_Trigger_Step_Maps_An_Axis_As_A_Half_Axis_In_The_Direction_It_Travels()
    {
        var step = Step("lefttrigger");

        Assert.Equal("+a4", Sdl3MappingActivation.TokenFor(step, Axis(4, 1)));
        Assert.Equal("-a4", Sdl3MappingActivation.TokenFor(step, Axis(4, -1)));
    }

    [Fact]
    public void A_Button_Step_Bound_To_An_Axis_Also_Uses_A_Half_Axis()
    {
        // A d-pad reported as two axes: SDL reads a button from a half axis, so a whole-axis
        // token would report it as pressed at rest.
        Assert.Equal("+a0", Sdl3MappingActivation.TokenFor(Step("dpleft"), Axis(0, 1)));
        Assert.Equal("-a0", Sdl3MappingActivation.TokenFor(Step("dpright"), Axis(0, -1)));
    }

    [Fact]
    public void A_Stick_Step_Inverts_The_Axis_Only_When_It_Runs_Backwards()
    {
        // The flow asks for "left"/"up", which a normally-oriented stick reports as negative.
        Assert.Equal("a0", Sdl3MappingActivation.TokenFor(Step("leftx"), Axis(0, -1)));
        Assert.Equal("a0~", Sdl3MappingActivation.TokenFor(Step("leftx"), Axis(0, 1)));
        Assert.Equal("a1", Sdl3MappingActivation.TokenFor(Step("lefty"), Axis(1, -1)));
        Assert.Equal("a1~", Sdl3MappingActivation.TokenFor(Step("righty"), Axis(1, 1)));
    }

    [Fact]
    public void A_Button_Or_Hat_Control_Keeps_Its_Token_On_Every_Kind_Of_Step()
    {
        var button = new Sdl3MappingControl(Sdl3MappingControlKind.Button, 3, "b3");
        var hat = new Sdl3MappingControl(Sdl3MappingControlKind.Hat, 0, "h0.8");

        Assert.Equal("b3", Sdl3MappingActivation.TokenFor(Step("guide"), button));
        Assert.Equal("b3", Sdl3MappingActivation.TokenFor(Step("lefttrigger"), button));
        Assert.Equal("h0.8", Sdl3MappingActivation.TokenFor(Step("dpleft"), hat));
    }

    [Fact]
    public void Capturing_Advances_Through_The_Steps_In_Order()
    {
        var session = new Sdl3MappingCaptureSession();

        Assert.False(session.IsComplete);
        Assert.Equal("a", session.Current!.Key);

        Assert.True(session.TryCapture(Snapshot(buttons: [true])));

        Assert.Equal("b", session.Current!.Key);
        Assert.Equal("b0", session.TokenFor(Step("a")));
        Assert.Empty(session.Skipped);
    }

    [Fact]
    public void A_Stick_Step_Prefers_The_Axis_Over_A_Button_Brushed_At_The_Same_Time()
    {
        var session = new Sdl3MappingCaptureSession();
        while (session.Current!.Key != "leftx")
        {
            session.Skip();
        }

        session.BeginStep(Snapshot(buttons: [false], axes: [0, 0]));

        // A thumb that rests on a face key while pushing the stick must not bind the stick to it.
        Assert.True(session.TryCapture(Snapshot(buttons: [true], axes: [-20000, 0])));

        Assert.Equal("a0", session.TokenFor(Step("leftx")));
    }

    [Fact]
    public void A_Held_Control_Is_Not_Captured_Twice_Across_Steps()
    {
        var session = new Sdl3MappingCaptureSession();
        var rest = Snapshot(buttons: [false, false]);
        var held = Snapshot(buttons: [true, false]);

        session.BeginStep(rest);
        Assert.True(session.TryCapture(held));

        // The button is still down when the next step starts, so it is not a new activation.
        session.BeginStep(held);
        Assert.False(session.TryCapture(held), "已经按住的键不应再次计为新激活");

        Assert.Equal("b", session.Current!.Key);
        Assert.Single(session.Captured);
    }

    [Fact]
    public void A_Held_Hat_Is_Captured_Again_After_Release()
    {
        var session = new Sdl3MappingCaptureSession();
        while (session.Current!.Key != "dpdown")
        {
            session.Skip();
        }

        var held = Snapshot(hats: [Sdl3MappingDefaults.HatDown]);
        var released = Snapshot(hats: [0]);
        session.BeginStep(held);

        Assert.False(session.TryCapture(held));
        Assert.False(session.TryCapture(released));
        Assert.True(session.TryCapture(held));
        Assert.Equal("h0.4", session.TokenFor(Step("dpdown")));
    }

    [Fact]
    public void A_Held_Stick_Axis_Is_Captured_Again_After_Returning_To_Centre()
    {
        var session = new Sdl3MappingCaptureSession();
        while (session.Current!.Key != "leftx")
        {
            session.Skip();
        }

        var held = Snapshot(axes: [-20000]);
        var centre = Snapshot(axes: [0]);
        session.BeginStep(held);

        Assert.False(session.TryCapture(held));
        Assert.False(session.TryCapture(centre));
        Assert.True(session.TryCapture(held));
        Assert.Equal("a0", session.TokenFor(Step("leftx")));
    }

    [Fact]
    public void A_Different_Stick_Axis_Already_Active_At_The_Next_Step_Is_Accepted()
    {
        var session = new Sdl3MappingCaptureSession([Step("leftx"), Step("lefty")]);
        session.BeginStep(Snapshot(axes: [0, 0]));
        Assert.True(session.TryCapture(Snapshot(axes: [-20000, 0])));

        session.BeginStep(Snapshot(axes: [-20000, -20000]));

        Assert.True(session.TryCapture(Snapshot(axes: [-20000, -20000])));
        Assert.Equal("a1", session.TokenFor(Step("lefty")));
    }

    [Fact]
    public void Full_Negative_Travel_Is_Captured_On_Consecutive_Stick_Axes()
    {
        var session = new Sdl3MappingCaptureSession([Step("leftx"), Step("lefty")]);
        session.BeginStep(Snapshot(axes: [0, 0]));
        Assert.True(session.TryCapture(Snapshot(axes: [short.MinValue, 0])));

        session.BeginStep(Snapshot(axes: [short.MinValue, short.MinValue]));

        Assert.True(session.TryCapture(Snapshot(axes: [short.MinValue, short.MinValue])));
        Assert.Equal("a1", session.TokenFor(Step("lefty")));
    }

    [Fact]
    public void An_Unselected_Axis_Active_In_The_Previous_Frame_Does_Not_Fill_The_Next_Step()
    {
        var session = new Sdl3MappingCaptureSession([Step("lefty"), Step("rightx")]);
        session.BeginStep(Snapshot(axes: [0, 0, 0, 0]));
        Assert.True(session.TryCapture(Snapshot(axes: [0, short.MinValue, short.MinValue, 0])));

        session.BeginStep(Snapshot(axes: [0, short.MinValue, short.MinValue, 0]));

        Assert.False(session.TryCapture(Snapshot(axes: [0, short.MinValue, short.MinValue, 0])));
        Assert.True(session.TryCapture(Snapshot(axes: [0, short.MinValue, short.MinValue, short.MinValue])));
        Assert.Equal("a3", session.TokenFor(Step("rightx")));
    }

    [Fact]
    public void A_Different_Hat_Direction_Already_Active_At_The_Next_Step_Is_Accepted()
    {
        var session = new Sdl3MappingCaptureSession([Step("dpup"), Step("dpdown")]);
        session.BeginStep(Snapshot(hats: [0]));
        Assert.True(session.TryCapture(Snapshot(hats: [Sdl3MappingDefaults.HatUp])));

        session.BeginStep(Snapshot(hats: [Sdl3MappingDefaults.HatDown]));

        Assert.True(session.TryCapture(Snapshot(hats: [Sdl3MappingDefaults.HatDown])));
        Assert.Equal("h0.4", session.TokenFor(Step("dpdown")));
    }

    [Fact]
    public void Skipping_Records_The_Step_And_Advances()
    {
        var session = new Sdl3MappingCaptureSession();

        session.Skip();

        Assert.Equal("b", session.Current!.Key);
        Assert.Equal(Step("a"), Assert.Single(session.Skipped));
        Assert.Null(session.TokenFor(Step("a")));
        Assert.Contains(Step("a"), session.UncoveredRequired);
    }

    [Fact]
    public void Cancelling_Stops_The_Flow_Without_Recording_Anything_More()
    {
        var session = new Sdl3MappingCaptureSession();
        session.Skip();
        session.Cancel();

        Assert.True(session.IsCancelled);
        Assert.False(session.TryCapture(Snapshot(buttons: [true])));
        Assert.False(session.TryAssign(Step("b"), "b1"));
        session.Skip();

        Assert.Equal(1, session.Index);
        Assert.Single(session.Skipped);
    }

    [Fact]
    public void Every_Step_Is_Walked_When_Nothing_Is_Skipped()
    {
        var session = new Sdl3MappingCaptureSession();

        foreach (var _ in session.Steps)
        {
            Assert.True(
                session.TryCapture(Snapshot(buttons: [true], axes: [-20000, 20000], hats: [1])),
                session.Current?.Key);
            Assert.False(session.TryCapture(Snapshot(buttons: [false], axes: [0, 0], hats: [0])));
        }

        Assert.True(session.IsComplete);
        Assert.Empty(session.Skipped);
        Assert.Empty(session.UncoveredRequired);
        Assert.Equal(session.Steps.Count, session.Captured.Count);
    }

    [Fact]
    public void The_Mapping_Covers_The_Minimum_Control_Set()
    {
        var session = new Sdl3MappingCaptureSession();
        foreach (var step in session.Steps)
        {
            session.TryAssign(step, TokenForStep(step));
        }

        var mapping = session.BuildMapping(Guid, "Generic USB Joystick", "Linux");

        Assert.Contains("platform:Linux", mapping);
        foreach (var key in new[]
                 {
                     // SDL's own keys: the face buttons are a/b/x/y, never south/east/west/north.
                     "a", "b", "x", "y",
                     "dpup", "dpdown", "dpleft", "dpright",
                     "start", "back", "guide",
                     "leftshoulder", "rightshoulder", "leftstick", "rightstick",
                     "leftx", "lefty", "rightx", "righty",
                     "lefttrigger", "righttrigger",
                 })
        {
            Assert.Contains($"{key}:", mapping);
        }

        // A key SDL does not recognize is dropped without an error, so the mapping must not
        // carry one anywhere - a face button written as "south:b0" would silently do nothing.
        foreach (var positional in new[] { "south:", "east:", "west:", "north:" })
        {
            Assert.DoesNotContain(positional, mapping);
        }
    }

    [Fact]
    public void The_Mapping_Is_Valid_And_Skips_The_Steps_The_Device_Could_Not_Provide()
    {
        var session = new Sdl3MappingCaptureSession();
        foreach (var step in session.Steps)
        {
            if (step.Key == "touchpad")
            {
                session.Skip();
                continue;
            }

            session.TryAssign(step, TokenForStep(step));
        }

        var mapping = session.BuildMapping(Guid, "Generic USB Joystick", "Windows");

        // The last step was skipped, so it is absent from the mapping.
        Assert.DoesNotContain("touchpad:", mapping);

        var entry = new Sdl3MappingEntry
        {
            GuidString = Guid,
            DeviceName = "Generic USB Joystick",
            Mapping = mapping,
            UpdatedUtc = "2026-11-16T00:00:00.0000000Z",
        };
        Assert.Empty(Sdl3MappingFile.Validate(entry));
    }

    [Fact]
    public void DescribeProgress_Names_What_Is_Still_Missing()
    {
        var session = new Sdl3MappingCaptureSession();
        session.Skip();
        session.Skip();

        var progress = session.DescribeProgress();

        Assert.Contains("[1/21]", progress);
        Assert.Contains("面键 左", progress);

        while (!session.IsComplete)
        {
            session.Skip();
        }

        Assert.Contains("未捕获", session.DescribeProgress());
    }

    [Fact]
    public void The_Default_Step_Set_Marks_The_Minimum_Coverage_As_Required()
    {
        var steps = Sdl3MappingStep.CreateDefault();

        Assert.Equal(21, steps.Count(step => step.Required));
        Assert.Equal(6, steps.Count(step => !step.Required));
        Assert.All(steps, step => Assert.False(string.IsNullOrWhiteSpace(step.Key)));
        Assert.All(steps.Where(step => step.Required), step => Assert.False(string.IsNullOrWhiteSpace(step.Label)));
    }

    [Fact]
    public void A_Guide_Step_Does_Not_Accept_A_Hat()
    {
        var session = new Sdl3MappingCaptureSession();
        while (session.Current!.Key != "guide")
        {
            session.Skip();
        }

        session.BeginStep(Snapshot(hats: [Sdl3MappingDefaults.HatUp]));

        Assert.False(session.TryCapture(Snapshot(hats: [Sdl3MappingDefaults.HatUp])));
    }

    private static Sdl3MappingControl Axis(int index, int sign)
        => new(Sdl3MappingControlKind.Axis, index, $"a{index}") { AxisSign = sign };

    private static string TokenForStep(Sdl3MappingStep step) => step.Role switch
    {
        Sdl3MappingStepRole.Trigger => "+a5",
        Sdl3MappingStepRole.Stick => "a0",
        _ => "b0",
    };
}
