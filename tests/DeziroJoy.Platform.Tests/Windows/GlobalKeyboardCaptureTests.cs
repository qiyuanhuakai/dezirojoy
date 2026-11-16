using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Windows.Capture;
using Xunit;

namespace DeziroJoy.Platform.Tests.Windows;

/// <summary>
/// The orchestrator that binds a <see cref="KeyboardCaptureSession"/> to a
/// <see cref="ILowLevelKeyboardHook"/>, driven here by a fake hook so no real keyboard and no
/// process-global hook state is involved. The behaviour under test is lifecycle: idempotent
/// arm/disarm, forwarding the captured combo, ending on release or Escape, dropping the handler
/// the instant the capture ends, and - the deadlock-avoidance that matters on Windows - never
/// calling <c>UnhookWindowsHookEx</c> from inside the hook callback, but queueing it instead.
/// </summary>
public sealed class GlobalKeyboardCaptureTests
{
    private const ushort KeyC = 0x43;

    private static RawKeyEvent Down(ushort vk) => new(vk, IsKeyUp: false, IsInjected: false);

    private static RawKeyEvent Up(ushort vk) => new(vk, IsKeyUp: true, IsInjected: false);

    [Fact]
    public void Arming_Installs_The_Hook_Once_And_Is_Idempotent()
    {
        var hook = new FakeLowLevelKeyboardHook();
        using var capture = new GlobalKeyboardCapture(hook);

        capture.Arm();
        capture.Arm();

        Assert.Equal(1, hook.InstallCalls);
        Assert.True(hook.IsInstalled);
        Assert.NotNull(hook.Handler);
    }

    [Fact]
    public void Disarming_Uninstalls_The_Hook_Once_And_Is_Idempotent()
    {
        var hook = new FakeLowLevelKeyboardHook();
        using var capture = new GlobalKeyboardCapture(hook);

        capture.Arm();
        capture.Disarm();
        capture.Disarm();

        Assert.Equal(1, hook.UninstallCalls);
        Assert.False(hook.IsInstalled);
        Assert.Null(hook.Handler);
    }

    [Fact]
    public void A_Combo_Is_Forwarded_And_The_Hook_Is_Uninstalled_Only_After_The_Callback_Returns()
    {
        var queue = new Queue<Action>();
        var hook = new FakeLowLevelKeyboardHook();
        using var capture = new GlobalKeyboardCapture(hook, action => queue.Enqueue(action));

        GamepadKey? capturedKey = null;
        KeyModifiers capturedModifiers = default;
        var ended = false;
        capture.KeyCaptured += (key, modifiers) => { capturedKey = key; capturedModifiers = modifiers; };
        capture.CaptureEnded += () => ended = true;

        capture.Arm();

        Assert.True(hook.Feed(Down(WindowsKeyCapture.VkLControl)));
        Assert.True(hook.Feed(Down(KeyC)));
        Assert.Equal(GamepadKey.C, capturedKey);
        Assert.Equal(KeyModifiers.Ctrl, capturedModifiers);
        Assert.False(ended);

        // Releasing the target ends the capture. The handler is dropped at once so nothing further
        // is swallowed, but the unhook itself is queued rather than run on the callback's stack.
        Assert.True(hook.Feed(Up(KeyC)));
        Assert.True(ended);
        Assert.Null(hook.Handler);
        Assert.Equal(0, hook.UninstallCalls);
        Assert.Single(queue);

        queue.Dequeue()();
        Assert.Equal(1, hook.UninstallCalls);
        Assert.False(hook.IsInstalled);
    }

    [Fact]
    public void An_Ended_Capture_Stops_Swallowing_Before_The_Hook_Is_Uninstalled()
    {
        var queue = new Queue<Action>();
        var hook = new FakeLowLevelKeyboardHook();
        using var capture = new GlobalKeyboardCapture(hook, action => queue.Enqueue(action));

        capture.Arm();
        hook.Feed(Down(KeyC));
        hook.Feed(Up(KeyC));

        // The handler is gone, so a trailing event passes through instead of being swallowed.
        Assert.False(hook.Feed(Down(0x56)));
    }

    [Fact]
    public void A_Modifier_Is_Swallowed_But_An_Injected_Event_Is_Not()
    {
        var hook = new FakeLowLevelKeyboardHook();
        using var capture = new GlobalKeyboardCapture(hook);

        capture.Arm();

        Assert.True(hook.Feed(Down(WindowsKeyCapture.VkLControl)));
        Assert.False(hook.Feed(new RawKeyEvent(KeyC, IsKeyUp: false, IsInjected: true)));
    }

    [Fact]
    public void A_Modifier_Held_At_Arm_Time_Is_Seeded_Into_The_First_Combo()
    {
        var hook = new FakeLowLevelKeyboardHook { HeldModifiers = [WindowsKeyCapture.VkLShift] };
        using var capture = new GlobalKeyboardCapture(hook);

        KeyModifiers capturedModifiers = default;
        capture.KeyCaptured += (_, modifiers) => capturedModifiers = modifiers;

        capture.Arm();
        hook.Feed(Down(KeyC));

        Assert.Equal(KeyModifiers.Shift, capturedModifiers);
    }

    [Fact]
    public void A_Refused_Install_Ends_The_Capture_Instead_Of_Leaving_It_Waiting()
    {
        var hook = new FakeLowLevelKeyboardHook { InstallResult = false };
        using var capture = new GlobalKeyboardCapture(hook);

        var ended = false;
        capture.CaptureEnded += () => ended = true;

        capture.Arm();

        Assert.True(ended);
        Assert.False(hook.IsInstalled);
        Assert.Null(hook.Handler);
    }

    private sealed class FakeLowLevelKeyboardHook : ILowLevelKeyboardHook
    {
        public bool InstallResult { get; init; } = true;

        public IReadOnlyCollection<ushort> HeldModifiers { get; init; } = [];

        public int InstallCalls { get; private set; }

        public int UninstallCalls { get; private set; }

        public bool IsInstalled { get; private set; }

        public Func<RawKeyEvent, bool>? Handler { get; set; }

        public bool Install()
        {
            InstallCalls++;
            if (!InstallResult)
            {
                return false;
            }

            IsInstalled = true;
            return true;
        }

        public void Uninstall()
        {
            UninstallCalls++;
            IsInstalled = false;
        }

        public IReadOnlyCollection<ushort> ReadHeldModifiers() => HeldModifiers;

        /// <summary>Feeds one event through the handler the orchestrator installed, returning its swallow decision.</summary>
        public bool Feed(RawKeyEvent e) => Handler?.Invoke(e) ?? false;
    }
}
