using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The UI must be honest about WHO injects and at what integrity level. The GUI owns the
/// <see cref="EngineHost"/> through <see cref="EngineComposition"/> and its sink calls
/// SendInput in-process, so the injector is always this process - never the daemon, whose
/// scheduled-task run level says nothing about what this window injects. A medium-integrity
/// injector has its input silently dropped by UIPI when it aims at an elevated target, so
/// the status text has to say that plainly and the relaunch has to carry the user's intent.
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
    public void Gui_Engine_Injects_In_Process_So_The_Injector_Is_The_Main_Window()
    {
        var vm = CreateViewModel(new StubElevationService(isSupported: true, isElevated: false));

        Assert.Contains("主窗口", vm.InjectorDescription);
        Assert.DoesNotContain("后台服务", vm.InjectorDescription);
        Assert.DoesNotContain("常驻服务", vm.InjectorDescription);
    }

    [Fact]
    public void Unelevated_Process_Reports_That_Elevation_Is_Available()
    {
        var vm = CreateViewModel(new StubElevationService(isSupported: true, isElevated: false));

        Assert.True(vm.IsElevationSupported);
        Assert.False(vm.IsElevated);
        Assert.True(vm.CanRelaunchElevated);
    }

    [Fact]
    public void Elevated_Process_Reports_That_It_Is_Already_Elevated()
    {
        var vm = CreateViewModel(new StubElevationService(isSupported: true, isElevated: true));

        Assert.True(vm.IsElevationSupported);
        Assert.True(vm.IsElevated);
        Assert.False(vm.CanRelaunchElevated);
    }

    [Fact]
    public void Platform_Without_Uipi_Reports_That_Elevation_Is_Not_Needed()
    {
        var vm = CreateViewModel(new StubElevationService(isSupported: false, isElevated: false));

        Assert.False(vm.IsElevationSupported);
        Assert.False(vm.IsElevated);
        Assert.False(vm.CanRelaunchElevated);
    }

    [Fact]
    public void Unelevated_Injector_Reports_Medium_Integrity()
    {
        var vm = CreateViewModel(new StubElevationService(isSupported: true, isElevated: false));

        Assert.Equal(InjectorIntegrityLevel.Medium, vm.InjectorIntegrity);
        Assert.Contains("未提权", vm.InjectorDescription);
    }

    [Fact]
    public void Elevated_Injector_Reports_Elevated_Integrity()
    {
        var vm = CreateViewModel(new StubElevationService(isSupported: true, isElevated: true));

        Assert.Equal(InjectorIntegrityLevel.Elevated, vm.InjectorIntegrity);
        Assert.Contains("已提权", vm.InjectorDescription);
    }

    [Fact]
    public void Platform_Without_Uipi_Reports_Unknown_Integrity_But_Still_Names_The_Injector()
    {
        var vm = CreateViewModel(new StubElevationService(isSupported: false, isElevated: false));

        Assert.Equal(InjectorIntegrityLevel.Unknown, vm.InjectorIntegrity);
        Assert.Contains("主窗口", vm.InjectorDescription);
    }

    [Fact]
    public void Unelevated_Injector_Status_States_The_Uipi_Consequence()
    {
        var vm = CreateViewModel(new StubElevationService(isSupported: true, isElevated: false));

        Assert.Contains("UIPI", vm.InjectorStatusText);
        Assert.Contains("管理员", vm.InjectorStatusText);
        Assert.Contains("丢弃", vm.InjectorStatusText);
    }

    [Fact]
    public void Elevated_Injector_Status_Says_Injection_Reaches_Elevated_Targets()
    {
        var vm = CreateViewModel(new StubElevationService(isSupported: true, isElevated: true));

        Assert.Contains("可向", vm.InjectorStatusText);
        Assert.Contains("管理员", vm.InjectorStatusText);
    }

    [Fact]
    public void Platform_Without_Uipi_Status_Says_Elevation_Is_Not_Needed()
    {
        var vm = CreateViewModel(new StubElevationService(isSupported: false, isElevated: false));

        Assert.Contains("无需提权", vm.InjectorStatusText);
    }

    [Fact]
    public void Changing_Elevation_Republishes_The_Injector_Properties()
    {
        var vm = CreateViewModel(new StubElevationService(isSupported: true, isElevated: false));
        var republished = new List<string>();
        vm.PropertyChanged += (_, args) => republished.Add(args.PropertyName ?? string.Empty);

        vm.IsElevated = true;

        Assert.Contains(nameof(MainWindowViewModel.InjectorDescription), republished);
        Assert.Contains(nameof(MainWindowViewModel.InjectorIntegrity), republished);
        Assert.Contains(nameof(MainWindowViewModel.InjectorStatusText), republished);
        Assert.Contains(nameof(MainWindowViewModel.CanRelaunchElevated), republished);
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
    public void Unelevated_Process_Can_Relaunch_Elevated()
    {
        var vm = CreateViewModel(new StubElevationService(isSupported: true, isElevated: false));

        Assert.True(vm.RelaunchElevatedCommand.CanExecute(null));
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

    [Fact]
    public void Relaunch_Carries_Resume_Mapping_When_The_Engine_Is_Running()
    {
        var service = new StubElevationService(isSupported: true, isElevated: false);
        var vm = CreateViewModel(service);
        SampleHelper.StartEngineManually(vm, vm.Host);

        vm.RelaunchElevatedCommand.Execute(null);

        Assert.Equal(MainWindowViewModel.ResumeMappingArgument, service.LastArguments);
    }

    [Fact]
    public void Relaunch_Without_A_Running_Engine_Carries_No_Arguments()
    {
        var service = new StubElevationService(isSupported: true, isElevated: false);
        var vm = CreateViewModel(service);

        vm.RelaunchElevatedCommand.Execute(null);

        Assert.Null(service.LastArguments);
    }

    [Fact]
    public void Real_Elevation_Service_Reports_The_Current_Process_Token_On_Windows()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "integrity levels are a Windows concept");

        var service = new ElevationService();
        var vm = CreateViewModel(service);

        Assert.True(vm.IsElevationSupported);
        Assert.Equal(service.IsElevated, vm.IsElevated);
        Assert.Equal(
            service.IsElevated ? InjectorIntegrityLevel.Elevated : InjectorIntegrityLevel.Medium,
            vm.InjectorIntegrity);
        Assert.Contains("主窗口", vm.InjectorDescription);
    }

    [Fact]
    public void Real_Elevation_Service_Is_Unsupported_Off_Windows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows always supports elevation");

        var service = new ElevationService();
        var vm = CreateViewModel(service);

        Assert.False(vm.IsElevationSupported);
        Assert.Equal(InjectorIntegrityLevel.Unknown, vm.InjectorIntegrity);
        Assert.Contains("主窗口", vm.InjectorDescription);
        Assert.Contains("无需提权", vm.InjectorStatusText);
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

        public string? LastArguments { get; private set; }

        public bool RelaunchElevated(string? arguments)
        {
            RelaunchCount++;
            LastArguments = arguments;
            return RelaunchSucceeds;
        }
    }
}
