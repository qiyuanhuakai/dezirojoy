using DeziroJoy.Platform.Sdl3;
using Xunit;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// The plain-joystick scan and the interactive capture loop, both over the scripted
/// <see cref="FakeSdl3Api"/>. The scan proves a device with no standard mapping is listed - with
/// the evidence a mapping is written against - while a device SDL already reads as a gamepad is
/// not offered. The runner proves the loop's own rules: a held control is not captured twice,
/// skipping records a gap, and cancelling saves nothing.
/// </summary>
public sealed class Sdl3JoystickCatalogTests
{
    [Fact]
    public void Only_Joysticks_Without_A_Standard_Mapping_Are_Listed_As_Unmapped()
    {
        var api = new FakeSdl3Api();
        var unmapped = api.AddJoystick(FakeSdl3Api.FakeJoystick.Make(instanceId: 4, buttons: 12, axes: 6, hats: 1));
        var alreadyMapped = api.AddJoystick(FakeSdl3Api.FakeJoystick.Make(instanceId: 5, buttons: 15, axes: 6, hats: 1));
        api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 5, name: "Xbox 手柄", serial: "unit-5"));

        var scan = Sdl3JoystickCatalog.Scan(api);

        Assert.True(scan.Initialized);
        Assert.Equal(2, scan.Joysticks.Count);
        Assert.Equal(1, scan.MappedAsGamepadCount);
        var single = Assert.Single(scan.Unmapped);
        Assert.Equal(unmapped.InstanceId, single.InstanceId);
        Assert.DoesNotContain(scan.Unmapped, joystick => joystick.InstanceId == alreadyMapped.InstanceId);
        Assert.Equal(12, single.ButtonCount);
        Assert.Equal(6, single.AxisCount);
        Assert.Equal(1, single.HatCount);
    }

    [Fact]
    public void The_Scan_Reports_The_Evidence_A_Mapping_Is_Written_Against()
    {
        var api = new FakeSdl3Api();
        var joystick = FakeSdl3Api.FakeJoystick.Make(instanceId: 9, buttons: 4, axes: 3, hats: 0);
        joystick.Serial = "SN-1234";
        joystick.Path = "/dev/input/event12";
        joystick.Vendor = 0x0079;
        joystick.Product = 0x0006;
        joystick.ProductVersion = 0x0100;
        api.AddJoystick(joystick);

        var single = Assert.Single(Sdl3JoystickCatalog.Scan(api).Unmapped);

        Assert.Equal("03000000790000000006000000000000", single.GuidString);
        Assert.Equal("Generic USB Joystick", single.Name);
        Assert.Equal(0x0079, single.Vendor);
        Assert.Equal(0x0006, single.Product);
        Assert.Equal("SN-1234", single.Serial);
        Assert.Equal("/dev/input/event12", single.Path);
        Assert.Equal(4, single.ButtonCount);
        Assert.Equal(3, single.AxisCount);
    }

    [Fact]
    public void An_Uninitializable_Runtime_Reports_The_Reason_And_Lists_Nothing()
    {
        var api = new FakeSdl3Api { InitSucceeds = false, InitError = "找不到 libSDL3.so" };

        var scan = Sdl3JoystickCatalog.Scan(api);

        Assert.False(scan.Initialized);
        Assert.Empty(scan.Unmapped);
        Assert.Contains("libSDL3.so", scan.Hint);
    }

    [Fact]
    public void Opening_A_Missing_Joystick_Reports_The_Reason_Instead_Of_Throwing()
    {
        var api = new FakeSdl3Api();
        api.JoystickOpenFailures[7] = "设备忙";

        var result = api.OpenJoystick(7, out var error);

        Assert.Null(result);
        Assert.Equal("设备忙", error);
    }

    [Fact]
    public void Reading_A_Joystick_Returns_Its_Raw_Control_State()
    {
        var api = new FakeSdl3Api();
        var joystick = api.AddJoystick(FakeSdl3Api.FakeJoystick.Make(instanceId: 3, buttons: 3, axes: 2, hats: 1));

        var handle = api.OpenJoystick(3, out _)!;
        joystick.Press(1);
        joystick.Move(0, -20000);
        joystick.Turn(0, Sdl3MappingDefaults.HatLeft);

        var sample = api.ReadJoystick(handle);

        Assert.Equal([false, true, false], sample.Buttons);
        Assert.Equal([-20000, (short)0], sample.Axes);
        Assert.Equal([Sdl3MappingDefaults.HatLeft], sample.Hats);
    }

    [Fact]
    public void A_Sample_Pumps_Sdl_Before_Reading_The_Cached_Joystick_State()
    {
        // SDL's raw joystick getters serve the state the driver last wrote, so a reader that
        // does not pump would sample the values from when the device was opened forever - the
        // capture flow would wait for a button that was already pressed.
        var api = new FakeSdl3Api();
        api.AddJoystick(FakeSdl3Api.FakeJoystick.Make(instanceId: 3, buttons: 1, axes: 1, hats: 0));
        var handle = api.OpenJoystick(3, out _)!;
        using var reader = new Sdl3JoystickReader(api, handle);

        Assert.Equal(0, api.UpdateGamepadsCalls);

        var sample = reader.Sample();

        Assert.Equal(1, api.UpdateGamepadsCalls);
        Assert.Single(sample.Buttons);
        Assert.Single(sample.Axes);

        reader.Sample();

        Assert.Equal(2, api.UpdateGamepadsCalls);
    }

    [Fact]
    public void A_Sample_After_Dispose_Throws_Rather_Than_Reading_A_Closed_Device()
    {
        var api = new FakeSdl3Api();
        api.AddJoystick(FakeSdl3Api.FakeJoystick.Make(instanceId: 3, buttons: 1, axes: 1, hats: 0));
        var handle = api.OpenJoystick(3, out _)!;
        var reader = new Sdl3JoystickReader(api, handle);

        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => reader.Sample());
    }

    [Fact]
    public void Closing_A_Joystick_Twice_Closes_It_Once()
    {
        var api = new FakeSdl3Api();
        api.AddJoystick(FakeSdl3Api.FakeJoystick.Make(instanceId: 3, buttons: 1, axes: 1, hats: 0));

        var handle = api.OpenJoystick(3, out _)!;

        api.CloseJoystick(handle);
        api.CloseJoystick(handle);

        Assert.Equal(1, api.JoystickCloseCount(handle));
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void A_Closed_Joystick_Reads_As_Empty_Rather_Than_Throwing()
    {
        var api = new FakeSdl3Api();
        api.AddJoystick(FakeSdl3Api.FakeJoystick.Make(instanceId: 3, buttons: 1, axes: 1, hats: 0));
        var handle = api.OpenJoystick(3, out _)!;

        api.CloseJoystick(handle);

        Assert.Equal(Sdl3JoystickSnapshot.Empty, api.ReadJoystick(handle));
    }
}

