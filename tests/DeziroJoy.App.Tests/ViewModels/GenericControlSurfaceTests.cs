using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The generic control surface as the window drives it, against the device layer's real
/// declaration: a device that describes its controls starts on the panel, detection is
/// latched from real samples, a card click opens the mapping settings for that control's
/// source, the button capture takes and releases its device lease, and a stick's execution
/// mode is rewritten as mappings - never reinterpreted.
/// </summary>
public sealed class GenericControlSurfaceTests
{
    private const string DeviceId = "pad-01";

    /// <summary>
    /// A device that declares the controls the Core's builder produces: the left stick, the
    /// four face keys, both bumpers, both triggers, the guide and one extra code.
    /// </summary>
    private static List<GamepadControlDescriptor> DeclaredControls()
        =>
        [
            new(new StickBinding(GamepadStick.Left), "左摇杆", GamepadControlKind.Direction, GamepadControlZone.Directions),
            new(new ButtonBinding(GamepadButton.A), "A", GamepadControlKind.Button, GamepadControlZone.MainButtons),
            new(new ButtonBinding(GamepadButton.B), "B", GamepadControlKind.Button, GamepadControlZone.MainButtons),
            new(new ButtonBinding(GamepadButton.LeftBumper), "LB", GamepadControlKind.Button, GamepadControlZone.Shoulders),
            new(new ButtonBinding(GamepadButton.LeftTrigger), "LT", GamepadControlKind.Axis, GamepadControlZone.Shoulders),
            new(new ButtonBinding(GamepadButton.Guide), "Guide", GamepadControlKind.Button, GamepadControlZone.Functions),
            new(new ButtonBinding(GamepadButton.Extra, 305), "附加键 305", GamepadControlKind.Other, GamepadControlZone.Extensions),
        ];

