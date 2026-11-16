using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Windows.Capture;
using Xunit;

namespace DeziroJoy.Platform.Tests.Windows;

/// <summary>
/// The decision core of the global capture, exercised as a pure stream of raw key events - no
/// hook, no real keyboard, no process-global state. These pin the behaviour the editor relies on:
/// modifiers are swallowed and tracked, a combo is captured on the non-modifier press, the capture
/// ends when everything is released, Escape cancels, injected events are ignored, and an ended
/// session goes inert.
/// </summary>
public sealed class KeyboardCaptureSessionTests
{
    private const ushort KeyC = 0x43;

    private const ushort KeyV = 0x56;

    private static RawKeyEvent Down(ushort vk) => new(vk, IsKeyUp: false, IsInjected: false);

    private static RawKeyEvent Up(ushort vk) => new(vk, IsKeyUp: true, IsInjected: false);

    private static RawKeyEvent Injected(ushort vk, bool isUp) => new(vk, isUp, IsInjected: true);

    [Fact]
    public void A_Non_Modifier_Press_Is_Captured_With_The_Held_Modifiers()
    {
        var session = new KeyboardCaptureSession();

        var ctrl = session.Process(Down(WindowsKeyCapture.VkLControl));
        var press = session.Process(Down(KeyC));

        Assert.Equal(CaptureDecision.Swallow, ctrl.Decision);
        Assert.Null(ctrl.CapturedKey);

        Assert.Equal(CaptureDecision.Swallow, press.Decision);
        Assert.Equal(GamepadKey.C, press.CapturedKey);
        Assert.Equal(KeyModifiers.Ctrl, press.CapturedModifiers);
        Assert.False(press.Ended);
    }

    [Fact]
    public void A_Lone_Key_Ends_The_Capture_When_Released()
    {
        var session = new KeyboardCaptureSession();

        var press = session.Process(Down(KeyC));
        var release = session.Process(Up(KeyC));

        Assert.Equal(GamepadKey.C, press.CapturedKey);
        Assert.False(press.Ended);
        Assert.Equal(CaptureDecision.Swallow, release.Decision);
        Assert.True(release.Ended);
    }

    [Fact]
    public void The_Last_Key_Pressed_Wins_And_The_Capture_Waits_For_Every_Release()
    {
        var session = new KeyboardCaptureSession();

        var first = session.Process(Down(KeyC));
        var second = session.Process(Down(KeyV));
        var releaseFirst = session.Process(Up(KeyC));
        var releaseSecond = session.Process(Up(KeyV));

        Assert.Equal(GamepadKey.C, first.CapturedKey);
        Assert.Equal(GamepadKey.V, second.CapturedKey);
        Assert.False(releaseFirst.Ended);
        Assert.True(releaseSecond.Ended);
    }

    [Fact]
    public void A_Modifier_Does_Not_Have_To_Be_Released_For_The_Capture_To_End()
    {
        var session = new KeyboardCaptureSession();

        session.Process(Down(WindowsKeyCapture.VkLControl));
        session.Process(Down(KeyC));

        var releaseKey = session.Process(Up(KeyC));

        // The capture ends on the target's release even though Ctrl is still held, so a modifier
        // left down can never wedge the hook open.
        Assert.True(releaseKey.Ended);
    }

    [Fact]
    public void A_Lone_Modifier_Tap_Ends_The_Capture_Without_Capturing_Anything()
    {
        var session = new KeyboardCaptureSession();

        var press = session.Process(Down(WindowsKeyCapture.VkLShift));
        var release = session.Process(Up(WindowsKeyCapture.VkLShift));

        Assert.Equal(CaptureDecision.Swallow, press.Decision);
        Assert.Null(press.CapturedKey);
        Assert.True(release.Ended);
        Assert.Null(release.CapturedKey);
    }

    [Fact]
    public void Escape_Cancels_The_Capture_On_The_Way_Down()
    {
        var session = new KeyboardCaptureSession();

        var cancel = session.Process(Down(WindowsKeyCapture.VkEscape));

        Assert.Equal(CaptureDecision.Swallow, cancel.Decision);
        Assert.Null(cancel.CapturedKey);
        Assert.True(cancel.Ended);
    }

    [Fact]
    public void An_Unmapped_Key_Is_Swallowed_But_Neither_Captured_Nor_Counted()
    {
        var session = new KeyboardCaptureSession();

        var press = session.Process(Down(0x88));
        var release = session.Process(Up(0x88));

        Assert.Equal(CaptureDecision.Swallow, press.Decision);
        Assert.Null(press.CapturedKey);
        Assert.False(press.Ended);
        Assert.True(release.Ended);
    }

    [Fact]
    public void Injected_Events_Pass_Through_And_Do_Not_Affect_The_Capture()
    {
        var session = new KeyboardCaptureSession();

        var injectedDown = session.Process(Injected(KeyC, isUp: false));
        var injectedUp = session.Process(Injected(KeyC, isUp: true));

        Assert.Equal(CaptureDecision.PassThrough, injectedDown.Decision);
        Assert.Equal(CaptureDecision.PassThrough, injectedUp.Decision);
        Assert.Null(injectedDown.CapturedKey);

        // The injected pair left nothing held, so a real press still captures cleanly.
        var press = session.Process(Down(KeyC));
        Assert.Equal(GamepadKey.C, press.CapturedKey);
    }

    [Fact]
    public void A_Finished_Session_Is_Inert()
    {
        var session = new KeyboardCaptureSession();

        session.Process(Down(KeyC));
        session.Process(Up(KeyC));

        var after = session.Process(Down(KeyV));

        Assert.Equal(CaptureDecision.PassThrough, after.Decision);
        Assert.Null(after.CapturedKey);
        Assert.False(after.Ended);
    }

    [Fact]
    public void A_Modifier_Held_Before_Arming_Is_Seeded_Into_The_First_Combo()
    {
        var session = new KeyboardCaptureSession([WindowsKeyCapture.VkLControl]);

        var press = session.Process(Down(KeyC));

        Assert.Equal(GamepadKey.C, press.CapturedKey);
        Assert.Equal(KeyModifiers.Ctrl, press.CapturedModifiers);
    }

    [Fact]
    public void Seeding_Ignores_Anything_That_Is_Not_A_Modifier()
    {
        var session = new KeyboardCaptureSession([KeyC]);

        var press = session.Process(Down(KeyV));

        Assert.Equal(KeyModifiers.None, press.CapturedModifiers);
    }
}
