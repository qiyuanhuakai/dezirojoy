using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The sensitivity panel reports the precision key the engine actually reads. That key
/// lives on the profile's precision special-key mapping: migration lifts it out of the
/// mouse mapping's own settings and leaves those fields at their defaults, and the engine
/// ignores the fields once a special key exists. A panel that read only the fields showed
/// the default while the engine ran the special key - the wrong button and the wrong mode
/// on the very surface a user calibrates aiming on. These pin the derivation, the
/// migration shape it has to survive, and that applying sensitivity never moves the key.
/// </summary>
public sealed class MouseSensitivityPrecisionTests
{
    private const string DeviceId = "pad-01";

    private static readonly TimeSpan AdoptionBudget = TimeSpan.FromSeconds(30);

    private static GamepadProfile ProfileWithPrecisionKey(
        GamepadButton button,
        PrecisionMode mode,
        MouseStickSettings? mouse = null)
        => new()
        {
            Name = "precision",
            DeviceId = DeviceId,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right),
                    Target = new MouseMoveTarget(mouse ?? new MouseStickSettings()),
                },
                new MappingEntry
                {
                    Source = new ButtonBinding(button),
                    Target = new SpecialKeyTarget(SpecialKeyKind.Precision, mode),
                },
            ],
        };

    /// <summary>
    /// A view model over a store that already holds <paramref name="seeded"/> as the
    /// device's own profile, with the device connected: the local path every provider -
    /// SDL3, evdev - feeds, where the host loads the stored profile on the device's first
    /// appearance.
    /// </summary>
    private static (MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider) BuildLocal(
        GamepadProfile seeded)
    {
        var provider = new FakeGamepadProvider();
        var store = new InMemoryProfileStore();
        store.Save(seeded);
        var host = new EngineHost(provider, new RecordingOutputSink(), store);
        var viewModel = new MainWindowViewModel(host, store);
        return (viewModel, host, provider);
    }

    private static void ConnectAndSample(MainWindowViewModel viewModel, EngineHost host, FakeGamepadProvider provider)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        SampleHelper.Sample(viewModel, host);
    }

    [Fact]
    public async Task A_Custom_Precision_Key_And_Toggle_Mode_Is_What_The_Panel_Reports()
    {
        var (viewModel, host, provider) = BuildLocal(
            ProfileWithPrecisionKey(GamepadButton.RightBumper, PrecisionMode.Toggle));
        await using var disposed = viewModel;

        ConnectAndSample(viewModel, host, provider);

        // The mouse mapping's own fields are the defaults here, exactly as migration leaves
        // them: what the panel reports is the special key, not the fields.
        var mouse = Assert.IsType<MouseMoveTarget>(
            viewModel.ActiveProfile!.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Right }).Target);
        Assert.Equal(GamepadButton.LeftStick, mouse.Settings.PrecisionButton);
        Assert.Equal(PrecisionMode.Hold, mouse.Settings.PrecisionMode);

        Assert.Equal(GamepadButton.RightBumper, viewModel.MouseSensitivity.PrecisionButton);
        Assert.Equal(PrecisionMode.Toggle, viewModel.MouseSensitivity.PrecisionMode);
        Assert.Equal("按一下启用，再按一下取消", viewModel.MouseSensitivity.PrecisionModeLabel);
        Assert.Equal(MappingDescriptions.Button(GamepadButton.RightBumper), viewModel.MouseSensitivity.PrecisionButtonLabel);
    }

    [Fact]
    public async Task A_Profile_Without_A_Precision_Key_Falls_Back_To_The_Mouse_Mappings_Own_Fields()
    {
        // The other half of the rule: an un-migrated profile - or one whose precision key
        // was removed - runs on the mouse mapping's own fields, and the panel reports those.
        var (viewModel, host, provider) = BuildLocal(new GamepadProfile
        {
            Name = "legacy-fields",
            DeviceId = DeviceId,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right),
                    Target = new MouseMoveTarget(new MouseStickSettings
                    {
                        PrecisionButton = GamepadButton.RightBumper,
                        PrecisionMode = PrecisionMode.Toggle,
                    }),
                },
            ],
        });
        await using var disposed = viewModel;

        ConnectAndSample(viewModel, host, provider);

        Assert.Equal(GamepadButton.RightBumper, viewModel.MouseSensitivity.PrecisionButton);
        Assert.Equal(PrecisionMode.Toggle, viewModel.MouseSensitivity.PrecisionMode);
    }

    [Fact]
    public async Task A_Disabled_Precision_Key_Leaves_The_Fields_In_Charge()
    {
        // The engine registers only enabled special-key mappings, so a disabled precision
        // key leaves the legacy fields in charge - and the panel must agree with the engine
        // rather than quoting a key that engages nothing.
        var (viewModel, host, provider) = BuildLocal(new GamepadProfile
        {
            Name = "disabled-key",
            DeviceId = DeviceId,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right),
                    Target = new MouseMoveTarget(new MouseStickSettings
                    {
                        PrecisionButton = GamepadButton.RightBumper,
                        PrecisionMode = PrecisionMode.Toggle,
                    }),
                },
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.LeftBumper),
                    Target = new SpecialKeyTarget(SpecialKeyKind.Precision, PrecisionMode.Hold),
                    Enabled = false,
                },
            ],
        });
        await using var disposed = viewModel;

        ConnectAndSample(viewModel, host, provider);

        Assert.Equal(GamepadButton.RightBumper, viewModel.MouseSensitivity.PrecisionButton);
        Assert.Equal(PrecisionMode.Toggle, viewModel.MouseSensitivity.PrecisionMode);
    }

    [Fact]
    public async Task A_Migrated_Profile_Reports_The_Lifted_Key_While_The_Fields_Stay_Default()
    {
        // The root cause, end to end: a v1 profile carries the precision key inside the
        // mouse mapping's settings. Every real store read migrates it - the key is lifted
        // into its own special-key mapping and the fields are reset to their defaults -
        // and the engine then runs the lifted key. The panel has to report the lifted key,
        // because the fields now describe nothing the engine reads.
        var v1 = new GamepadProfile
        {
            Name = "legacy",
            SchemaVersion = 1,
            DeviceId = DeviceId,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right),
                    Target = new MouseMoveTarget(new MouseStickSettings
                    {
                        PrecisionButton = GamepadButton.RightBumper,
                        PrecisionMode = PrecisionMode.Toggle,
                    }),
                },
            ],
        };

        var migrated = ProfileMigration.Migrate(v1);

        // The premise: migration lifted the key and reset the fields.
        var migratedMouse = Assert.IsType<MouseMoveTarget>(
            migrated.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Right }).Target);
        Assert.Equal(GamepadButton.LeftStick, migratedMouse.Settings.PrecisionButton);
        Assert.Equal(PrecisionMode.Hold, migratedMouse.Settings.PrecisionMode);
        var lifted = Assert.IsType<SpecialKeyTarget>(
            migrated.Mappings.Single(m => m.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Precision }).Target);
        Assert.Equal(PrecisionMode.Toggle, lifted.PrecisionMode);
        Assert.Equal(
            new ButtonBinding(GamepadButton.RightBumper),
            migrated.Mappings.Single(m => m.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Precision }).Source);

        var (viewModel, host, provider) = BuildLocal(migrated);
        await using var disposed = viewModel;

        ConnectAndSample(viewModel, host, provider);

        Assert.Equal(GamepadButton.RightBumper, viewModel.MouseSensitivity.PrecisionButton);
        Assert.Equal(PrecisionMode.Toggle, viewModel.MouseSensitivity.PrecisionMode);
    }

    [Fact]
    public async Task Applying_Sensitivity_Keeps_The_Precision_Key_And_The_Fields_Agree_With_It()
    {
        var (viewModel, host, provider) = BuildLocal(
            ProfileWithPrecisionKey(GamepadButton.RightBumper, PrecisionMode.Toggle));
        await using var disposed = viewModel;

        ConnectAndSample(viewModel, host, provider);

        viewModel.MouseSensitivity.MaxSpeed = 2400f;
        await viewModel.ApplyMouseSensitivityCommand.ExecuteAsync(null);

        // The edit landed on the mouse mapping...
        var mouse = Assert.IsType<MouseMoveTarget>(
            viewModel.ActiveProfile!.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Right }).Target);
        Assert.Equal(2400f, mouse.Settings.MaxSpeed);

        // ...the precision special key is exactly where it was, untouched...
        var key = viewModel.ActiveProfile.Mappings.Single(m => m.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Precision });
        Assert.Equal(new ButtonBinding(GamepadButton.RightBumper), key.Source);
        Assert.Equal(PrecisionMode.Toggle, Assert.IsType<SpecialKeyTarget>(key.Target).PrecisionMode);

        // ...and the legacy fields are written from the key rather than reset to defaults,
        // so the body stays self-consistent if the key is ever removed.
        Assert.Equal(GamepadButton.RightBumper, mouse.Settings.PrecisionButton);
        Assert.Equal(PrecisionMode.Toggle, mouse.Settings.PrecisionMode);

        // The reload after the apply keeps reporting the key.
        Assert.Equal(GamepadButton.RightBumper, viewModel.MouseSensitivity.PrecisionButton);
        Assert.Equal(PrecisionMode.Toggle, viewModel.MouseSensitivity.PrecisionMode);
    }

    [Fact]
    public async Task A_Daemon_Snapshot_And_Its_Re_Adoption_Report_The_Key_The_Daemon_Carries()
    {
        // Daemon mode is where the panel matters most - the profile arrives as the daemon's
        // snapshot body - and a second profile re-adopted from a newer snapshot has to move
        // the report with it.
        var context = await MainWindowViewModelDaemonTests.CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        var first = ProfileWithPrecisionKey(GamepadButton.RightBumper, PrecisionMode.Toggle);
        var accepted = Assert.IsType<MutationAckMessage>(await context.Server.ProcessAsync(
            new ApplyConfigurationMessage("apply-1", 0, first.Name, ProfileSerializer.Serialize(first))));
        Assert.True(accepted.Accepted);
        await WaitForPrecisionReport(viewModel, GamepadButton.RightBumper, PrecisionMode.Toggle);

        var second = ProfileWithPrecisionKey(GamepadButton.LeftBumper, PrecisionMode.Hold);
        var reAccepted = Assert.IsType<MutationAckMessage>(await context.Server.ProcessAsync(
            new ApplyConfigurationMessage("apply-2", 1, second.Name, ProfileSerializer.Serialize(second))));
        Assert.True(reAccepted.Accepted);
        await WaitForPrecisionReport(viewModel, GamepadButton.LeftBumper, PrecisionMode.Hold);

        // The panel's report is the daemon's own body: that body's legacy precision fields
        // are the defaults - the engine reads the special key, not them - and the report is
        // the key all the same. This is the shape a migrated or daemon-stored profile
        // arrives in, and the whole point of the derivation.
        var mouse = Assert.IsType<MouseMoveTarget>(
            viewModel.ActiveProfile!.Mappings.Single(m => m.Source is StickBinding { Stick: GamepadStick.Right }).Target);
        Assert.Equal(GamepadButton.LeftStick, mouse.Settings.PrecisionButton);
        Assert.Equal(PrecisionMode.Hold, mouse.Settings.PrecisionMode);
        Assert.Equal(GamepadButton.LeftBumper, viewModel.MouseSensitivity.PrecisionButton);
        Assert.Equal(PrecisionMode.Hold, viewModel.MouseSensitivity.PrecisionMode);
    }

    /// <summary>
    /// Waits until the panel reports the named key. The panel is reloaded from the profile
    /// after the adoption applies it, so the report - not the snapshot property - is the
    /// observable that says the derivation ran, and the initial check keeps a report that
    /// landed before the subscription from being waited for twice.
    /// </summary>
    private static async Task WaitForPrecisionReport(
        MainWindowViewModel viewModel,
        GamepadButton button,
        PrecisionMode mode)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (viewModel.MouseSensitivity.PrecisionButton == button
                && viewModel.MouseSensitivity.PrecisionMode == mode)
            {
                reached.TrySetResult();
            }
        }

        viewModel.MouseSensitivity.PropertyChanged += OnPropertyChanged;
        try
        {
            if (viewModel.MouseSensitivity.PrecisionButton != button
                || viewModel.MouseSensitivity.PrecisionMode != mode)
            {
                await reached.Task.WaitAsync(AdoptionBudget, TestContext.Current.CancellationToken);
            }
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"the panel never reported {button}/{mode}: it shows " +
                $"{viewModel.MouseSensitivity.PrecisionButton}/{viewModel.MouseSensitivity.PrecisionMode}");
        }
        finally
        {
            viewModel.MouseSensitivity.PropertyChanged -= OnPropertyChanged;
        }
    }
}
