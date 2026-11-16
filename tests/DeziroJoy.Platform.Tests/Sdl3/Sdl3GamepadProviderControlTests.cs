using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Sdl3;
using Xunit;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// What the provider puts on the descriptor: the described controls, built once per open rather
/// than per tick, rebuilt when SDL reports a remap, and untouched by whatever the user is
/// pressing. Deterministic by construction - the only code that could talk to a real SDL runtime
/// lives behind the seam these tests replace.
/// </summary>
public sealed class Sdl3GamepadProviderControlTests : IDisposable
{
    private readonly FakeSdl3Api _api = new();
    private readonly Sdl3GamepadProvider _provider;

    public Sdl3GamepadProviderControlTests()
    {
        // Zero interval: every GetConnected scans, so a test can observe the effect of each
        // scripted change without racing the rate limiter.
        _provider = new Sdl3GamepadProvider(_api, TimeSpan.Zero);
    }

    public void Dispose() => _provider.Dispose();

    private GamepadDescriptor SingleDescriptor() => _provider.GetConnected().Single();

    [Fact]
    public void Get_Connected_Surfaces_The_Described_Controls()
    {
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, name: "Xbox 手柄", serial: "unit-1"));

        var descriptor = SingleDescriptor();

        Assert.Equal(
            [
                "左摇杆", "右摇杆",
                "十字键 上", "十字键 下", "十字键 左", "十字键 右",
                "A", "B", "X", "Y",
                "左肩键", "右肩键", "左扳机", "右扳机",
                "Back / Select", "Start / Options", "Guide / Home", "左摇杆按下", "右摇杆按下",
            ],
            descriptor.Controls.Select(control => control.Label));
    }

    [Fact]
    public void A_Device_Sdl_Declares_Nothing_For_Comes_Back_With_An_Empty_Description()
    {
        // Empty means "no declaration", which is a different statement from "no controls" - the
        // panel shows nothing rather than inventing a layout.
        _api.AddDevice(FakeSdl3Api.MakeDevice(
            instanceId: 1,
            declaredButtons: [],
            declaredAxes: [],
            declaredButtonCount: 0));

        Assert.Empty(SingleDescriptor().Controls);
    }

    [Fact]
    public void The_Description_Follows_The_Declaration_Not_The_Device_Name()
    {
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, name: "Unknown Device"));
        var unknown = SingleDescriptor();

        var device = _api.Devices[0];
        device.Info = device.Info with { Name = "Xbox Wireless Controller" };
        _api.Queue(Sdl3GamepadEventType.Remapped, 1);

        var renamed = SingleDescriptor();

        Assert.Equal(
            unknown.Controls.Select(control => control.Label),
            renamed.Controls.Select(control => control.Label));
    }

    [Fact]
    public void Controls_Are_Built_Once_Per_Open_Not_Per_Tick()
    {
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));

        var first = SingleDescriptor().Controls;
        var second = SingleDescriptor().Controls;
        var third = SingleDescriptor().Controls;

        // GetConnected runs every engine tick; rebuilding a description that cannot change
        // would allocate on every one of them.
        Assert.Same(first, second);
        Assert.Same(first, third);
    }

    [Fact]
    public void Pressing_Buttons_Does_Not_Change_The_Description()
    {
        var device = _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));
        var before = SingleDescriptor().Controls;

        device.Pressed.Add(SDL3.SDL.GamepadButton.South);
        device.Pressed.Add(SDL3.SDL.GamepadButton.LeftShoulder);
        device.Axes[(int)SDL3.SDL.GamepadAxis.LeftX] = 32767;

        _ = _provider.Poll(SingleDescriptor().DeviceId);

        // A declaration is not an observation: nothing the user does moves a described control,
        // and a control nobody has touched stays described and configurable.
        Assert.Equal(before, SingleDescriptor().Controls);
    }

    [Fact]
    public void A_Remap_Rebuilds_The_Description_From_The_New_Declaration()
    {
        var device = _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));
        var before = SingleDescriptor().Controls;

        device.Info = device.Info with
        {
            DeclaredButtons = ["a", "dpup"],
            DeclaredAxes = [],
        };
        _api.Queue(Sdl3GamepadEventType.Remapped, 1);

        var after = SingleDescriptor().Controls;

        Assert.Equal(["十字键 上", "A"], after.Select(control => control.Label));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Every_Described_Control_Is_Bindable()
    {
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));

        // A description that could not be bound to would be decoration.
        Assert.All(
            SingleDescriptor().Controls,
            control => Assert.True(
                control.Source is ButtonBinding or StickBinding,
                $"无法绑定的输入源：{control.Source.GetType().Name}"));
    }

    [Fact]
    public void A_Described_Stick_Click_And_A_Described_Stick_Are_Different_Controls()
    {
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));

        var controls = SingleDescriptor().Controls;

        var stick = controls.Single(control => control.Label == "左摇杆");
        var click = controls.Single(control => control.Label == "左摇杆按下");

        Assert.IsType<StickBinding>(stick.Source);
        Assert.Equal(GamepadControlKind.Direction, stick.Kind);
        Assert.Equal(GamepadControlZone.Directions, stick.Zone);

        Assert.IsType<ButtonBinding>(click.Source);
        Assert.Equal(GamepadButton.LeftStick, ((ButtonBinding)click.Source).Button);
        Assert.Equal(GamepadControlZone.Functions, click.Zone);
    }
}
