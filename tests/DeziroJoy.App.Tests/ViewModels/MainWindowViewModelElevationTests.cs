using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// Elevation state has to be visible in the UI: a medium-integrity process cannot inject
/// into a high-integrity target and SendInput fails silently, so the only way the user can
/// tell "UIPI blocked me" from "it worked" is if the app says which side it is on.
/// </summary>
public class MainWindowViewModelElevationTests
{
    private static MainWindowViewModel CreateViewModel(IElevationService elevation)
    {
        var provider = new FakeGamepadProvider();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, new RecordingOutputSink(), store);

        return new MainWindowViewModel(host, store, elevation);
    }

    [Fact]
    public void Unelevated_Process_Reports_That_Elevation_Is_Available()
    {
        var vm = CreateViewModel(new StubElevationService(isSupported: true, isElevated: false));

        Assert.True(vm.IsElevationSupported);
        Assert.False(vm.IsElevated);
        Assert.True(vm.CanRelaunchElevated);
        Assert.Contains("未提权", vm.ElevationStatusText);
    }

    [Fact]
    public void Elevated_Process_Reports_That_It_Is_Already_Elevated()
    {
        var vm = CreateViewModel(new StubElevationService(isSupported: true, isElevated: true));

        Assert.True(vm.IsElevationSupported);
        Assert.True(vm.IsElevated);
        Assert.False(vm.CanRelaunchElevated);
        Assert.Contains("管理员", vm.ElevationStatusText);
    }

    [Fact]
    public void Platform_Without_Uipi_Reports_That_Elevation_Is_Not_Needed()
    {
        var vm = CreateViewModel(new StubElevationService(isSupported: false, isElevated: false));

        Assert.False(vm.IsElevationSupported);
        Assert.False(vm.IsElevated);
        Assert.False(vm.CanRelaunchElevated);
        Assert.Contains("无需提权", vm.ElevationStatusText);
    }

    [Fact]
    public void Relaunch_Elevated_Command_Asks_The_Service_To_Relaunch()
    {
        var service = new StubElevationService(isSupported: true, isElevated: false);
        var vm = CreateViewModel(service);

        vm.RelaunchElevatedCommand.Execute(null);

        Assert.Equal(1, service.RelaunchCount);
    }

    [Fact]
    public void Relaunch_Elevated_Raises_The_Handover_Request_So_The_View_Can_Shut_Down()
    {
        var service = new StubElevationService(isSupported: true, isElevated: false);
        var vm = CreateViewModel(service);
        var raised = 0;
        vm.ElevationRelaunchRequested += (_, _) => raised++;

        vm.RelaunchElevatedCommand.Execute(null);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_Declined_Uac_Prompt_Does_Not_Ask_The_App_To_Shut_Down()
    {
        var service = new StubElevationService(isSupported: true, isElevated: false) { RelaunchSucceeds = false };
        var vm = CreateViewModel(service);
        var raised = 0;
        vm.ElevationRelaunchRequested += (_, _) => raised++;

        vm.RelaunchElevatedCommand.Execute(null);

        Assert.Equal(1, service.RelaunchCount);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void An_Already_Elevated_Process_Cannot_Relaunch_Again()
    {
        var service = new StubElevationService(isSupported: true, isElevated: true);
        var vm = CreateViewModel(service);

        Assert.False(vm.RelaunchElevatedCommand.CanExecute(null));
    }

    private sealed class StubElevationService : IElevationService
    {
        public StubElevationService(bool isSupported, bool isElevated)
        {
            IsSupported = isSupported;
            IsElevated = isElevated;
        }

        public bool IsSupported { get; }

        public bool IsElevated { get; }

        public bool RelaunchSucceeds { get; init; } = true;

        public int RelaunchCount { get; private set; }

        public bool RelaunchElevated()
        {
            RelaunchCount++;
            return RelaunchSucceeds;
        }
    }
}