    private static GamepadProfile MouseStickProfile()
        => new()
        {
            Name = "surface",
            DeviceId = DeviceId,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right),
                    Target = new MouseMoveTarget(new MouseStickSettings { MaxSpeed = 1800f }),
                },
            ],
        };

    private static (MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider) BuildLocal(
        GamepadProfile? seeded = null,
        IReadOnlyList<GamepadControlDescriptor>? controls = null)
    {
        var provider = new FakeGamepadProvider();
        var store = new InMemoryProfileStore();
        if (seeded is not null)
        {
            store.Save(seeded);
        }

        var host = new EngineHost(provider, new RecordingOutputSink(), store);
        var viewModel = new MainWindowViewModel(host, store);
        return (viewModel, host, provider);
    }

    private static void Connect(
        FakeGamepadProvider provider,
        IReadOnlyList<GamepadControlDescriptor>? controls = null)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller", controls ?? []);
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

    private static async Task WaitForCaptureAsync(Func<bool> settled, string what)
    {
        // The daemon round trip is real IPC, and the full suite runs this beside every other
        // class: the budget is generous so a loaded machine costs time, not a failure.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!settled())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException($"timed out waiting for {what}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_Device_That_Declares_No_Controls_Starts_On_The_Hand_Drawn_Model()
    {
        var (viewModel, host, provider) = BuildLocal(MouseStickProfile());
        await using var disposed = viewModel;

        // The legacy backends describe nothing, and "not described yet" is not "no controls":
        // the panel stays empty and the hand-drawn model remains the mapping surface.
        Connect(provider);
        Sample(viewModel, host);

        Assert.False(viewModel.CanUseGenericPanel);
        Assert.False(viewModel.IsGenericPanelMode);
        Assert.Equal("通用面板", viewModel.SurfaceToggleLabel);

        viewModel.ToggleMappingSurfaceCommand.Execute(null);

        Assert.False(viewModel.IsGenericPanelMode);
    }

    [Fact]
    public async Task A_Device_That_Declares_Its_Controls_Starts_On_The_Generic_Panel()
    {
        var (viewModel, host, provider) = BuildLocal(MouseStickProfile(), DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        // The declaration is the panel, in the Core's five zones, in declaration order.
        Assert.True(viewModel.CanUseGenericPanel);
        Assert.True(viewModel.IsGenericPanelMode);
        Assert.Equal("手柄模型", viewModel.SurfaceToggleLabel);

        var panel = viewModel.GenericPanel;

        // The declaration is the panel - plus the one source this profile binds that the
        // declaration never names: the mouse-move right stick. A bound control is always
        // shown, so it joins as an extension rather than sitting invisible in the profile.
        Assert.Equal(DeclaredControls().Count + 1, panel.Items.Count);

        var rightStick = panel.Items.Single(item => item.Label == "右摇杆");
        Assert.Equal(GamepadControlKind.Other, rightStick.Kind);
        Assert.Equal(GamepadControlZone.Extensions, rightStick.Zone);
        Assert.True(rightStick.IsBound);
        Assert.True(rightStick.IsShown);
        Assert.Contains(rightStick, panel.ExtensionItems);

        // Nothing has been pressed, so every declared control waits in 更多输入 until a real
        // sample says otherwise - a declaration is not an observation. The bound stick holds
        // its extension slot, and the wait keeps the declaration order.
        Assert.Equal(["右摇杆"], panel.ExtensionItems.Select(item => item.Label).ToArray());
        Assert.Equal(
            ["左摇杆", "A", "B", "LB", "LT", "Guide", "附加键 305"],
            panel.MoreInputItems.Select(item => item.Label).ToArray());
        Assert.Equal(DeclaredControls().Count, panel.MoreInputCount);

        // 显示全部输入 promotes the wait onto the panel, and the Core's five zones fill in
        // the declaration order - the declared controls first, the merged one appended.
        panel.ToggleShowAllCommand.Execute(null);

        Assert.Equal(["左摇杆"], panel.DirectionItems.Select(item => item.Label).ToArray());
        Assert.Equal(["A", "B"], panel.MainButtonItems.Select(item => item.Label).ToArray());
        Assert.Equal(["LB", "LT"], panel.ShoulderItems.Select(item => item.Label).ToArray());
        Assert.Equal(["Guide"], panel.FunctionItems.Select(item => item.Label).ToArray());
        Assert.Equal(
            ["附加键 305", "右摇杆"],
            panel.ExtensionItems.Select(item => item.Label).ToArray());
        Assert.Equal(0, panel.MoreInputCount);

        // Collapsing again puts the untouched controls back in the wait, so the latch below
        // is observed from the collapsed state the panel starts in.
        panel.ToggleShowAllCommand.Execute(null);
        Assert.Equal(DeclaredControls().Count, panel.MoreInputCount);

        // A real sample latches the control it pressed, and only that one.
        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.A, 0f, 0f, 0f, 0f));
        Sample(viewModel, host);

        var a = panel.Items.Single(item => item.Label == "A");
        Assert.True(a.IsDetected);
        Assert.True(a.IsShown);
        Assert.Contains(a, panel.MainButtonItems);

        // Only the declared controls ever wait: the merged one is bound and stays shown, so
        // the count is the declaration's, not the panel's.
        Assert.Equal(DeclaredControls().Count - 1, panel.MoreInputCount);
    }

    [Fact]
    public async Task The_Surface_Switch_Is_The_Users_And_Leaves_Every_Binding_Untouched()
    {
        var (viewModel, host, provider) = BuildLocal(MouseStickProfile(), DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var before = viewModel.ActiveProfile!.Mappings.ToArray();

        viewModel.ToggleMappingSurfaceCommand.Execute(null);
        Assert.False(viewModel.IsGenericPanelMode);

        // Switching the layout is a view of this session's device: the profile - and with it
        // every mapping - is exactly what it was.
        Assert.Equal(before, viewModel.ActiveProfile!.Mappings.ToArray());

        viewModel.ToggleMappingSurfaceCommand.Execute(null);
        Assert.True(viewModel.IsGenericPanelMode);
        Assert.Equal(before, viewModel.ActiveProfile!.Mappings.ToArray());
    }

    [Fact]
    public async Task Opening_A_Generic_Control_Opens_The_Mapping_Settings_For_Its_Source()
    {
        var (viewModel, host, provider) = BuildLocal(MouseStickProfile(), DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var guide = viewModel.GenericPanel.Items.Single(item => item.Label == "Guide");

        // The panel's click path, exactly as a click on the hand-drawn model's element.
        viewModel.GenericPanel.OpenControlRequested!(guide);

        Assert.True(viewModel.IsEditing);
        Assert.Equal(GamepadButton.Guide, viewModel.Editor.SelectedButton);
        Assert.Equal(new ButtonBinding(GamepadButton.Guide), viewModel.SelectedSource);
    }

    [Fact]
    public async Task The_Panels_Bound_State_Follows_The_Profile()
    {
        var seeded = MouseStickProfile();
        var (viewModel, host, provider) = BuildLocal(seeded, DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var lb = viewModel.GenericPanel.Items.Single(item => item.Label == "LB");
        Assert.False(lb.IsBound);

        // The control gains its first mapping through the ordinary edit path, and the
        // panel's display strategy follows - the control moves onto the always-shown half.
        Assert.True(await viewModel.ApplyProfile(seeded with
        {
            Mappings =
            [
                .. seeded.Mappings,
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.LeftBumper),
                    Target = new KeyPressTarget(GamepadKey.G),
                },
            ],
        }));

        Assert.True(lb.IsBound);
        Assert.True(lb.IsShown);
        Assert.False(lb.IsInMoreInputs);
    }

    [Fact]
    public async Task Four_Way_Conversion_Generates_Four_Direction_Mappings_With_The_Default_Keys()
    {
        var (viewModel, host, provider) = BuildLocal(MouseStickProfile(), DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var applied = await viewModel.SetStickDirectionMode(GamepadStick.Right, GenericStickMode.FourWay);

        Assert.True(applied);
        var mappings = viewModel.ActiveProfile!.Mappings;
        Assert.DoesNotContain(mappings, m => m.Source is StickBinding { Direction: null });
        Assert.Equal(4, mappings.Count);

        var expected = new (InputDirection Direction, GamepadKey Key)[]
        {
            (InputDirection.Up, GamepadKey.ArrowUp),
            (InputDirection.Down, GamepadKey.ArrowDown),
            (InputDirection.Left, GamepadKey.ArrowLeft),
            (InputDirection.Right, GamepadKey.ArrowRight),
        };
        foreach (var (direction, key) in expected)
        {
            var entry = Assert.Single(
                mappings,
                m => m.Source is StickBinding { Direction: { } } binding && binding.Direction == direction);
            Assert.Equal(new KeyPressTarget(key), entry.Target);
        }
    }

    [Fact]
    public async Task Four_Way_Conversion_Keeps_Directions_The_User_Already_Configured()
    {
        var seeded = MouseStickProfile() with
        {
            Mappings =
            [
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right),
                    Target = new MouseMoveTarget(new MouseStickSettings()),
                },
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right) { Direction = InputDirection.Up },
                    Target = new KeyPressTarget(GamepadKey.W),
                },
            ],
        };
        var (viewModel, host, provider) = BuildLocal(seeded, DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var applied = await viewModel.SetStickDirectionMode(GamepadStick.Right, GenericStickMode.FourWay);

        Assert.True(applied);
        var mappings = viewModel.ActiveProfile!.Mappings;

        // The configured direction keeps its key; the other three get the defaults.
        var up = Assert.Single(mappings, m => m.Source is StickBinding { Direction: InputDirection.Up });
        Assert.Equal(new KeyPressTarget(GamepadKey.W), up.Target);
        Assert.Equal(
            new KeyPressTarget(GamepadKey.ArrowDown),
            Assert.Single(mappings, m => m.Source is StickBinding { Direction: InputDirection.Down }).Target);
        Assert.Equal(
            new KeyPressTarget(GamepadKey.ArrowLeft),
            Assert.Single(mappings, m => m.Source is StickBinding { Direction: InputDirection.Left }).Target);
        Assert.Equal(
            new KeyPressTarget(GamepadKey.ArrowRight),
            Assert.Single(mappings, m => m.Source is StickBinding { Direction: InputDirection.Right }).Target);
        Assert.Equal(4, mappings.Count);
    }

    [Fact]
    public async Task Continuous_Conversion_Collapses_To_One_Mapping_And_Keeps_A_Carried_Target()
    {
        var seeded = MouseStickProfile() with
        {
            Mappings =
            [
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right) { Direction = InputDirection.Up },
                    Target = new MouseMoveTarget(new MouseStickSettings { MaxSpeed = 2600f }),
                },
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right) { Direction = InputDirection.Down },
                    Target = new KeyPressTarget(GamepadKey.ArrowDown),
                },
            ],
        };
        var (viewModel, host, provider) = BuildLocal(seeded, DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var applied = await viewModel.SetStickDirectionMode(GamepadStick.Right, GenericStickMode.Continuous);

        Assert.True(applied);
        var mappings = viewModel.ActiveProfile!.Mappings;

        var entry = Assert.Single(mappings);
        var source = Assert.IsType<StickBinding>(entry.Source);
        Assert.Null(source.Direction);

        // The continuous target a direction already carried wins, settings and all - the
        // user's tuning survives the collapse.
        var mouse = Assert.IsType<MouseMoveTarget>(entry.Target);
        Assert.Equal(2600f, mouse.Settings.MaxSpeed);
    }

    [Fact]
    public async Task Continuous_Conversion_Of_Untouched_Defaults_Lands_On_The_Canonical_Target()
    {
        var seeded = MouseStickProfile() with
        {
            Mappings =
            [
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right) { Direction = InputDirection.Up },
                    Target = new KeyPressTarget(GamepadKey.ArrowUp),
                },
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right) { Direction = InputDirection.Down },
                    Target = new KeyPressTarget(GamepadKey.ArrowDown),
                },
            ],
        };
        var (viewModel, host, provider) = BuildLocal(seeded, DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var applied = await viewModel.SetStickDirectionMode(GamepadStick.Right, GenericStickMode.Continuous);

        Assert.True(applied);
        var entry = Assert.Single(viewModel.ActiveProfile!.Mappings);
        Assert.IsType<MouseMoveTarget>(entry.Target);
    }

    [Fact]
    public async Task The_Conversion_Runs_Through_The_Profile_So_The_Engine_Sees_It()
    {
        var (viewModel, host, provider) = BuildLocal(MouseStickProfile(), DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        await viewModel.SetStickDirectionMode(GamepadStick.Right, GenericStickMode.FourWay);

        // The running engine holds the rewritten profile: four direction mappings is a
        // shape the engine validates, so a rebuild that dropped them would fail to load.
        var engine = Assert.IsType<ControllerRuntime>(viewModel.ActiveController).Engine;
        Assert.Contains(
            engine.EffectiveMappings,
            mapping => mapping.Source is StickBinding { Direction: InputDirection.Left });
    }

    [Fact]
    public async Task The_Capture_Lease_Follows_The_Capture_On_The_Local_Host()
    {
        var (viewModel, host, provider) = BuildLocal(MouseStickProfile(), DeclaredControls());
        await using var disposed = viewModel;

        // A press completes an armed capture only while the temporary hijack is on - that is
        // the mode where pad presses drive the UI instead of the active mapping - so the
        // successful-press path is exercised with it armed, exactly as the navigation tests
        // do it.
        viewModel.SetGamepadNavigationEnabled(true);

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var guide = viewModel.GenericPanel.Items.Single(item => item.Label == "Guide");
        viewModel.GenericPanel.OpenControlRequested!(guide);

        // Arming the capture takes the lease on exactly the device being bound.
        viewModel.Editor.IsCapturingButton = true;
        Assert.Equal(DeviceId, host.CapturedDeviceId);
        Assert.True(guide.IsCaptureTarget, "the panel marks the control the capture waits for");

        // Every way the capture ends releases the lease with it - a successful press first.
        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.Guide, 0f, 0f, 0f, 0f));
        Sample(viewModel, host);

        Assert.False(viewModel.Editor.IsCapturingButton);
        Assert.Null(host.CapturedDeviceId);
        Assert.False(guide.IsCaptureTarget);
    }

    [Fact]
    public async Task The_Capture_Budget_Ends_A_Forgotten_Capture()
    {
        var (viewModel, host, provider) = BuildLocal(MouseStickProfile(), DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var guide = viewModel.GenericPanel.Items.Single(item => item.Label == "Guide");
        viewModel.GenericPanel.OpenControlRequested!(guide);

        // An already-expired budget stands in for the real one: the capture is checked on
        // the sample stream, so the very next sample must end it.
        var budget = viewModel.ButtonCaptureTimeout;
        viewModel.ButtonCaptureTimeout = TimeSpan.Zero;
        try
        {
            viewModel.Editor.IsCapturingButton = true;
            Assert.Equal(DeviceId, host.CapturedDeviceId);

            provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0.2f, 0f));
            Sample(viewModel, host);

            Assert.False(viewModel.Editor.IsCapturingButton);
            Assert.Null(host.CapturedDeviceId);
            Assert.Contains("超时", viewModel.StatusMessage);
        }
        finally
        {
            viewModel.ButtonCaptureTimeout = budget;
        }
    }

    [Fact]
    public async Task Closing_The_Editor_Ends_The_Capture_And_Its_Lease()
    {
        var (viewModel, host, provider) = BuildLocal(MouseStickProfile(), DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var guide = viewModel.GenericPanel.Items.Single(item => item.Label == "Guide");
        viewModel.GenericPanel.OpenControlRequested!(guide);

        viewModel.Editor.IsCapturingButton = true;
        Assert.Equal(DeviceId, host.CapturedDeviceId);

        viewModel.CancelEditorCommand.Execute(null);

        Assert.False(viewModel.IsEditing);
        Assert.False(viewModel.Editor.IsCapturingButton);
        Assert.Null(host.CapturedDeviceId);
        Assert.False(guide.IsCaptureTarget);
    }

    [Fact]
    public async Task A_Device_That_Goes_Away_Ends_The_Capture_And_Clears_The_Panel()
    {
        var (viewModel, host, provider) = BuildLocal(MouseStickProfile(), DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var guide = viewModel.GenericPanel.Items.Single(item => item.Label == "Guide");
        viewModel.GenericPanel.OpenControlRequested!(guide);

        viewModel.Editor.IsCapturingButton = true;
        Assert.Equal(DeviceId, host.CapturedDeviceId);

        // The pad the capture waits for is gone: the host's absence scan says so on the
        // next presence reconciliation.
        provider.Remove(DeviceId);
        for (var i = 0; i < EngineHost.DeviceAbsenceGraceTicks + 1; i++)
        {
            host.TickOnce(SampleHelper.Tick);
        }

        viewModel.SyncControllers();

        Assert.False(viewModel.Editor.IsCapturingButton);
        Assert.Null(host.CapturedDeviceId);
        Assert.Contains("断开", viewModel.StatusMessage);
        Assert.Empty(viewModel.GenericPanel.Items);
    }

    [Fact]
    public async Task Leaving_Scope_Ends_The_Capture_And_Its_Lease()
    {
        var (viewModel, host, provider) = BuildLocal(MouseStickProfile(), DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var guide = viewModel.GenericPanel.Items.Single(item => item.Label == "Guide");
        viewModel.GenericPanel.OpenControlRequested!(guide);

        viewModel.Editor.IsCapturingButton = true;
        Assert.Equal(DeviceId, host.CapturedDeviceId);

        viewModel.SetPhysicalInputScope(false);

        Assert.False(viewModel.Editor.IsCapturingButton);
        Assert.Null(host.CapturedDeviceId);
    }

    [Fact]
    public async Task A_Snapshot_Refresh_Keeps_Every_Latch_The_Panel_Holds()
    {
        var context = await MainWindowViewModelDaemonTests.CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        // A render process never sees the pad: the daemon's snapshot is the declaration, so
        // the panel is built from it exactly as it is from a local descriptor.
        context.DaemonHost.AddDevice("pad-1", "Test Controller", GamepadLayout.Xbox, DeclaredControls());
        await WaitForCaptureAsync(
            () => viewModel.GenericPanel.HasItems && viewModel.GenericPanel.Items.Count == DeclaredControls().Count,
            "the snapshot's declaration to reach the panel");

        var panel = viewModel.GenericPanel;
        var a = panel.Items.Single(item => item.Label == "A");
        var stick = panel.Items.Single(item => item.IsStick);
        var lb = panel.Items.Single(item => item.Label == "LB");
        var guide = panel.Items.Single(item => item.Label == "Guide");

        // A real sample latches the control it pressed...
        context.DaemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0f, 0f, 0f, 0f));
        await WaitForCaptureAsync(() => a.IsDetected, "the press to latch");

        // ...the user hides one control and flips the stick's glyph - display state, not
        // mappings...
        lb.HideCommand.Execute(null);
        stick.SwitchAppearanceCommand.Execute(null);
        Assert.Equal(GenericStickAppearance.Dpad, stick.StickAppearance);

        // ...and the editor's capture marks the control being bound.
        panel.OpenControlRequested!(guide);
        viewModel.Editor.IsCapturingButton = true;
        Assert.True(guide.IsCaptureTarget);

        // Another pad connects, so the daemon re-publishes the same device's declaration. A
        // snapshot is not a new device: every latch, hide, switch and capture mark survives,
        // on the very same items - the panel is not rebuilt under the user.
        context.DaemonHost.AddDevice("pad-2", "Second Controller", GamepadLayout.Xbox, DeclaredControls());
        await WaitForCaptureAsync(
            () => viewModel.DaemonSnapshot is { } snapshot && snapshot.Devices.Count == 2,
            "the second pad to reach the snapshot");

        Assert.Same(a, panel.Items.Single(item => item.Label == "A"));
        Assert.True(a.IsDetected, "the detection latch survives the snapshot");
        Assert.True(lb.IsUserHidden, "the hide survives the snapshot");
        Assert.Equal(0, panel.ShoulderItems.Count(item => item.Label == "LB"));
        Assert.Same(stick, panel.Items.Single(item => item.IsStick));
        Assert.Equal(GenericStickAppearance.Dpad, stick.StickAppearance);
        Assert.True(guide.IsCaptureTarget, "the capture mark survives the snapshot");
        Assert.Equal(1, panel.HiddenCount);
        Assert.Equal(DeclaredControls().Count, panel.Items.Count);
    }

    [Fact]
    public async Task The_Refreshed_Panel_Re_Derives_Every_Sticks_Mode_From_The_Profile()
    {
        var context = await MainWindowViewModelDaemonTests.CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        context.DaemonHost.AddDevice("pad-1", "Test Controller", GamepadLayout.Xbox, DeclaredControls());
        await WaitForCaptureAsync(() => viewModel.GenericPanel.HasItems, "the declaration to reach the panel");

        var stick = viewModel.GenericPanel.Items.Single(item => item.IsStick);
        Assert.Equal(GenericStickMode.Continuous, stick.StickMode);

        // The daemon's profile now carries four direction mappings for the stick. A reused
        // item keeps its state, but its mode is the profile's answer - so the refresh that
        // brings the profile re-derives the selector rather than trusting the item to
        // remember a mode the mappings no longer carry. The profile arrives through the
        // daemon's own apply path, which is what moves the revision the window tracks.
        var fourWay = new GamepadProfile
        {
            Name = "four way",
            DeviceId = "pad-1",
            Mappings =
            [
                .. Enum.GetValues<InputDirection>().Select(direction => new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Left) { Direction = direction },
                    Target = new KeyPressTarget(GamepadKey.Space),
                }),
            ],
        };
        await context.Server.ProcessAsync(
            new ApplyConfigurationMessage("req-four-way", 0, "four way", ProfileSerializer.Serialize(fourWay)));
        await WaitForCaptureAsync(
            () => viewModel.ActiveProfile?.Name == "four way",
            "the profile to reach the window");

        Assert.Same(stick, viewModel.GenericPanel.Items.Single(item => item.IsStick));
        Assert.Equal(GenericStickMode.FourWay, stick.StickMode);
        Assert.Equal("四方向", stick.StickModeLabel);
    }

    [Fact]
    public async Task A_Stick_Mode_Switch_Waits_For_The_Editor_Draft_To_Be_Settled()
    {
        var (viewModel, host, provider) = BuildLocal(MouseStickProfile(), DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        // An unconfirmed draft is open: converting the mode would rewrite the mappings under
        // it, and committing the draft afterwards would silently overwrite the conversion.
        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));
        Assert.True(viewModel.IsEditing);

        var before = viewModel.ActiveProfile!.Mappings.ToArray();
        var stick = viewModel.GenericPanel.Items.Single(item => item.IsStick);

        await stick.SwitchStickModeCommand.ExecuteAsync(null);

        Assert.Equal(GenericStickMode.Continuous, stick.StickMode);
        Assert.Contains("请先确定或取消", viewModel.StatusMessage);
        Assert.Equal(before, viewModel.ActiveProfile.Mappings.ToArray());

        // Settling the draft lets the same switch through.
        viewModel.CancelEditorCommand.Execute(null);
        Assert.False(viewModel.IsEditing);

        await stick.SwitchStickModeCommand.ExecuteAsync(null);

        Assert.Equal(GenericStickMode.FourWay, stick.StickMode);
    }

    [Fact]
    public async Task The_Panel_Is_One_Devices_Surface_Even_With_A_Second_Pad_Connected()
    {
        var (viewModel, host, provider) = BuildLocal(MouseStickProfile(), DeclaredControls());
        await using var disposed = viewModel;

        provider.Add(DeviceId, "Xbox Wireless Controller", DeclaredControls());
        provider.Add("pad-02", "Second Controller", DeclaredControls());
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        provider.SetState("pad-02", GamepadState.Create(default, 0f, 0f, 0f, 0f));
        SampleEveryDevice(viewModel, host);

        var panel = viewModel.GenericPanel;
        var a = panel.Items.Single(item => item.Label == "A");
        Assert.False(a.IsPressed);

        // The second pad presses A: the panel belongs to the device in scope - the first
        // controller - so nothing lights and nothing latches, exactly as its capture would
        // not answer that pad either.
        provider.SetState("pad-02", GamepadState.Create(GamepadButtonMask.A, 0f, 0f, 0f, 0f));
        SampleEveryDevice(viewModel, host);

        Assert.False(a.IsPressed, "the second pad's press does not light the panel");
        Assert.False(a.IsDetected, "and does not latch it either");

        // The device in scope presses it: the card lights and the latch takes.
        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.A, 0f, 0f, 0f, 0f));
        SampleEveryDevice(viewModel, host);

        Assert.True(a.IsPressed);
        Assert.True(a.IsDetected);
    }

    [Fact]
    public async Task An_Ended_Capture_Leaves_No_Stale_Target_For_The_Next_One()
    {
        var (viewModel, host, provider) = BuildLocal(MouseStickProfile(), DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var guide = viewModel.GenericPanel.Items.Single(item => item.Label == "Guide");
        viewModel.GenericPanel.OpenControlRequested!(guide);

        viewModel.Editor.IsCapturingButton = true;
        Assert.True(guide.IsCaptureTarget);

        // The capture ends - cancelled here, timed out or disconnected elsewhere - and the
        // control it waited for is nobody's target any more.
        viewModel.CancelEditorCommand.Execute(null);
        Assert.False(guide.IsCaptureTarget);

        // A later capture armed from the editor itself, with no card behind it, marks only
        // its own target: the ended capture's control is not remembered.
        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));
        viewModel.Editor.IsCapturingButton = true;

        Assert.Equal(0, viewModel.GenericPanel.Items.Count(item => item.IsCaptureTarget));
    }

    [Fact]
    public async Task The_Daemons_Snapshot_Declaration_Builds_The_Panel_And_Its_Capture_Lease_Travels()
    {
        var context = await MainWindowViewModelDaemonTests.CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        // A render process never sees the pad: the daemon's snapshot is the declaration, so
        // the panel is built from it exactly as it is from a local descriptor.
        context.DaemonHost.AddDevice("pad-1", "Test Controller", GamepadLayout.Xbox, DeclaredControls());
        await WaitForCaptureAsync(
            () => viewModel.GenericPanel.HasItems && viewModel.GenericPanel.Items.Count == DeclaredControls().Count,
            "the snapshot's declaration to reach the panel");

        Assert.True(viewModel.CanUseGenericPanel);
        Assert.True(viewModel.IsGenericPanelMode);

        // Nothing has been pressed yet, so the whole declaration waits in 更多输入 - the
        // daemon's snapshot follows the same display strategy the local path does.
        Assert.Equal(
            ["左摇杆", "A", "B", "LB", "LT", "Guide", "附加键 305"],
            viewModel.GenericPanel.MoreInputItems.Select(item => item.Label).ToArray());

        // 显示全部输入 promotes the wait onto the panel, and the Core's five zones fill in
        // the declaration order - the snapshot's arrangement is the panel's.
        viewModel.GenericPanel.ToggleShowAllCommand.Execute(null);

        Assert.Equal(
            ["左摇杆"],
            viewModel.GenericPanel.DirectionItems.Select(item => item.Label).ToArray());
        Assert.Equal(["A", "B"], viewModel.GenericPanel.MainButtonItems.Select(item => item.Label).ToArray());
        Assert.Equal(["LB", "LT"], viewModel.GenericPanel.ShoulderItems.Select(item => item.Label).ToArray());
        Assert.Equal(["Guide"], viewModel.GenericPanel.FunctionItems.Select(item => item.Label).ToArray());
        Assert.Equal(
            ["附加键 305"],
            viewModel.GenericPanel.ExtensionItems.Select(item => item.Label).ToArray());

        // The capture lease travels over IPC: the daemon holds it for the device in scope,
        // and releasing the capture releases it there too.
        var guide = viewModel.GenericPanel.Items.Single(item => item.Label == "Guide");
        viewModel.GenericPanel.OpenControlRequested!(guide);

        viewModel.Editor.IsCapturingButton = true;
        await WaitForCaptureAsync(() => context.DaemonHost.CapturedDeviceId == "pad-1", "the daemon's capture lease");

        viewModel.Editor.IsCapturingButton = false;
        await WaitForCaptureAsync(() => context.DaemonHost.CapturedDeviceId is null, "the lease's release");
    }

    [Fact]
    public async Task A_Bound_Source_The_Device_Never_Declared_Joins_The_Panel_As_An_Extension()
    {
        // A legacy config: the profile maps a raw code the device's declaration never names.
        // A bound control is always shown, so it joins the panel as an extension - in the
        // Core's own naming, from the raw code's ordinal - instead of sitting invisible.
        var profile = new GamepadProfile
        {
            Name = "legacy",
            DeviceId = DeviceId,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.Extra, 999),
                    Target = new KeyPressTarget(GamepadKey.Space),
                },
            ],
        };
        var (viewModel, host, provider) = BuildLocal(profile, DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var panel = viewModel.GenericPanel;
        Assert.Equal(DeclaredControls().Count + 1, panel.Items.Count);

        var merged = panel.Items.Single(item => item.Label == "按钮 1000");
        Assert.Equal(new ButtonBinding(GamepadButton.Extra, 999), merged.Source);
        Assert.Equal(GamepadControlKind.Other, merged.Kind);
        Assert.Equal(GamepadControlZone.Extensions, merged.Zone);

        // Bound means always shown: it holds its slot from the first sample, never waits in
        // 更多输入, and refuses to be hidden - a mapping the user made stays reachable.
        Assert.True(merged.IsBound);
        Assert.True(merged.IsShown);
        Assert.False(merged.IsInMoreInputs);
        Assert.False(merged.CanHide);
        Assert.Contains(merged, panel.ExtensionItems);
        Assert.Equal(DeclaredControls().Count, panel.MoreInputCount);

        merged.HideCommand.Execute(null);
        Assert.Contains(merged, panel.ExtensionItems);

        // 显示全部输入 moves the declaration's own wait onto the panel and changes nothing
        // about the merged control or its mapping.
        panel.ToggleShowAllCommand.Execute(null);

        Assert.Contains(merged, panel.ExtensionItems);
        Assert.Single(viewModel.ActiveProfile!.Mappings);
        Assert.Equal(
            new ButtonBinding(GamepadButton.Extra, 999),
            viewModel.ActiveProfile.Mappings[0].Source);
    }

    [Fact]
    public async Task The_Merge_Keeps_One_Item_Per_Physical_Control()
    {
        // Four directions on the declared left stick, and a double-click gesture on a
        // declared face button: both are configuration ON a control the declaration already
        // names, so neither is appended a second time - the match is physical identity, not
        // the raw binding a mapping happens to carry.
        var profile = new GamepadProfile
        {
            Name = "directions",
            DeviceId = DeviceId,
            Mappings =
            [
                .. Enum.GetValues<InputDirection>().Select(direction => new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Left) { Direction = direction },
                    Target = new KeyPressTarget(GamepadKey.Space),
                }),
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.A)
                    {
                        Gesture = new ButtonGesture { DoubleClickWindow = ButtonGesture.DefaultDoubleClickWindow },
                    },
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
            ],
        };
        var (viewModel, host, provider) = BuildLocal(profile, DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var panel = viewModel.GenericPanel;
        Assert.Equal(DeclaredControls().Count, panel.Items.Count);
        Assert.Single(panel.Items, item => item.Label == "左摇杆");
        Assert.Single(panel.Items, item => item.Label == "A");

        // Both are bound - the directions and the gesture are mappings on them - so both
        // hold their slots without a second card for any of them.
        Assert.True(panel.Items.Single(item => item.Label == "左摇杆").IsBound);
        Assert.True(panel.Items.Single(item => item.Label == "A").IsBound);
        Assert.Contains(panel.Items.Single(item => item.Label == "左摇杆"), panel.DirectionItems);
        Assert.Contains(panel.Items.Single(item => item.Label == "A"), panel.MainButtonItems);
    }

    [Fact]
    public async Task The_Merged_Sources_Are_Appended_In_The_Profiles_Own_Order()
    {
        var profile = new GamepadProfile
        {
            Name = "raw",
            DeviceId = DeviceId,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.Extra, 999),
                    Target = new KeyPressTarget(GamepadKey.Space),
                },
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.Extra, 998),
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
            ],
        };
        var (viewModel, host, provider) = BuildLocal(profile, DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var panel = viewModel.GenericPanel;
        panel.ToggleShowAllCommand.Execute(null);

        // The declaration first, the bound extras after it in the profile's order - the
        // append order is fixed, so a reload or another client's edit cannot shuffle them.
        Assert.Equal(
            ["附加键 305", "按钮 1000", "按钮 999"],
            panel.ExtensionItems.Select(item => item.Label).ToArray());
    }

    [Fact]
    public async Task A_Commit_That_Binds_An_Undeclared_Source_Brings_It_Onto_The_Panel()
    {
        var profile = new GamepadProfile
        {
            Name = "committing",
            DeviceId = DeviceId,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.A),
                    Target = new KeyPressTarget(GamepadKey.Space),
                },
            ],
        };
        var (viewModel, host, provider) = BuildLocal(profile, DeclaredControls());
        await using var disposed = viewModel;

        Connect(provider, DeclaredControls());
        Sample(viewModel, host);

        var panel = viewModel.GenericPanel;
        Assert.Equal(DeclaredControls().Count, panel.Items.Count);

        // A press latches a control, and the latch must survive the commit below - the
        // panel follows the declaration without reloading itself.
        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.A, 0f, 0f, 0f, 0f));
        Sample(viewModel, host);
        var a = panel.Items.Single(item => item.Label == "A");
        Assert.True(a.IsDetected);

        // The user maps a raw code the device never declared - through the editor, not
        // through a card, because there is no card for it yet.
        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.Extra, 999));
        viewModel.Editor.TargetKind = MappingTargetKind.KeyPress;
        viewModel.Editor.SelectedKey = GamepadKey.Space;
        viewModel.CommitEditorCommand.Execute(null);

        // The panel gained the control without losing anything it held.
        Assert.Equal(DeclaredControls().Count + 1, panel.Items.Count);
        var merged = panel.Items.Single(item => item.Label == "按钮 1000");
        Assert.True(merged.IsBound);
        Assert.Contains(merged, panel.ExtensionItems);
        Assert.True(a.IsDetected, "the commit follows the declaration, it does not reload it");
        Assert.Contains(a, panel.MainButtonItems);
    }
}
