using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

public sealed class GuiRuntimeFactoryTests
{
    [Fact]
    public async Task Windows_Never_Builds_A_Local_Engine_When_The_Daemon_Is_Unavailable()
    {
        var endpoint = new UnavailableEndpoint();
        var localBuilds = 0;
        var factory = new GuiRuntimeFactory(
            endpoint,
            () =>
            {
                localBuilds++;
                return new EngineComposition();
            },
            () => true);

        await using var runtime = await factory.CreateAsync(TestContext.Current.CancellationToken);

        Assert.False(runtime.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(runtime.UnavailableReason));
        Assert.Null(runtime.Controller);
        Assert.Null(runtime.Local);
        Assert.Equal(0, localBuilds);
        Assert.Equal(0, endpoint.OpenCalls);
    }

    [Fact]
    public async Task Linux_Keeps_The_Local_Composition_And_Does_Not_Probe_The_Daemon()
    {
        var endpoint = new UnavailableEndpoint();
        var localBuilds = 0;
        var factory = new GuiRuntimeFactory(
            endpoint,
            () =>
            {
                localBuilds++;
                return new EngineComposition();
            },
            () => false);

        await using var runtime = await factory.CreateAsync(TestContext.Current.CancellationToken);

        Assert.True(runtime.IsAvailable);
        Assert.NotNull(runtime.Local);
        Assert.Null(runtime.Controller);
        Assert.Equal(1, localBuilds);
        Assert.Equal(0, endpoint.EnsureCalls);
        Assert.Equal(0, endpoint.OpenCalls);
    }

    private sealed class UnavailableEndpoint : IDaemonEndpoint
    {
        public int EnsureCalls { get; private set; }

        public int OpenCalls { get; private set; }

        public Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken)
        {
            EnsureCalls++;
            return Task.FromResult(false);
        }

        public Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
        {
            OpenCalls++;
            throw new InvalidOperationException("an unavailable endpoint cannot open a channel");
        }
    }
}
