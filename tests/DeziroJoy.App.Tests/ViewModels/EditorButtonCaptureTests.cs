using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The editor's button capture as the window routes it. The capture is the editor's own
/// affordance - a mouse press arms it, a pad press names the mapping's source - so it
/// answers the pad whoever armed it and whatever the temporary hijack switch is doing:
/// with the hijack off the press still completes the capture, and navigates nowhere,
/// because the capture lease already keeps that press out of the game. The lease names
/// only the device being bound, and every way the capture ends releases it. Its budget
/// rides the UI clock, so a capture nobody completes expires even when the pad is quiet.
/// </summary>
public sealed class EditorButtonCaptureTests
{
    private const string DeviceId = "pad-01";

    private static (MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider) BuildLocal()
    {
        var provider = new FakeGamepadProvider();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, new RecordingOutputSink(), store);
        var viewModel = new MainWindowViewModel(host, store);
        return (viewModel, host, provider);
    }

    private static void Connect(FakeGamepadProvider provider)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
    }

    private static void Sample(MainWindowViewModel viewModel, EngineHost host)
        => SampleHelper.Sample(viewModel, host);

    /// <summary>
    /// One host tick with every device's sample fed to the view model, the way the window's
    /// own subscription does. <see cref="SampleHelper.Sample"/> keeps only the last event of
    /// a tick, which is one device's whole story when a second pad is connected - the host
    /// republishes every device to a fresh subscriber - so a two-pad test drives the view
    /// model the way production does instead.
    /// </summary>
    private static void SampleEveryDevice(MainWindowViewModel viewModel, EngineHost host)
    {
        var sampled = new List<ControllerSampledEventArgs>();
        void OnSampled(object? sender, ControllerSampledEventArgs args) => sampled.Add(args);

        host.Sampled += OnSampled;
        try
        {
            host.TickOnce(SampleHelper.Tick);
        }
        finally
        {
            host.Sampled -= OnSampled;
        }

        foreach (var args in sampled)
        {
            viewModel.ApplySample(args);
        }
    }

    private static void Press(
        FakeGamepadProvider provider,
        MainWindowViewModel viewModel,
        EngineHost host,
        GamepadButtonMask buttons)
    {
        provider.SetState(DeviceId, GamepadState.Create(buttons, 0f, 0f, 0f, 0f));
        Sample(viewModel, host);
    }

    [Fact]
    public async Task A_Press_Completes_The_Capture_With_The_Hijack_Off()
    {
        var (viewModel, host, provider) = BuildLocal();
        await using var disposed = viewModel;

        // The hijack ships off - this is the ordinary session the panel is used in.
        Assert.False(viewModel.GamepadNavigationEnabled);

        Connect(provider);
        Sample(viewModel, host);

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));

        viewModel.Editor.IsCapturingButton = true;
        Assert.Equal(DeviceId, host.CapturedDeviceId);

        // The press the user reaches for completes the capture without the hijack: before
        // this, the sample was dropped at the hijack gate and the capture could only ever
        // end on its budget.
        Press(provider, viewModel, host, GamepadButtonMask.B);

        Assert.False(viewModel.Editor.IsCapturingButton);
        Assert.Equal(GamepadButton.B, viewModel.Editor.SelectedButton);
        Assert.Null(host.CapturedDeviceId);
    }

    [Fact]
    public async Task With_The_Hijack_Off_The_Captured_Press_Navigates_Nowhere()
    {
        var (viewModel, host, provider) = BuildLocal();
        await using var disposed = viewModel;

        Connect(provider);
        Sample(viewModel, host);

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));
        var selectedBefore = viewModel.SelectedSource;
        var mappingBefore = viewModel.SelectedMapping;

        viewModel.Editor.IsCapturingButton = true;
        Press(provider, viewModel, host, GamepadButtonMask.B);

        // The press named the source the draft carries; it did not jump the editor to the
        // pressed element the way a hijacked press would - the hijack stays off.
        Assert.Equal(GamepadButton.B, viewModel.Editor.SelectedButton);
        Assert.Equal(selectedBefore, viewModel.SelectedSource);
        Assert.Same(mappingBefore, viewModel.SelectedMapping);
        Assert.True(viewModel.IsEditing);
    }

    [Fact]
    public async Task With_The_Hijack_Off_An_Unarmed_Press_Stays_With_The_Mapping()
    {
        var (viewModel, host, provider) = BuildLocal();
        await using var disposed = viewModel;

        Connect(provider);
        Sample(viewModel, host);

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));
        var selectedBefore = viewModel.SelectedSource;
        var mappingBefore = viewModel.SelectedMapping;

        // No capture is armed, so the press belongs to the active mapping: nothing in the
        // UI moves, exactly as before the capture learned to answer the pad.
        Press(provider, viewModel, host, GamepadButtonMask.B);

        Assert.False(viewModel.Editor.IsCapturingButton);
        Assert.Equal(selectedBefore, viewModel.SelectedSource);
        Assert.Same(mappingBefore, viewModel.SelectedMapping);
        Assert.Equal(GamepadButton.A, viewModel.Editor.SelectedButton);
    }

    [Fact]
    public async Task A_Press_Completes_The_Capture_With_The_Hijack_On()
    {
        var (viewModel, host, provider) = BuildLocal();
        await using var disposed = viewModel;
        viewModel.SetGamepadNavigationEnabled(true);

        Connect(provider);
        Sample(viewModel, host);

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));

        viewModel.Editor.IsCapturingButton = true;
        Assert.Equal(DeviceId, host.CapturedDeviceId);

        Press(provider, viewModel, host, GamepadButtonMask.B);

        Assert.False(viewModel.Editor.IsCapturingButton);
        Assert.Equal(GamepadButton.B, viewModel.Editor.SelectedButton);
        Assert.Null(host.CapturedDeviceId);
    }

    [Fact]
    public async Task The_Capture_Lease_Names_Only_The_Device_Being_Bound()
    {
        var (viewModel, host, provider) = BuildLocal();
        await using var disposed = viewModel;

        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.Add("pad-02", "Second Controller");
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        provider.SetState("pad-02", GamepadState.Create(default, 0f, 0f, 0f, 0f));
        SampleEveryDevice(viewModel, host);

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));
        viewModel.Editor.IsCapturingButton = true;

        // The lease names the device in scope - the first controller - and no other: the
        // second pad is not paused by a capture armed for the first.
        Assert.Equal(DeviceId, host.CapturedDeviceId);

        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.B, 0f, 0f, 0f, 0f));
        SampleEveryDevice(viewModel, host);

        Assert.False(viewModel.Editor.IsCapturingButton);
        Assert.Null(host.CapturedDeviceId);
    }

    [Fact]
    public async Task Cancelling_Ends_The_Capture_With_The_Hijack_Off()
    {
        var (viewModel, host, provider) = BuildLocal();
        await using var disposed = viewModel;

        Connect(provider);
        Sample(viewModel, host);

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));

        viewModel.Editor.IsCapturingButton = true;
        Assert.Equal(DeviceId, host.CapturedDeviceId);

        viewModel.CancelEditorCommand.Execute(null);

        Assert.False(viewModel.IsEditing);
        Assert.False(viewModel.Editor.IsCapturingButton);
        Assert.Null(host.CapturedDeviceId);
    }

    [Fact]
    public async Task A_Forgotten_Capture_Expires_Without_Any_Input()
    {
        var (viewModel, host, provider) = BuildLocal();
        await using var disposed = viewModel;

        Connect(provider);
        Sample(viewModel, host);

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));

        // An already-expired budget stands in for the real one, and no sample is ever fed:
        // the check itself is what ends the capture, which is what the UI clock calls.
        viewModel.ButtonCaptureTimeout = TimeSpan.Zero;

        viewModel.Editor.IsCapturingButton = true;
        Assert.Equal(DeviceId, host.CapturedDeviceId);

        viewModel.EnforceButtonCaptureBudget();

        Assert.False(viewModel.Editor.IsCapturingButton);
        Assert.Contains("超时", viewModel.StatusMessage);
        Assert.Null(host.CapturedDeviceId);
    }

    [Fact]
    public async Task An_Injected_Clock_Walks_A_Fresh_Capture_Past_Its_Budget()
    {
        var (viewModel, host, provider) = BuildLocal();
        await using var disposed = viewModel;

        Connect(provider);
        Sample(viewModel, host);

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));

        // The budget is read from an injected clock, so a test walks it past the deadline
        // without waiting out the real fifteen seconds - and without a single sample.
        var now = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        viewModel.CaptureClock = () => now;

        viewModel.Editor.IsCapturingButton = true;
        Assert.Equal(DeviceId, host.CapturedDeviceId);

        viewModel.EnforceButtonCaptureBudget();
        Assert.True(viewModel.Editor.IsCapturingButton, "the budget has not run out yet");

        now += viewModel.ButtonCaptureTimeout + TimeSpan.FromSeconds(1);
        viewModel.EnforceButtonCaptureBudget();

        Assert.False(viewModel.Editor.IsCapturingButton);
        Assert.Contains("超时", viewModel.StatusMessage);
        Assert.Null(host.CapturedDeviceId);
    }

    [Fact]
    public async Task A_Fresh_Capture_Survives_The_Budget_Check()
    {
        var (viewModel, host, provider) = BuildLocal();
        await using var disposed = viewModel;

        Connect(provider);
        Sample(viewModel, host);

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));

        viewModel.Editor.IsCapturingButton = true;

        // The real budget is generous; the check on its own must not end a capture the user
        // is still reaching for.
        viewModel.EnforceButtonCaptureBudget();

        Assert.True(viewModel.Editor.IsCapturingButton);
        Assert.Equal(DeviceId, host.CapturedDeviceId);
    }
}
