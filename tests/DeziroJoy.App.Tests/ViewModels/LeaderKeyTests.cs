using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The leader key swaps the active mapping between the device's own profile and a second
/// profile the user saved as the alternate. It is a hijack feature, so it is scoped like
/// navigation: invisible, out-of-scope or switched-off pads never swap.
/// </summary>
public sealed class LeaderKeyTests
{
    private const string DeviceId = "xinput:0";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 64.0);

    private static (MainWindowViewModel Vm, EngineHost Host, FakeGamepadProvider Provider, InMemoryProfileStore Store, ThemeService Settings)
        CreateHost(ThemeService settings)
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var vm = new MainWindowViewModel(host, store, settings: settings);

        return (vm, host, provider, store, settings);
    }

    private static void Connect(FakeGamepadProvider provider)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
    }

    private static void Sample(MainWindowViewModel vm, EngineHost host)
    {
        ControllerSampledEventArgs? sampled = null;
        host.Sampled += (_, args) => sampled = args;

        try
        {
            host.TickOnce(Tick);
        }
        finally
        {
            host.Sampled -= (_, args) => sampled = args;
        }

        if (sampled is not null)
        {
            vm.ApplySample(sampled);
        }
    }

    private static void Press(FakeGamepadProvider provider, GamepadButtonMask buttons)
        => provider.SetState(DeviceId, GamepadState.Create(buttons, 0f, 0f, 0f, 0f));

    private static ThemeService Settings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dezirojoy-leader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        return new ThemeService(directory);
    }

    [Fact]
    public async Task Pressing_The_Leader_Swaps_Between_The_Two_Profiles()
    {
        var settings = Settings();
        var (vm, host, provider, store, _) = CreateHost(settings);
        await using var _ = vm;

        var primary = new GamepadProfile { Name = "Primary", DeviceId = DeviceId, Mappings = [] };
        var alternate = new GamepadProfile { Name = "Alternate", DeviceId = DeviceId, Mappings = [] };

        store.Save(primary);
        vm.SaveLeaderAlternate(alternate);
        settings.SetLeader(GamepadButton.Guide, LeaderMode.Press);

        Connect(provider);
        Sample(vm, host);

        Assert.Equal("Primary", vm.ActiveProfile!.Name);
        Assert.Equal(LeaderSlot.Primary, vm.ActiveLeaderSlot);

        Press(provider, GamepadButtonMask.Guide);
        Sample(vm, host);

        Assert.Equal("Alternate", vm.ActiveProfile!.Name);
        Assert.Equal(LeaderSlot.Alternate, vm.ActiveLeaderSlot);

        Press(provider, default);
        Sample(vm, host);

        Press(provider, GamepadButtonMask.Guide);
        Sample(vm, host);

        Assert.Equal("Primary", vm.ActiveProfile!.Name);
        Assert.Equal(LeaderSlot.Primary, vm.ActiveLeaderSlot);
    }

    [Fact]
    public async Task Holding_The_Leader_Swaps_Only_While_Held()
    {
        var settings = Settings();
        var (vm, host, provider, store, _) = CreateHost(settings);
        await using var _ = vm;

        var primary = new GamepadProfile { Name = "Primary", DeviceId = DeviceId, Mappings = [] };
        var alternate = new GamepadProfile { Name = "Alternate", DeviceId = DeviceId, Mappings = [] };

        store.Save(primary);
        vm.SaveLeaderAlternate(alternate);
        settings.SetLeader(GamepadButton.Guide, LeaderMode.Hold);

        Connect(provider);
        Sample(vm, host);

        Assert.Equal("Primary", vm.ActiveProfile!.Name);
        Assert.Equal(LeaderSlot.Primary, vm.ActiveLeaderSlot);

        Press(provider, GamepadButtonMask.Guide);
        Sample(vm, host);

        Assert.Equal("Alternate", vm.ActiveProfile!.Name);
        Assert.Equal(LeaderSlot.Alternate, vm.ActiveLeaderSlot);

        Press(provider, default);
        Sample(vm, host);

        Assert.Equal("Primary", vm.ActiveProfile!.Name);
        Assert.Equal(LeaderSlot.Primary, vm.ActiveLeaderSlot);
    }

    [Fact]
    public async Task Without_A_Leader_Button_Nothing_Swaps()
    {
        var settings = Settings();
        var (vm, host, provider, store, _) = CreateHost(settings);
        await using var _ = vm;

        var primary = new GamepadProfile { Name = "Primary", DeviceId = DeviceId, Mappings = [] };
        var alternate = new GamepadProfile { Name = "Alternate", DeviceId = DeviceId, Mappings = [] };

        store.Save(primary);
        vm.SaveLeaderAlternate(alternate);

        Connect(provider);
        Sample(vm, host);

        Press(provider, GamepadButtonMask.Guide);
        Sample(vm, host);

        Assert.Equal("Primary", vm.ActiveProfile!.Name);
        Assert.Equal(LeaderSlot.Primary, vm.ActiveLeaderSlot);
    }

    [Fact]
    public async Task Out_Of_Scope_The_Leader_Does_Not_Swap()
    {
        var settings = Settings();
        var (vm, host, provider, store, _) = CreateHost(settings);
        await using var _ = vm;

        var primary = new GamepadProfile { Name = "Primary", DeviceId = DeviceId, Mappings = [] };
        var alternate = new GamepadProfile { Name = "Alternate", DeviceId = DeviceId, Mappings = [] };

        store.Save(primary);
        vm.SaveLeaderAlternate(alternate);
        settings.SetLeader(GamepadButton.Guide, LeaderMode.Press);

        Connect(provider);
        Sample(vm, host);
        vm.SetPhysicalInputScope(false);

        Press(provider, GamepadButtonMask.Guide);
        Sample(vm, host);

        Assert.Equal("Primary", vm.ActiveProfile!.Name);
    }
}
