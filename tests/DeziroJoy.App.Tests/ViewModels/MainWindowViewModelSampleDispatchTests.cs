using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.Tests.Ipc;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The coalesced daemon-sample dispatch (roadmap P3). The daemon's input samples arrive on its
/// receive loop faster than the window refreshes; posting every one of them across is what
/// filled the UI queue with work the next frame would supersede anyway. With a frame clock
/// wired, the receive loop keeps only the latest sample per device and one dispatch drains it
/// per UI frame - while the sensitivity panels' real-input calibration, the one consumer that
/// needs the complete stream, keeps taking every sample on its own path.
/// <para>
/// Every test runs the real controller over the in-memory server channel and drives the frame
/// clock by hand, so "one dispatch per frame" is observable rather than inferred: the
/// visualizer's stick frame is published as one notification per applied sample, which makes
/// the count of applied samples readable.
/// </para>
/// </summary>
public sealed class MainWindowViewModelSampleDispatchTests
{
    /// <summary>The ramped left-stick position a raw 0.5 push reports; the shared preview deadzone's own arithmetic.</summary>
    private const double RampedHalf = 0.4117647;

    /// <summary>The ramped position a raw 0.3 push reports - below the navigation threshold, so it navigates nowhere.</summary>
    private const double RampedThreeTenths = 0.1764706;

    /// <summary>The ramped position a raw 0.4 push reports.</summary>
    private const double RampedFourTenths = 0.2941176;

