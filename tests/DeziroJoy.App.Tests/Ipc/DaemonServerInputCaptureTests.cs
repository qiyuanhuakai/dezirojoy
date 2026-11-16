using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Ipc;

/// <summary>
/// The daemon-side half of the editor's input-capture lease: the per-connection verb, its
/// exclusivity, and the cleanup paths. The lease is per connection like the hijack lease - it
/// is one window's editor state, never published - and unlike the hijack lease only one
/// connection may hold it at a time, because the engine silences exactly one device and a
/// second editor silently displaced would leave its own device mapping while its user
/// believes it is captured.
/// </summary>
public class DaemonServerInputCaptureTests
{
    private static DaemonStateStore NewStore()
        => new(Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():n}.json"));

    private static (DaemonServer Server, FakeDaemonHost Host) CreateRunning()
    {
        var host = new FakeDaemonHost();
        host.AddDevice("pad-1");
        host.AddDevice("pad-2");
        var server = new DaemonServer(host, new InMemoryProfileStore(), NewStore());
        return (server, host);
    }

    [Fact]
    public async Task Taking_The_Capture_Lease_Is_Acked_And_Reaches_The_Host()
    {
        var (server, host) = CreateRunning();

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-1", "pad-1"), "client-a"));

        Assert.True(ack.Accepted);
        Assert.Equal("pad-1", host.CapturedDevice);
        Assert.Equal(1, host.CaptureCalls);
    }

    [Fact]
    public async Task Taking_The_Lease_Twice_For_The_Same_Device_Reaches_The_Host_Once()
    {
        var (server, host) = CreateRunning();

        await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-1", "pad-1"), "client-a");
        var again = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-2", "pad-1"), "client-a"));

        Assert.True(again.Accepted);
        Assert.Equal("pad-1", host.CapturedDevice);
        Assert.Equal(1, host.CaptureCalls);
    }

    [Fact]
    public async Task Releasing_The_Lease_With_Null_Is_Acked_And_Clears_The_Host()
    {
        var (server, host) = CreateRunning();
        await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-1", "pad-1"), "client-a");

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-2", null), "client-a"));

        Assert.True(ack.Accepted);
        Assert.Null(host.CapturedDevice);
        Assert.Equal(2, host.CaptureCalls);
    }

    [Fact]
    public async Task Releasing_A_Lease_This_Connection_Never_Took_Is_Still_A_Success()
    {
        // Cancel is idempotent: the GUI sends null on every exit path, including one that
        // never started a capture.
        var (server, host) = CreateRunning();

        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-1", null), "client-a"));

        Assert.True(ack.Accepted);
        Assert.Null(host.CapturedDevice);
        Assert.Equal(0, host.CaptureCalls);
    }

    [Fact]
    public async Task A_Second_Connection_Cannot_Take_The_Lease_While_One_Is_Held()
    {
        var (server, host) = CreateRunning();
        await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-a", "pad-1"), "client-a");

        var refused = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-b", "pad-2"), "client-b"));

        Assert.False(refused.Accepted);
        Assert.Equal(DaemonErrorCodes.InputCaptureActive, refused.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(refused.ErrorMessage));
        // The held lease is untouched by the refusal.
        Assert.Equal("pad-1", host.CapturedDevice);
    }

    [Fact]
    public async Task The_Lease_Is_Scoped_To_Its_Connection_And_Moves_On_Release()
    {
        var (server, host) = CreateRunning();

        await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-a", "pad-1"), "client-a");
        await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-b", "pad-2"), "client-b");
        Assert.Equal("pad-1", host.CapturedDevice);

        // The holder releases; the second window's request now lands.
        await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-a2", null), "client-a");
        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-b2", "pad-2"), "client-b"));

        Assert.True(ack.Accepted);
        Assert.Equal("pad-2", host.CapturedDevice);
    }

    [Fact]
    public async Task A_Holder_Moving_To_Another_Device_Replaces_Its_Own_Lease()
    {
        var (server, host) = CreateRunning();

        await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-a", "pad-1"), "client-a");
        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-a2", "pad-2"), "client-a"));

        Assert.True(ack.Accepted);
        Assert.Equal("pad-2", host.CapturedDevice);
    }

    [Fact]
    public async Task Releasing_The_Connection_Releases_The_Lease()
    {
        var (server, host) = CreateRunning();
        await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-a", "pad-1"), "client-a");

        server.ReleaseConnection("client-a");

        Assert.Null(host.CapturedDevice);
        Assert.Equal(2, host.CaptureCalls);

        // The freed lease is available to the next window.
        var ack = Assert.IsType<MutationAckMessage>(
            await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-b", "pad-1"), "client-b"));
        Assert.True(ack.Accepted);
        Assert.Equal("pad-1", host.CapturedDevice);
    }

    [Fact]
    public async Task Releasing_A_Connection_Without_A_Lease_Does_Not_Touch_The_Host()
    {
        var (server, host) = CreateRunning();
        await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-a", "pad-1"), "client-a");

        server.ReleaseConnection("client-b");

        Assert.Equal("pad-1", host.CapturedDevice);
    }

    [Fact]
    public async Task Disconnect_Releases_The_Capture_Lease_Without_Stopping_Mapping()
    {
        // A window that vanishes mid-capture must not leave a device silenced: the user cannot
        // cancel an editor that is already gone, so the transport's release path does it.
        var (server, host) = CreateRunning();
        await server.ProcessAsync(new StartMessage("req-start", 0));
        await server.ProcessAsync(new SetMappingEnabledMessage("req-pause", false));
        var stream = new ScriptedStream();
        stream.Script(new HelloMessage("gui-1", IpcContract.ProtocolVersion, "client-1", IpcSession.Current));
        stream.Script(new SetInputCaptureDeviceMessage("req-capture", "pad-1"));

        await server.ServeConnectionAsync(stream, TestContext.Current.CancellationToken);

        Assert.Null(host.CapturedDevice);
        Assert.True(host.IsRunning);
        Assert.False(host.IsMappingEnabled);
        Assert.Equal(0, host.StopCalls);
    }

    [Fact]
    public async Task The_Capture_Lease_Is_Never_Published_In_A_Snapshot()
    {
        // The lease is one window's editor state, like the hijack lease: publishing it would
        // make every other window quote a capture that is none of their business, and a
        // snapshot per toggle would be noise for state no other surface reads.
        var (server, _) = CreateRunning();
        var pushes = new List<StateChangedMessage>();
        server.StateChanged += pushes.Add;

        await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-1", "pad-1"), "client-a");
        await server.ProcessAsync(new SetInputCaptureDeviceMessage("req-2", null), "client-a");

        Assert.Empty(pushes);
    }
}
