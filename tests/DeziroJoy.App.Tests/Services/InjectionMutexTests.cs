using DeziroJoy.App.Services;
using DeziroJoy.Core.Platform;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// One injector at a time, enforced. The engine holds a named, system-wide mutex for
/// exactly as long as it is injecting, so a second engine - the resident daemon, another
/// GUI instance, or a second host in this process - is refused instead of doubling every
/// input. Every test passes its own mutex name so the suite never serialises on one slot.
/// </summary>
public class InjectionMutexTests
{
    private static string UniqueName() => $"Global\\DeziroJoy.Injection.Tests.{Guid.NewGuid():N}";

    private static EngineHost CreateHost(string name)
    {
        var provider = new NoGamepadProvider();
        var sink = new NullOutputSink();
        return new EngineHost(provider, sink, new InMemoryProfileStore(), name);
    }

    [Fact]
    public void A_Second_Host_In_The_Same_Process_Cannot_Start_While_The_First_Is_Injecting()
    {
        var name = UniqueName();
        using var first = CreateHost(name);
        using var second = CreateHost(name);

        first.Start(TimeSpan.FromMilliseconds(2));

        Assert.True(first.IsRunning);
        Assert.Throws<InjectionAlreadyActiveException>(() => second.Start(TimeSpan.FromMilliseconds(2)));
        Assert.False(second.IsRunning);
    }

    [Fact]
    public void Stopping_The_First_Host_Lets_A_Second_One_Start()
    {
        var name = UniqueName();
        using var first = CreateHost(name);
        using var second = CreateHost(name);

        first.Start(TimeSpan.FromMilliseconds(2));
        first.Stop();

        second.Start(TimeSpan.FromMilliseconds(2));

        Assert.True(second.IsRunning);
        second.Stop();
    }

    [Fact]
    public void Disposing_The_First_Host_Releases_The_Mutex()
    {
        var name = UniqueName();
        var first = CreateHost(name);
        first.Start(TimeSpan.FromMilliseconds(2));
        first.Dispose();

        using var second = CreateHost(name);
        second.Start(TimeSpan.FromMilliseconds(2));

        Assert.True(second.IsRunning);
        second.Stop();
    }

    [Fact]
    public void The_Mutex_Is_Free_Again_Once_The_Engine_Stops()
    {
        var name = UniqueName();
        using var host = CreateHost(name);

        host.Start(TimeSpan.FromMilliseconds(2));
        Assert.Null(InjectionMutex.TryAcquire(name));

        host.Stop();
        using var reacquired = InjectionMutex.TryAcquire(name);
        Assert.NotNull(reacquired);
    }

    [Fact]
    public void Starting_An_Already_Running_Host_Keeps_The_Single_Hold()
    {
        var name = UniqueName();
        using var host = CreateHost(name);

        host.Start(TimeSpan.FromMilliseconds(2));
        host.Start(TimeSpan.FromMilliseconds(2));

        Assert.True(host.IsRunning);
        host.Stop();

        using var reacquired = InjectionMutex.TryAcquire(name);
        Assert.NotNull(reacquired);
    }

    [Fact]
    public void A_Refused_Start_Leaves_Nothing_Held_Behind()
    {
        var name = UniqueName();
        using var holder = CreateHost(name);
        using var refused = CreateHost(name);

        holder.Start(TimeSpan.FromMilliseconds(2));
        Assert.Throws<InjectionAlreadyActiveException>(() => refused.Start(TimeSpan.FromMilliseconds(2)));

        // The refused host must not have taken a share of the slot: releasing the holder
        // must leave it free, not leave a phantom hold that blocks the next start.
        holder.Stop();

        using var reacquired = InjectionMutex.TryAcquire(name);
        Assert.NotNull(reacquired);
    }
}