    [Fact]
    public async Task A_Frames_Worth_Of_Samples_Dispatches_Once_With_The_Latest_State()
    {
        var context = await CreateViewModel();
        await using var viewModel = context.ViewModel;

        var stickFrames = new List<double>();
        viewModel.Visualizer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == string.Empty)
            {
                stickFrames.Add(viewModel.Visualizer.LeftStickX);
            }
        };

        // Four samples inside one frame: the neutral baseline, then three distinct pushes.
        // The intermediate ones are superseded before any dispatch, so the UI never sees them.
        context.DaemonHost.PublishInputSample("pad-1", GamepadState.Neutral);
        context.DaemonHost.PublishInputSample("pad-1", GamepadState.Create(default, 0.1f, 0, 0, 0));
        context.DaemonHost.PublishInputSample("pad-1", GamepadState.Create(default, 0.3f, 0, 0, 0));
        context.DaemonHost.PublishInputSample("pad-1", GamepadState.Create(default, 0.5f, 0, 0, 0));
        await WaitForSample(context.Controller, "pad-1", rightStickX: 0f, leftStickX: 0.5f);

        // Nothing has been dispatched yet: no tick, no UI update.
        Assert.Empty(stickFrames);
        Assert.Equal(0, viewModel.Visualizer.LeftStickX);

        context.Clock.RaiseTick();

        // One dispatch for the whole frame, and it carries the latest sample - not the first,
        // and not one per sample.
        Assert.Equal(RampedHalf, Assert.Single(stickFrames), 6);
        Assert.Equal(RampedHalf, viewModel.Visualizer.LeftStickX, 6);
    }

    [Fact]
    public async Task Every_Device_Keeps_Its_Own_Latest_Sample_Inside_One_Frame()
    {
        // A single "latest sample" slot would let the second pad's sample overwrite the first
        // pad's, and the first pad's press would never navigate. The queue is keyed by device,
        // so both pads' samples survive the frame - and they are applied in arrival order, so
        // the pad that sampled most recently is the one the window is scoped to afterwards.
        var context = await CreateViewModel(secondDevice: true);
        await using var viewModel = context.ViewModel;
        viewModel.SetGamepadNavigationEnabled(true);

        context.DaemonHost.PublishInputSample("pad-1", GamepadState.Neutral);
        context.DaemonHost.PublishInputSample("pad-2", GamepadState.Neutral);
        await WaitForSample(context.Controller, "pad-2", leftStickX: 0f);
        context.Clock.RaiseTick();

        // One frame, two devices: pad-1's press and pad-2's sub-threshold stick push.
        context.DaemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));
        context.DaemonHost.PublishInputSample("pad-2", GamepadState.Create(default, 0.3f, 0, 0, 0));
        await WaitForSample(context.Controller, "pad-2", leftStickX: 0.3f);
        context.Clock.RaiseTick();

        // Pad-1's press navigated even though pad-2 sampled after it inside the same frame.
        Assert.Equal(new ButtonBinding(GamepadButton.A), viewModel.SelectedSource);

        // Pad-2's sample was applied too, and it was the last one applied: the diagram shows
        // the pad that sampled most recently, exactly as the per-sample path's last update won.
        Assert.Equal(RampedThreeTenths, viewModel.Visualizer.LeftStickX, 6);
    }

    [Fact]
    public async Task The_Calibration_Keeps_The_Complete_Sample_Stream_While_The_Display_Coalesces()
    {
        // The panels' real-input readouts are built from every sample, so a calibrating panel
        // takes the samples one by one - the frame coalescing is the display path's alone.
        var context = await CreateViewModel();
        await using var viewModel = context.ViewModel;
        viewModel.MouseSensitivity.IsRealInputMode = true;
        viewModel.ScrollSensitivity.IsRealInputMode = true;

        var calibration = new List<double>();
        viewModel.MouseSensitivity.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MouseSensitivityViewModel.RawStickX))
            {
                calibration.Add(viewModel.MouseSensitivity.RawStickX);
            }
        };

        var stickFrames = new List<double>();
        viewModel.Visualizer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == string.Empty)
            {
                stickFrames.Add(viewModel.Visualizer.RightStickX);
            }
        };

        for (var value = 0.1f; value <= 0.4f; value += 0.1f)
        {
            context.DaemonHost.PublishInputSample("pad-1", GamepadState.Create(default, 0, 0, value, 0));
        }

        await WaitForSample(context.Controller, "pad-1", rightStickX: 0.4f);

        // Every sample reached the panel - four readouts, not the frame's last one - before a
        // single frame was dispatched. The values round-trip through the protocol's float, so
        // they are compared as floats.
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f], calibration.Select(value => (float)value));
        Assert.Equal(0.4f, (float)viewModel.MouseSensitivity.RawStickX);

        context.Clock.RaiseTick();

        // The display path still coalesced the very same frame into one dispatch.
        Assert.Equal(RampedFourTenths, Assert.Single(stickFrames), 6);
    }

    [Fact]
    public async Task Closing_The_Window_Stops_The_Clock_And_Drops_The_Undispatched_Samples()
    {
        var context = await CreateViewModel();
        var viewModel = context.ViewModel;
        viewModel.SetGamepadNavigationEnabled(true);

        context.DaemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));
        await WaitForSample(context.Controller, "pad-1", pressed: GamepadButtonMask.A);

        await viewModel.DisposeAsync();

        // The clock was released and whatever the last frames never dispatched is gone with it:
        // a tick after the close applies nothing, navigates nowhere and touches no visual.
        Assert.True(context.Clock.StopCalls >= 1);
        Assert.False(context.Clock.IsRunning);
        Assert.Null(viewModel.SelectedSource);
        Assert.False(viewModel.Visualizer.IsAPressed);

        context.Clock.RaiseTick();

        Assert.Null(viewModel.SelectedSource);
        Assert.False(viewModel.Visualizer.IsAPressed);
    }

    [Fact]
    public async Task Losing_The_Daemon_Releases_The_Clock_And_Drops_The_Queued_Samples()
    {
        var context = await CreateViewModel();
        await using var viewModel = context.ViewModel;

        context.DaemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));
        await WaitForSample(context.Controller, "pad-1", pressed: GamepadButtonMask.A);

        // The daemon goes away while the sample is still waiting for its frame. The wait is on
        // the window's own handling of the loss - the snapshot going null, which the handler
        // clears after it stops the clock - rather than on the controller's state, which flips
        // a moment before the handler runs.
        await context.Channel.DisposeAsync();
        await WaitForCondition(() => viewModel.DaemonSnapshot is null);

        Assert.True(context.Clock.StopCalls >= 1);
        Assert.False(context.Clock.IsRunning);

        // The queued sample belonged to the connection that just went: the frame that runs
        // anyway paints nothing.
        context.Clock.RaiseTick();

        Assert.False(viewModel.Visualizer.IsAPressed);
    }

    [Fact]
    public async Task A_Reconnected_Daemon_Starts_The_Clock_Again_And_Its_Samples_Flow()
    {
        var context = await CreateViewModel();
        await using var viewModel = context.ViewModel;

        await context.Channel.DisposeAsync();
        await WaitForCondition(() => viewModel.DaemonSnapshot is null);
        var startsAfterDisconnect = context.Clock.StartCalls;

        // The replacement daemon serves the same endpoint, so the controller's own reconnect
        // lands on it - the same path a supervised restart takes.
        context.Endpoint.Channel = new MainWindowViewModelDaemonTests.ServerChannel(context.Server);
        context.Server.StateChanged += context.Endpoint.Channel.Push;
        context.Server.InputSampled += context.Endpoint.Channel.Push;
        await WaitForCondition(() => viewModel.IsDaemonConnected);

        Assert.Equal(startsAfterDisconnect + 1, context.Clock.StartCalls);
        Assert.True(context.Clock.IsRunning);

        context.DaemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));
        await WaitForSample(context.Controller, "pad-1", pressed: GamepadButtonMask.B);
        context.Clock.RaiseTick();

        Assert.True(viewModel.Visualizer.IsBPressed);
    }

    private static async Task<DispatchContext> CreateViewModel(bool secondDevice = false)
    {
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1", "Controller One");
        if (secondDevice)
        {
            daemonHost.AddDevice("pad-2", "Controller Two");
        }

        var store = new InMemoryProfileStore();
        var statePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var channel = new MainWindowViewModelDaemonTests.ServerChannel(server);
        server.StateChanged += channel.Push;
        server.InputSampled += channel.Push;
        var endpoint = new MainWindowViewModelDaemonTests.ServerEndpoint(channel);
        var clock = new FakeFrameClock();
        var controller = new DaemonController(endpoint);
        await controller.ConnectAsync(TestContext.Current.CancellationToken);

        var presentation = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
        var viewModel = new MainWindowViewModel(
            presentation,
            store,
            daemon: controller,
            daemonRequired: true,
            sampleFrameClock: clock);
        return new DispatchContext(viewModel, daemonHost, channel, server, endpoint, controller, clock);
    }

    /// <summary>
    /// Waits until the controller has taken a sample for <paramref name="deviceId"/> whose
    /// state matches. The controller stores the latest sample per device before raising the
    /// event, so a match also means the view model has already queued it.
    /// </summary>
    private static async Task WaitForSample(
        DaemonController controller,
        string deviceId,
        GamepadButtonMask pressed = default,
        float leftStickX = 0f,
        float rightStickX = 0f)
    {
        await WaitForCondition(() => controller.InputSamples.Any(sample => sample.DeviceId == deviceId
            && sample.PressedButtons == pressed
            && sample.LeftStickX == leftStickX
            && sample.RightStickX == rightStickX));
    }

    private static async Task WaitForCondition(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 1000 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private sealed record DispatchContext(
        MainWindowViewModel ViewModel,
        FakeDaemonHost DaemonHost,
        MainWindowViewModelDaemonTests.ServerChannel Channel,
        DaemonServer Server,
        MainWindowViewModelDaemonTests.ServerEndpoint Endpoint,
        DaemonController Controller,
        FakeFrameClock Clock);
}
