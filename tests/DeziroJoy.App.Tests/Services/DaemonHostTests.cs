using DeziroJoy.App.Services;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Windows.Output;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The daemon's only user-visible contract is its log: a startup line naming the platform,
/// the devices it found and the fact that the engine is running, plus a clean shutdown
/// line. Both are rendered by pure helpers so they can be asserted without a process.
/// </summary>
public class DaemonHostTests
{
    [Fact]
    public void Startup_Summary_Names_The_Platform_The_Devices_And_The_Engine()
    {
        using var composition = new EngineComposition();

        var summary = DaemonHost.BuildStartupSummary(composition, TimeSpan.FromMilliseconds(2));

        Assert.Contains("daemon", summary);
        Assert.Contains(EngineComposition.PlatformName, summary);
        Assert.Contains($"devices detected: {composition.Host.Controllers.Count}", summary);
        Assert.Contains("engine started", summary);
    }

    [Fact]
    public void Startup_Summary_States_The_Elevation_Position()
    {
        using var composition = new EngineComposition();

        var summary = DaemonHost.BuildStartupSummary(composition, TimeSpan.FromMilliseconds(2));

        if (composition.IsElevationSupported)
        {
            Assert.Contains(composition.IsElevated ? "elevated" : "not elevated", summary);
        }
        else
        {
            Assert.Contains("not applicable", summary);
        }
    }

    [Fact]
    public void Startup_Summary_Names_The_Output_Sink_Actually_In_Use()
    {
        using var composition = new EngineComposition();

        var summary = DaemonHost.BuildStartupSummary(composition, TimeSpan.FromMilliseconds(2));

        Assert.Contains(composition.Sink.GetType().Name, summary);
    }

    [Fact]
    public void Shutdown_Summary_Reports_A_Clean_Exit()
    {
        using var composition = new EngineComposition();

        var summary = DaemonHost.BuildShutdownSummary(composition);

        Assert.Contains("engine stopped", summary);
        Assert.Contains("daemon exited cleanly", summary);
    }

    [Fact]
    public void Startup_Summary_Rejects_A_Null_Composition()
    {
        Assert.Throws<ArgumentNullException>(() => DaemonHost.BuildStartupSummary(null!, TimeSpan.FromMilliseconds(2)));
    }

    [Fact]
    public void Shutdown_Summary_Dumps_The_Injection_Log_So_A_Failed_Mapping_Has_Evidence()
    {
        using var composition = new EngineComposition();

        var summary = DaemonHost.BuildShutdownSummary(composition);

        Assert.Contains("engine stopped", summary);
        Assert.Contains("daemon exited cleanly", summary);
        Assert.Contains("injected:", summary);
    }

    [Fact]
    public void Shutdown_Summary_States_The_Injection_Count_Without_The_Log()
    {
        using var composition = new EngineComposition();

        var summary = DaemonHost.BuildShutdownSummary(composition, injectionLog: null);

        Assert.Contains("engine stopped", summary);
        Assert.Contains("injected: unavailable", summary);
    }

    [Fact]
    public void Shutdown_Summary_Counts_What_The_Log_Recorded()
    {
        var log = new InjectionLog();
        log.Record(new InjectionRecord(1, InjectionAction.KeyDown, (int)GamepadKey.Space, 0, true));
        log.Record(new InjectionRecord(2, InjectionAction.KeyUp, (int)GamepadKey.Space, 0, true));

        using var composition = new EngineComposition();
        var summary = DaemonHost.BuildShutdownSummary(composition, log);

        Assert.Contains("injected: 2 events", summary);
    }

    [Fact]
    public void Shutdown_Summary_Reports_Blocked_Injections_Separately()
    {
        var log = new InjectionLog();
        log.Record(new InjectionRecord(1, InjectionAction.KeyDown, (int)GamepadKey.Space, 0, true));
        log.Record(new InjectionRecord(2, InjectionAction.KeyUp, (int)GamepadKey.Space, 0, false));

        using var composition = new EngineComposition();
        var summary = DaemonHost.BuildShutdownSummary(composition, log);

        Assert.Contains("injected: 2 events", summary);
        Assert.Contains("1 blocked", summary);
    }
}
