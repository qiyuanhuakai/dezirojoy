using DeziroJoy.App.Hosting;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.Tests.Ipc;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Hosting;

/// <summary>
/// The tray process owns no window and no view model, so
/// <see cref="TrayHost.DaemonScreenKeyboardFeed"/> is the seam that turns daemon snapshots and
/// input samples into the floating screen keyboard - and turns the keyboard's own presses back
/// into requests the daemon answers. The resident mode is where the keyboard matters most, so
/// the tray hosts it exactly as it hosts the layer hint overlay. These tests run the real
/// controller over a real server, the same way the layer hint feed's do.
/// </summary>
public sealed class DaemonScreenKeyboardFeedTests
{
    private static MappingEntry ScreenKeyboardKey(GamepadButton button)
        => new() { Source = new ButtonBinding(button), Target = new SpecialKeyTarget(SpecialKeyKind.Keyboard) };

    [Fact]
    public async Task The_Snapshot_Flag_Puts_The_Keyboard_On_Screen_And_Takes_The_Lease()
    {
        var context = await CreateFeed();
        using var feed = context.Feed;

        context.Server.RequestScreenKeyboardToggle();
        await WaitForCondition(() => context.Keyboard.IsOpen);

        // The lease is the same per-connection hijack lease the render window takes, so while
        // the keyboard is open the mapping stops and the pad drives the caps.
        await WaitForCondition(() => context.Host.IsInjectionSuspended);
    }