/// <summary>
/// Scripted stand-ins for the capture loop's two seams: a joystick whose state is a queue of
/// frames, and an operator whose commands are a queue of keystrokes. Neither blocks, so the
/// runner's polling loop runs to completion inside a test.
/// </summary>
internal sealed class ScriptedJoystickReader : ISdl3JoystickReader
{
    private readonly Queue<Sdl3JoystickSnapshot> _frames;

    public ScriptedJoystickReader(IEnumerable<Sdl3JoystickSnapshot> frames)
        => _frames = new Queue<Sdl3JoystickSnapshot>(frames);

    public int Samples { get; private set; }

    public Sdl3JoystickSnapshot Sample()
    {
        Samples++;
        return _frames.Count > 0 ? _frames.Dequeue() : Sdl3JoystickSnapshot.Empty;
    }

    public void Dispose()
    {
    }
}

internal sealed class ScriptedCaptureInput : ISdl3MappingCaptureInput
{
    private readonly Queue<char> _commands;

    public ScriptedCaptureInput(IEnumerable<char> commands)
        => _commands = new Queue<char>(commands);

    public char? TryReadCommand() => _commands.Count > 0 ? _commands.Dequeue() : null;
}

/// <summary>
/// The interactive capture loop itself: it takes a baseline per step, samples until a control
/// activates, and honours the operator's skip and cancel commands.
/// </summary>
public sealed class Sdl3MappingCaptureRunnerTests
{
    [Fact]
    public void The_Loop_Captures_Each_Step_From_The_Scripted_Frames()
    {
        var steps = Sdl3MappingStep.CreateDefault().Take(3).ToList();
        var session = new Sdl3MappingCaptureSession(steps);
        var reader = new ScriptedJoystickReader(
        [
            Snapshot(buttons: [false]),
            Snapshot(buttons: [true]),
            Snapshot(buttons: [false]),
            Snapshot(buttons: [true]),
            Snapshot(buttons: [false]),
            Snapshot(buttons: [true]),
        ]);

        var outcome = Sdl3MappingCaptureRunner.Run(
            session,
            reader,
            new ScriptedCaptureInput([]),
            TimeSpan.Zero);

        Assert.True(outcome.Completed);
        Assert.False(outcome.Cancelled);
        Assert.Equal(3, outcome.CapturedCount);
        Assert.Equal(0, outcome.SkippedCount);
        Assert.Equal(["b0", "b0", "b0"], steps.Select(step => session.TokenFor(step)));
    }

