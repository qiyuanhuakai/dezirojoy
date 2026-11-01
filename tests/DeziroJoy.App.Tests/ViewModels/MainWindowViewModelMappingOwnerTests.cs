using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Platform;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The GUI must tell the truth about WHO is injecting. The resident daemon runs its own
/// <see cref="EngineHost"/> in its own process, so this window's host says nothing about
/// it; the injection mutex does. A start the mutex refuses must surface as a refusal,
/// never as a start that appeared to succeed.
/// </summary>
public class MainWindowViewModelMappingOwnerTests
{
    private static string UniqueName() => $"Global\\DeziroJoy.Injection.Tests.{Guid.NewGuid():N}";

    private static (MainWindowViewModel Vm, EngineHost Host) CreateViewModel(string mutexName)
    {
        var provider = new NoGamepadProvider();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, new NullOutputSink(), store, mutexName);

        return (new MainWindowViewModel(host, store), host);
    }

    [Fact]
    public void A_Successful_Start_Reports_This_Window_As_The_Injector()
    {
        var name = UniqueName();
        var (vm, host) = CreateViewModel(name);

        vm.ToggleEngineCommand.Execute(null);

        Assert.True(vm.IsEngineRunning);
        Assert.True(host.IsRunning);
        Assert.Equal(MappingOwner.ThisWindow, vm.MappingOwner);
        Assert.Contains("本窗口", vm.MappingOwnerDescription);

        vm.ToggleEngineCommand.Execute(null);
    }

    [Fact]
    public void A_Refused_Start_Says_Another_Process_Is_Injecting()
    {
        var name = UniqueName();
        var (vm, host) = CreateViewModel(name);

        // Stand in for the resident daemon: another process holding the slot.
        using var daemon = InjectionMutex.TryAcquire(name);
        Assert.NotNull(daemon);

        vm.ToggleEngineCommand.Execute(null);

        Assert.False(vm.IsEngineRunning);
        Assert.False(host.IsRunning);
        Assert.Equal(MappingOwner.AnotherProcess, vm.MappingOwner);
        Assert.Contains("另一个进程", vm.MappingOwnerDescription);
        Assert.Contains("拒绝", vm.MappingOwnerDescription);
    }

    [Fact]
    public void A_Refused_Start_Never_Reports_The_Engine_As_Running()
    {
        var name = UniqueName();
        var (vm, host) = CreateViewModel(name);

        using var daemon = InjectionMutex.TryAcquire(name);
        Assert.NotNull(daemon);

        vm.ToggleEngineCommand.Execute(null);

        Assert.False(vm.IsEngineRunning);
        Assert.False(host.IsRunning);
        Assert.DoesNotContain("映射未启动", vm.StatusMessage == string.Empty ? string.Empty : vm.StatusMessage);
    }

    [Fact]
    public void Stopping_Reports_That_Nothing_Is_Injecting()
    {
        var name = UniqueName();
        var (vm, _) = CreateViewModel(name);

        vm.ToggleEngineCommand.Execute(null);
        Assert.Equal(MappingOwner.ThisWindow, vm.MappingOwner);

        vm.ToggleEngineCommand.Execute(null);

        Assert.False(vm.IsEngineRunning);
        Assert.Equal(MappingOwner.None, vm.MappingOwner);
    }

    [Fact]
    public void The_Owner_Description_Distinguishes_Nothing_This_Window_And_Another_Process()
    {
        var name = UniqueName();
        var (vm, _) = CreateViewModel(name);

        Assert.Equal(MappingOwner.None, vm.MappingOwner);
        var nothing = vm.MappingOwnerDescription;

        vm.ToggleEngineCommand.Execute(null);
        var own = vm.MappingOwnerDescription;
        vm.ToggleEngineCommand.Execute(null);

        using var daemon = InjectionMutex.TryAcquire(name);
        Assert.NotNull(daemon);
        vm.ToggleEngineCommand.Execute(null);
        var other = vm.MappingOwnerDescription;

        Assert.Equal(3, new[] { nothing, own, other }.Distinct().Count());
        Assert.Contains("本窗口", own);
        Assert.Contains("另一个进程", other);
    }

    [Fact]
    public void The_Owner_Description_Is_Republished_When_The_Owner_Changes()
    {
        var name = UniqueName();
        var (vm, _) = CreateViewModel(name);
        var republished = new List<string>();
        vm.PropertyChanged += (_, args) => republished.Add(args.PropertyName ?? string.Empty);

        vm.ToggleEngineCommand.Execute(null);

        Assert.Contains(nameof(MainWindowViewModel.MappingOwner), republished);
        Assert.Contains(nameof(MainWindowViewModel.MappingOwnerDescription), republished);

        vm.ToggleEngineCommand.Execute(null);
    }

    [Fact]
    public void The_Injector_Description_Still_Names_This_Window_And_Is_Not_Reworded()
    {
        var name = UniqueName();
        var (vm, _) = CreateViewModel(name);

        // The elevation members describe the integrity of the process that injects; the
        // owner members describe which process is injecting. They must stay distinct.
        Assert.Contains("主窗口", vm.InjectorDescription);
        Assert.DoesNotContain("另一个进程", vm.InjectorDescription);
        Assert.DoesNotContain("本窗口", vm.InjectorDescription);
    }
}