    [Fact]
    public async Task The_Pad_Drives_The_Keyboard_Instead_Of_The_Mapping()
    {
        var context = await CreateFeed();
        using var feed = context.Feed;

        context.Server.RequestScreenKeyboardToggle();
        await WaitForCondition(() => context.Keyboard.IsOpen);

        // The first sample of a device establishes its baseline, exactly as every other
        // navigation here does; the press is the edge that follows it.
        context.Host.PublishInputSample("pad-1", GamepadState.Neutral);
        context.Host.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.DPadRight, 0f, 0f, 0f, 0f));
        await WaitForCondition(() => context.Keyboard.Keyboard.FocusedKey?.Label == "b");
    }

    [Fact]
    public async Task The_Key_That_Summoned_The_Keyboard_Also_Puts_It_Away()
    {
        var context = await CreateFeed();
        using var feed = context.Feed;

        // The profile carries the keyboard key on Back, so the engine is suspended while the
        // keyboard is open and the window has to answer the press itself.
        context.Server.RequestScreenKeyboardToggle();
        await WaitForCondition(() => context.Keyboard.IsOpen);

        context.Host.PublishInputSample("pad-1", GamepadState.Neutral);
        context.Host.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.Back, 0f, 0f, 0f, 0f));
        await WaitForCondition(() => !context.Keyboard.IsOpen);

        await WaitForCondition(() => !context.Host.IsInjectionSuspended);

        // The flag the daemon holds moved with it, so the next engine toggle opens rather than
        // closes - the window never hid a keyboard the daemon still believes is open.
        context.Server.RequestScreenKeyboardToggle();
        await WaitForCondition(() => context.Keyboard.IsOpen);
    }

    [Fact]
    public async Task What_The_Keyboard_Types_Is_Asked_Of_The_Daemon()
    {
        var context = await CreateFeed();
        using var feed = context.Feed;

        context.Server.RequestScreenKeyboardToggle();
        await WaitForCondition(() => context.Keyboard.IsOpen);

        // A presses the key the cursor is on: the daemon is the sole injection authority, so
        // the request travels over the same connection the feed already owns.
        context.Keyboard.Navigate(GamepadButtonMask.A);
        await WaitForCondition(() => context.Host.InjectedTexts.Count > 0);

        Assert.Equal(["a"], context.Host.InjectedTexts);

        // 退格 is a key tap rather than text.
        context.Keyboard.Keyboard.CursorRow = context.Keyboard.Keyboard.ActivePage.Rows.Count - 1;
        context.Keyboard.Keyboard.CursorColumn = 0;
        context.Keyboard.Navigate(GamepadButtonMask.A);
        await WaitForCondition(() => context.Host.TappedKeys.Count > 0);

        Assert.Equal([(GamepadKey.Backspace, KeyModifiers.None)], context.Host.TappedKeys);
    }

    [Fact]
    public async Task Losing_The_Daemon_Takes_The_Keyboard_Away()
    {
        var context = await CreateFeed();
        var feed = context.Feed;

        context.Server.RequestScreenKeyboardToggle();
        await WaitForCondition(() => context.Keyboard.IsOpen);

        await context.Channel.DisposeAsync();
        await WaitForCondition(() => !context.Keyboard.IsOpen);

        feed.Dispose();
    }

    private static async Task WaitForCondition(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private static async Task<FeedContext> CreateFeed()
    {
        var profile = new GamepadProfile
        {
            Name = "screen-keyboard",
            DeviceId = "pad-1",
            Mappings = [ScreenKeyboardKey(GamepadButton.Back)],
        };

        var host = new FakeDaemonHost { HasController = true };
        host.AddDevice("pad-1");
        host.Reload("pad-1", profile);
        var store = new InMemoryProfileStore();
        var statePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"dezirojoy-tray-keyboard-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(host, store, new DaemonStateStore(statePath));
        var channel = new FeedServerChannel(server);
        server.StateChanged += channel.Push;
        server.InputSampled += channel.Push;
        var controller = new DaemonController(new FeedServerEndpoint(channel));
        await controller.ConnectAsync(TestContext.Current.CancellationToken);

        var keyboard = new ScreenKeyboardViewModel();
        var feed = new TrayHost.DaemonScreenKeyboardFeed(controller, keyboard);
        return new FeedContext(controller, server, feed, host, profile, channel, keyboard);
    }

    private sealed record FeedContext(
        DaemonController Controller,
        DaemonServer Server,
        TrayHost.DaemonScreenKeyboardFeed Feed,
        FakeDaemonHost Host,
        GamepadProfile Profile,
        FeedServerChannel Channel,
        ScreenKeyboardViewModel Keyboard);

    /// <summary>A ready endpoint over one in-memory channel, so no pipe is opened.</summary>
    private sealed class FeedServerEndpoint(FeedServerChannel channel) : IDaemonEndpoint
    {
        public Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
            => Task.FromResult<IDaemonChannel>(channel);
    }

    /// <summary>
    /// One client side of the daemon server: answers are produced synchronously on send and
    /// pushed broadcasts queue behind them, exactly like the named-pipe transport delivers them
    /// in order.
    /// </summary>
    private sealed class FeedServerChannel(DaemonServer server) : IDaemonChannel
    {
        private readonly object _gate = new();
        private readonly Queue<IpcMessage> _incoming = new();
        private TaskCompletionSource<IpcMessage>? _waiting;
        private bool _closed;

        public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            var answer = await server.ProcessAsync(message);
            Push(answer);
        }

        public Task<IpcMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_incoming.Count > 0)
                {
                    return Task.FromResult(_incoming.Dequeue());
                }

                if (_closed)
                {
                    return Task.FromException<IpcMessage>(new EndOfStreamException());
                }

                _waiting ??= new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                return AwaitReceiveAsync(_waiting.Task, cancellationToken);
            }
        }

        public void Push(IpcMessage message)
        {
            lock (_gate)
            {
                _incoming.Enqueue(message);
                _waiting?.TrySetResult(message);
                _waiting = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _closed = true;
                _waiting?.TrySetException(new EndOfStreamException());
                _waiting = null;
            }

            return ValueTask.CompletedTask;
        }

        private async Task<IpcMessage> AwaitReceiveAsync(Task<IpcMessage> wait, CancellationToken cancellationToken)
        {
            await wait.WaitAsync(cancellationToken);
            return await ReceiveAsync(cancellationToken);
        }
    }
}
