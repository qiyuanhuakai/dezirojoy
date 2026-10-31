using DeziroJoy.App.Services;
using DeziroJoy.Core.Platform;
using DeziroJoy.Platform.Linux.Evdev;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// Integration smoke test for the real background loop. Runs everywhere: on machines
/// without gamepads the providers simply report none.
/// </summary>
public class EngineHostPlatformSmokeTests
{
    [Fact]
    public void Host_Background_Loop_Runs_And_Stops_Cleanly_On_Evdev()
    {
        using IGamepadProvider provider = new EvdevGamepadProvider();
        using IOutputSink sink = new NullOutputSink();
        using var host = new EngineHost(provider, sink, new InMemoryProfileStore());

        host.Start(TimeSpan.FromMilliseconds(2));
        Thread.Sleep(200);
        host.Stop();

        Assert.False(host.IsRunning);
    }

    [Fact]
    public void Host_Ticks_With_No_Controllers_Without_Throwing()
    {
        using IGamepadProvider provider = new EvdevGamepadProvider();
        using IOutputSink sink = new NullOutputSink();
        using var host = new EngineHost(provider, sink, new InMemoryProfileStore());

        host.TickOnce(TimeSpan.FromMilliseconds(2));

        Assert.Empty(host.Controllers);
    }
}
