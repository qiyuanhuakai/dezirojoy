using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The window's preview gate. The 16 ms timer keeps firing whatever the window is doing,
/// but a frame is drawn only when something is on screen to animate: the window shown,
/// active and un-minimized, the panel's own tab selected, and the panel not parked at
/// rest. A running drift measurement outranks every one of those gates, because its
/// collection rides the same clock and must not lose a sample.
/// <para>
/// The engine is never gated: the last test holds the window hidden while the engine
/// ticks and injects through it, which is the daemon's input and release path.
/// </para>
/// </summary>
public sealed class MainWindowPreviewGateTests
{
    private const string DeviceId = "pad-01";

    /// <summary>The preview timer's own cadence, the frame the gate is driven with here.</summary>
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    private static (MainWindow Window, MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider, RecordingOutputSink Sink) BuildWindow()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var viewModel = new MainWindowViewModel(host, store);
        var window = new MainWindow(viewModel, host);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // A shown window is focused in production; headless has no focus model, so the
        // platform's activation is raised by hand.
        RaiseActivated(window);
        return (window, viewModel, host, provider, sink);
    }

    private static void ConnectPad(FakeGamepadProvider provider)
        => provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));

    /// <summary>Engine running with a controller connected, so both panels are live.</summary>
    private static void StartLiveEngine(
        MainWindowViewModel viewModel,
        EngineHost host,
        FakeGamepadProvider provider)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        SampleHelper.StartEngineManually(viewModel, host);
    }

    private static TabControl Tabs(Window window) => window.FindControl<TabControl>("MainTabs")!;

    private static (double X, double Y, double Speed, double Travel) MouseFrame(MainWindowViewModel viewModel)
    {
        var mouse = viewModel.MouseSensitivity;
        return (mouse.PreviewX, mouse.PreviewY, mouse.CurrentSpeed, mouse.TravelDistance);
    }

    private static void RaiseActivated(Window window)
    {
        var field = typeof(WindowBase).GetField("Activated", BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (EventHandler?)field?.GetValue(window);
        handler?.Invoke(window, EventArgs.Empty);
    }

    private static void RaiseDeactivated(Window window)
    {
        var field = typeof(WindowBase).GetField("Deactivated", BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (EventHandler?)field?.GetValue(window);
        handler?.Invoke(window, EventArgs.Empty);
    }

    [AvaloniaFact]
    public void The_Preview_Gate_Counts_Drawn_Frames_On_The_Selected_Tab()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        StartLiveEngine(viewModel, host, provider);
        Tabs(window).SelectedIndex = 1;

        var scroll = viewModel.ScrollSensitivity;

        for (var i = 0; i < 10; i++)
        {
            window.AdvancePreview(Frame);
        }

        // One drawn frame per tick on the selected tab - and only there.
        Assert.Equal(10, window.AppliedPreviewTicks);
        Assert.True(
            viewModel.MouseSensitivity.CurrentSpeed > 0,
            "the live sweep should be drawing on the mouse tab");
        Assert.Equal(0, scroll.WheelOffset);
        Assert.Equal(0, scroll.CurrentRate);
    }

    [AvaloniaFact]
    public void A_Hidden_Window_Draws_No_Preview_Frames_And_Resumes_On_Show()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        StartLiveEngine(viewModel, host, provider);
        Tabs(window).SelectedIndex = 1;

        window.AdvancePreview(Frame);
        var drawn = window.AppliedPreviewTicks;
        var frozen = MouseFrame(viewModel);

        window.Hide();
        for (var i = 0; i < 20; i++)
        {
            window.AdvancePreview(Frame);
        }

        // Invisible: not one frame drawn, and the frozen frame has not moved a pixel.
        Assert.Equal(drawn, window.AppliedPreviewTicks);
        Assert.Equal(frozen, MouseFrame(viewModel));

        // Re-visible: the very next frame resumes from exactly where the preview parked.
        window.Show();
        RaiseActivated(window);
        window.AdvancePreview(Frame);

        Assert.Equal(drawn + 1, window.AppliedPreviewTicks);
        Assert.True(
            viewModel.MouseSensitivity.TravelDistance > frozen.Travel,
            "the resumed frame should keep the sweep moving");
    }

    [AvaloniaFact]
    public void A_Deactivated_Window_Freezes_The_Preview_Until_It_Returns()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        StartLiveEngine(viewModel, host, provider);
        Tabs(window).SelectedIndex = 1;

        window.AdvancePreview(Frame);
        var drawn = window.AppliedPreviewTicks;
        var frozen = MouseFrame(viewModel);

        RaiseDeactivated(window);
        for (var i = 0; i < 20; i++)
        {
            window.AdvancePreview(Frame);
        }

        Assert.Equal(drawn, window.AppliedPreviewTicks);
        Assert.Equal(frozen, MouseFrame(viewModel));

        RaiseActivated(window);
        window.AdvancePreview(Frame);

        Assert.Equal(drawn + 1, window.AppliedPreviewTicks);
        Assert.True(viewModel.MouseSensitivity.TravelDistance > frozen.Travel);
    }

    [AvaloniaFact]
    public void Switching_Tabs_Parks_The_Leaving_Panel_And_Wakes_The_Entered_One()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        StartLiveEngine(viewModel, host, provider);
        Tabs(window).SelectedIndex = 1;

        window.AdvancePreview(Frame);
        var drawn = window.AppliedPreviewTicks;
        var mouseFrozen = MouseFrame(viewModel);

        // The mapping tab hosts no preview at all.
        Tabs(window).SelectedIndex = 0;
        for (var i = 0; i < 20; i++)
        {
            window.AdvancePreview(Frame);
        }

        Assert.Equal(drawn, window.AppliedPreviewTicks);
        Assert.Equal(mouseFrozen, MouseFrame(viewModel));

        // The scroll tab draws its own panel; the mouse panel stays parked.
        Tabs(window).SelectedIndex = 2;
        for (var i = 0; i < 10; i++)
        {
            window.AdvancePreview(Frame);
        }

        Assert.Equal(drawn + 10, window.AppliedPreviewTicks);
        Assert.True(
            viewModel.ScrollSensitivity.CurrentRate > 0,
            "the scroll sweep should be drawing on the scroll tab");
        Assert.Equal(mouseFrozen, MouseFrame(viewModel));

        // Back to the mouse tab: it resumes from where it parked.
        Tabs(window).SelectedIndex = 1;
        window.AdvancePreview(Frame);

        Assert.Equal(drawn + 11, window.AppliedPreviewTicks);
        Assert.True(viewModel.MouseSensitivity.TravelDistance > mouseFrozen.Travel);
    }

    [AvaloniaFact]
    public void A_Minimized_Window_Freezes_The_Preview_And_Restoring_Resumes_On_The_Latest_Sample()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        StartLiveEngine(viewModel, host, provider);
        Tabs(window).SelectedIndex = 1;

        var mouse = viewModel.MouseSensitivity;
        mouse.IsRealInputMode = true;
        mouse.ResponseTime = 0f;

        // A sample with the stick pushed, so the preview is mid-motion.
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0.5f, 0f));
        SampleHelper.Sample(viewModel, host);
        window.AdvancePreview(Frame);

        var drawn = window.AppliedPreviewTicks;
        var frozen = MouseFrame(viewModel);
        Assert.True(frozen.Speed > 0, "the pushed stick should have the preview moving");

        window.WindowState = WindowState.Minimized;

        // The pad keeps moving while the window is minimized: samples keep arriving...
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0.9f, 0f));
        SampleHelper.Sample(viewModel, host);
        for (var i = 0; i < 20; i++)
        {
            window.AdvancePreview(Frame);
        }

        // ...but nothing is drawn and the frozen frame does not move.
        Assert.Equal(drawn, window.AppliedPreviewTicks);
        Assert.Equal(frozen, MouseFrame(viewModel));

        window.WindowState = WindowState.Normal;
        window.AdvancePreview(Frame);

        // Restoring resumes on the latest sample immediately: the engine's own smoothed
        // speed at the newest deflection, not a replay of the frames the window missed.
        Assert.Equal(drawn + 1, window.AppliedPreviewTicks);
        Assert.Equal(mouse.DailySpeed, mouse.CurrentSpeed, 3);
        Assert.True(mouse.TravelDistance > frozen.Travel);
    }

    [AvaloniaFact]
    public void Resuming_After_A_Long_Pause_Replays_No_Missed_Frames()
    {
        // The no-catch-up contract, driven through the timer's own entry point so the delta
        // clamp is under test: a panel that comes back after a real pause resumes with one
        // ordinary frame, never a replay of the elapsed time. The tick bookkeeping runs
        // before the gate, so even a gated pause leaves the resumed frame no debt.
        var (window, viewModel, host, provider, _) = BuildWindow();
        StartLiveEngine(viewModel, host, provider);
        Tabs(window).SelectedIndex = 1;

        var mouse = viewModel.MouseSensitivity;
        mouse.IsRealInputMode = true;
        mouse.ResponseTime = 0f;
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0.8f, 0f));
        SampleHelper.Sample(viewModel, host);

        window.Hide();

        // A real pause: the timer keeps firing through it in production, so the wall-clock
        // gap is what an implementation must not hand the resumed frame.
        Thread.Sleep(150);

        window.Show();
        RaiseActivated(window);
        window.OnPreviewTick(null, EventArgs.Empty);

        Assert.Equal(1, window.AppliedPreviewTicks);
        Assert.Equal(mouse.DailySpeed, mouse.CurrentSpeed, 3);

        // One clamped frame (16 ms) of engine output at the current speed, with room to
        // spare: a replay of the 150 ms pause would be nearly ten times this.
        var oneFrameTravel = mouse.DailySpeed * 0.016;
        Assert.True(
            mouse.TravelDistance <= oneFrameTravel * 1.5,
            $"resumed tick replayed {mouse.TravelDistance.ToString("F3", CultureInfo.InvariantCulture)} px, " +
            $"one frame is {oneFrameTravel.ToString("F3", CultureInfo.InvariantCulture)} px");
    }

    [AvaloniaFact]
    public void A_Resting_Panel_Draws_No_Frames_Until_The_Engine_Returns()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        Tabs(window).SelectedIndex = 1;

        var mouse = viewModel.MouseSensitivity;

        // No engine, no controller: the panel is parked at rest from the first frame on.
        for (var i = 0; i < 700; i++)
        {
            window.AdvancePreview(Frame);
        }

        Assert.True(mouse.IsStatic);
        Assert.True(window.AppliedPreviewTicks == 0, "a parked panel must not redraw");

        // The engine comes back: the very next tick must notice - the liveness refresh runs
        // ahead of the gate - and the sweep takes back over.
        StartLiveEngine(viewModel, host, provider);
        window.AdvancePreview(Frame);

        Assert.False(mouse.IsStatic);
        Assert.Equal(1, window.AppliedPreviewTicks);
        Assert.True(mouse.CurrentSpeed > 0, "the engine's return should restart the sweep");
    }

    [AvaloniaFact]
    public void A_Live_Pad_At_Rest_Draws_Nothing_Until_A_Sample_Moves_It()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        StartLiveEngine(viewModel, host, provider);
        Tabs(window).SelectedIndex = 1;

        var mouse = viewModel.MouseSensitivity;
        mouse.IsRealInputMode = true;
        mouse.ResponseTime = 0f;

        // The pad is live but resting: one sample, then the silence a still stick reports.
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        SampleHelper.Sample(viewModel, host);
        window.AdvancePreview(Frame);

        Assert.True(mouse.IsStatic);
        var drawn = window.AppliedPreviewTicks;

        for (var i = 0; i < 20; i++)
        {
            window.AdvancePreview(Frame);
        }

        Assert.True(window.AppliedPreviewTicks == drawn, "a live pad at rest must not redraw");
        Assert.Equal(0, mouse.CurrentSpeed);

        // The stick moves: the sample alone reopens the gate, and the next frame draws.
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0.7f, 0f));
        SampleHelper.Sample(viewModel, host);
        window.AdvancePreview(Frame);

        Assert.Equal(drawn + 1, window.AppliedPreviewTicks);
        Assert.Equal(mouse.DailySpeed, mouse.CurrentSpeed, 3);
    }

    [AvaloniaFact]
    public void A_Running_Calibration_Keeps_Its_Clock_Across_Tabs_And_Hides()
    {
        var (window, viewModel, _, _, _) = BuildWindow();
        Tabs(window).SelectedIndex = 1;

        var mouse = viewModel.MouseSensitivity;
        mouse.IsRealInputMode = true;

        // The pad rests with noise: one sample, then the silence the drift measurement
        // exists to capture.
        mouse.ApplySample(GamepadState.Create(default, 0f, 0f, 0.04f, 0.01f));
        mouse.StartDriftMeasurementCommand.Execute(null);

        var scroll = viewModel.ScrollSensitivity;

        // The measurement leaves its tab: the collection must not lose a sample.
        Tabs(window).SelectedIndex = 0;
        for (var i = 0; i < 40; i++)
        {
            window.AdvancePreview(MouseSensitivityViewModel.DriftSampleInterval);
        }

        Assert.Equal(40, mouse.DriftSampleCount);

        // ...and the window hides: still no loss.
        window.Hide();
        for (var i = 0; i < 20; i++)
        {
            window.AdvancePreview(MouseSensitivityViewModel.DriftSampleInterval);
        }

        Assert.Equal(60, mouse.DriftSampleCount);
        Assert.Equal(60, window.AppliedPreviewTicks);

        // The scroll panel never measured and was never on screen: untouched.
        Assert.Equal(0, scroll.WheelOffset);
        Assert.Equal(0, scroll.CurrentRate);

        mouse.StopDriftMeasurementCommand.Execute(null);

        // The full distribution survived, so the suggestion stands on its own.
        Assert.Equal(0.06, mouse.SuggestedDeadZone!.Value, 3);
    }

    [AvaloniaFact]
    public void A_Hidden_Window_Never_Throttles_The_Engine()
    {
        // The gate is a preview gate: the engine's own loop is the daemon's input and
        // release path, and it must keep running with the window hidden.
        var (window, viewModel, host, provider, sink) = BuildWindow();
        StartLiveEngine(viewModel, host, provider);

        window.Hide();

        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.B, 0f, 0f, 0f, 0f));
        host.TickOnce(SampleHelper.Tick);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("down:Enter", sink.Calls);

        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        host.TickOnce(SampleHelper.Tick);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("up:Enter", sink.Calls);
        Assert.True(host.IsRunning);
        Assert.True(window.AppliedPreviewTicks == 0, "the preview itself stayed frozen");
    }
}