    [Fact]
    public void A_Skip_Command_Advances_The_Step_And_Records_The_Gap()
    {
        var steps = Sdl3MappingStep.CreateDefault().Take(2).ToList();
        var session = new Sdl3MappingCaptureSession(steps);

        var outcome = Sdl3MappingCaptureRunner.Run(
            session,
            new ScriptedJoystickReader(
            [
                Snapshot(buttons: [false]),
                Snapshot(buttons: [true]),
            ]),
            new ScriptedCaptureInput(['s']),
            TimeSpan.Zero);

        Assert.True(outcome.Completed);
        Assert.Equal(1, outcome.SkippedCount);
        Assert.Equal(1, outcome.CapturedCount);
        Assert.Equal("b0", session.TokenFor(steps[0]));
        Assert.Null(session.TokenFor(steps[1]));
        Assert.Equal(steps[1], Assert.Single(session.Skipped));
    }

    [Fact]
    public void A_Cancel_Command_Abandons_The_Flow_And_Leaves_Nothing_Captured()
    {
        var steps = Sdl3MappingStep.CreateDefault().Take(5).ToList();
        var session = new Sdl3MappingCaptureSession(steps);

        var outcome = Sdl3MappingCaptureRunner.Run(
            session,
            new ScriptedJoystickReader([Snapshot()]),
            new ScriptedCaptureInput(['q']),
            TimeSpan.Zero);

        Assert.True(outcome.Cancelled);
        Assert.False(outcome.Completed);
        Assert.Empty(session.Captured);
        Assert.Equal(0, session.Index);
    }

    [Fact]
    public void A_Control_Already_Held_Is_Never_Captured()
    {
        var steps = Sdl3MappingStep.CreateDefault().Take(2).ToList();
        var session = new Sdl3MappingCaptureSession(steps);

        var outcome = Sdl3MappingCaptureRunner.Run(
            session,
            new ScriptedJoystickReader(
            [
                Snapshot(buttons: [true]),
                Snapshot(buttons: [true]),
                Snapshot(buttons: [true]),
            ]),
            new ScriptedCaptureInput(['s', 's']),
            TimeSpan.Zero);

        Assert.True(outcome.Completed);
        Assert.Equal(0, outcome.CapturedCount);
        Assert.Equal(2, outcome.SkippedCount);
        Assert.All(steps, step => Assert.Null(session.TokenFor(step)));
    }

    private static Sdl3JoystickSnapshot Snapshot(bool[]? buttons = null, short[]? axes = null)
        => new(buttons ?? [], axes ?? [], []);
}
