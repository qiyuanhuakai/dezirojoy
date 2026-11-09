using System.Text.Json;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The GUI-only activation channel. A second launch must reach the first GUI here - never
/// through <see cref="IpcContract.PipeName"/>, which belongs to the elevated daemon - and the
/// exchange is versioned and bounded so a peer speaking another protocol is refused rather
/// than guessed at.
/// </summary>
public sealed class GuiActivationServerTests
{
    private static GuiInstanceIdentity NewIdentity()
    {
        var id = Guid.NewGuid().ToString("N");
        return new GuiInstanceIdentity(
            Path.Combine(Path.GetTempPath(), $"dezirojoy-test-gui-{id}.lock"),
            $"DeziroJoy.Test.Gui.Activation.{id}");
    }

    private static Task<Stream> ConnectAsync(GuiInstanceIdentity identity)
        => GuiActivationClient.ConnectAsync(
            identity,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

    private static async Task<GuiShowAck> RoundTripAsync(Stream stream, GuiShowRequest request)
    {
        var cancellation = TestContext.Current.CancellationToken;

        await PipeFraming.WriteFrameAsync(
            stream,
            JsonSerializer.SerializeToUtf8Bytes(request, IpcSerializer.Options),
            cancellation);
        var payload = await PipeFraming.ReadFrameAsync(stream, cancellation)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), cancellation);
        return JsonSerializer.Deserialize<GuiShowAck>(payload, IpcSerializer.Options)!;
    }

    [Fact]
    public async Task A_Show_Request_Is_Acked_Only_After_The_Window_Is_Restored()
    {
        var identity = NewIdentity();
        using var server = new GuiActivationServer(identity);

        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ShowRequested += () =>
        {
            shown.TrySetResult();
            return Task.CompletedTask;
        };

        await using var pipe = await ConnectAsync(identity);
        var ack = await RoundTripAsync(pipe, new GuiShowRequest(GuiActivationContract.ProtocolVersion));

        Assert.True(ack.Accepted, $"the show was refused: {ack.Reason}");
        Assert.Equal(GuiActivationContract.ProtocolVersion, ack.Version);
        Assert.True(shown.Task.IsCompletedSuccessfully, "the ack must wait for the window restore");
    }

    [Fact]
    public async Task A_Show_Request_From_Another_Protocol_Version_Is_Refused()
    {
        var identity = NewIdentity();
        using var server = new GuiActivationServer(identity);

        var requested = false;
        server.ShowRequested += () =>
        {
            requested = true;
            return Task.CompletedTask;
        };

        await using var pipe = await ConnectAsync(identity);
        var ack = await RoundTripAsync(
            pipe,
            new GuiShowRequest(GuiActivationContract.ProtocolVersion + 1));

        Assert.False(ack.Accepted, "a peer speaking another protocol version must be refused");
        Assert.False(requested, "a refused request must not touch the window");
    }

    [Fact]
    public async Task The_Activation_Channel_Does_Not_Answer_Before_The_Window_Is_Ready()
    {
        var identity = NewIdentity();

        using var server = GuiActivationServer.StartActive(identity);

        // Nothing is serving yet: a connect must time out rather than be answered by a
        // half-built channel. On Linux the per-instance endpoint is not published until
        // BeginServing, so this is exactly "the listener is not up yet".
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await GuiActivationClient.ConnectAsync(
                identity,
                TimeSpan.FromMilliseconds(300),
                TestContext.Current.CancellationToken));

        server.ShowRequested += () => Task.CompletedTask;
        server.BeginServing();

        await using var ready = await ConnectAsync(identity);
        var ack = await RoundTripAsync(ready, new GuiShowRequest(GuiActivationContract.ProtocolVersion));

        Assert.True(ack.Accepted, $"the show was refused after the GUI became ready: {ack.Reason}");
    }

    [Fact]
    public async Task A_Show_Request_With_No_Window_To_Restore_Is_Refused_Not_Hung()
    {
        var identity = NewIdentity();
        using var server = new GuiActivationServer(identity);

        await using var pipe = await ConnectAsync(identity);
        var ack = await RoundTripAsync(pipe, new GuiShowRequest(GuiActivationContract.ProtocolVersion));

        Assert.False(ack.Accepted, "a GUI that cannot show itself must say so");
    }

    [Fact]
    public async Task The_Server_Keeps_Serving_After_A_Peer_Disappears()
    {
        var identity = NewIdentity();
        using var server = new GuiActivationServer(identity);

        var shows = 0;
        server.ShowRequested += () =>
        {
            shows++;
            return Task.CompletedTask;
        };

        // A peer that connects and vanishes mid-exchange must not take the channel down:
        // the next launch still has to reach this GUI. The partial frame is what makes the
        // exchange deterministic - the server reads a length prefix and then hits the end of
        // the stream, so no show is recorded for the vanished peer.
        await using (var gone = await ConnectAsync(identity))
        {
            var partial = new byte[PipeFraming.LengthPrefixBytes];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(partial, 64);
            await gone.WriteAsync(partial, TestContext.Current.CancellationToken);
            await gone.FlushAsync(TestContext.Current.CancellationToken);
        }

        // The next launch goes through the real client, which is what a second launch uses:
        // it must reach the live listener even though it may first land on the connection
        // the GUI is tearing down.
        var shown = await GuiActivationClient.TryShowAsync(
            identity,
            GuiActivationOptions.Default,
            TestContext.Current.CancellationToken);

        Assert.True(shown, "the channel must survive a vanished peer");
        Assert.Equal(1, shows);
    }

    [Fact]
    public async Task A_Readiness_Probe_Accepts_Only_The_Expected_Process_Role()
    {
        var identity = NewIdentity();
        using var tray = new GuiActivationServer(identity, role: GuiInstanceRole.Tray);

        Assert.True(await GuiActivationClient.TryProbeAsync(
            identity,
            GuiInstanceRole.Tray,
            GuiActivationOptions.Default,
            TestContext.Current.CancellationToken));
        Assert.False(await GuiActivationClient.TryProbeAsync(
            identity,
            GuiInstanceRole.Render,
            GuiActivationOptions.Default,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_Show_Failure_Returns_A_Refusal_Instead_Of_Dropping_The_Connection()
    {
        var identity = NewIdentity();
        using var server = new GuiActivationServer(identity);
        server.ShowRequested += () => throw new InvalidOperationException("window is closing");

        await using var pipe = await ConnectAsync(identity);
        var ack = await RoundTripAsync(pipe, new GuiShowRequest(GuiActivationContract.ProtocolVersion));

        Assert.False(ack.Accepted);
        Assert.Contains("window is closing", ack.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Connect_Timeout_Covers_The_Winner_Startup_Window()
    {
        Assert.True(
            GuiActivationOptions.Default.ConnectTimeout >= TimeSpan.FromSeconds(15),
            "a second launch must be able to wait for the first GUI to finish starting");
    }

    [Fact]
    public void The_Activation_Channel_Is_Separate_From_The_Daemon_Mapping_Channel()
    {
        var identity = GuiInstanceIdentity.ForCurrentProcess();

        Assert.NotEqual(IpcContract.PipeName, identity.PipeName);
        Assert.StartsWith(GuiActivationContract.PipeNamePrefix, identity.PipeName, StringComparison.Ordinal);
        Assert.EndsWith(".lock", identity.LockPath, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Identity_Is_Stable_For_The_Current_Process()
    {
        Assert.Equal(GuiInstanceIdentity.ForCurrentProcess(), GuiInstanceIdentity.ForCurrentProcess());
    }

    [Fact]
    public async Task An_In_Flight_Show_Is_Acked_Even_As_The_Holder_Retires()
    {
        // A launch that reaches a holder microseconds before it retires must still be answered:
        // the drain lets the ack finish writing instead of cancelling it with the lifetime.
        var identity = NewIdentity();
        var server = new GuiActivationServer(identity);

        try
        {
            var restoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.ShowRequested += () =>
            {
                restoring.TrySetResult();
                return releaseRestore.Task;
            };

            await using var client = await ConnectAsync(identity);
            var exchange = RoundTripAsync(client, new GuiShowRequest(GuiActivationContract.ProtocolVersion));

            // The handler is mid-restore; retire the holder before it can write the ack.
            await restoring.Task;
            var retire = Task.Run(() => server.Dispose(), TestContext.Current.CancellationToken);

            releaseRestore.SetResult();
            var ack = await exchange;

            Assert.True(ack.Accepted, $"the show must be acked as the holder retires: {ack.Reason}");
            await retire;
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task A_Retiring_Holder_Does_Not_Disturb_Its_Successors_Channel()
    {
        // The Linux bug this guards: a retiring holder must not delete the socket its successor
        // now serves. Each holder owns a distinct channel, so the holder's exit leaves the
        // successor reachable - a launch through discovery still gets its ack.
        var identity = NewIdentity();
        using var holder = new GuiActivationServer(identity, role: GuiInstanceRole.Tray);
        using var successor = new GuiActivationServer(identity, role: GuiInstanceRole.Render);

        var shows = 0;
        successor.ShowRequested += () =>
        {
            shows++;
            return Task.CompletedTask;
        };

        holder.Dispose();

        var shown = await GuiActivationClient.TryShowAsync(
            identity,
            GuiActivationOptions.Default,
            TestContext.Current.CancellationToken);

        Assert.True(shown, "the successor must remain reachable after the holder retired");
        Assert.Equal(1, shows);
    }

    [Fact]
    public async Task A_Launch_Always_Reaches_The_Current_Holder_Across_Handovers()
    {
        // Back-to-back handovers: each new holder publishes its own endpoint, the previous one
        // retires, and a launch must land on the live holder every time - never a stale one.
        var identity = NewIdentity();
        for (var generation = 1; generation <= 3; generation++)
        {
            using var holder = new GuiActivationServer(identity);
            var shows = 0;
            holder.ShowRequested += () =>
            {
                shows++;
                return Task.CompletedTask;
            };

            var shown = await GuiActivationClient.TryShowAsync(
                identity,
                GuiActivationOptions.Default,
                TestContext.Current.CancellationToken);

            Assert.True(shown, $"generation {generation} must be reachable after the previous holder retired");
            Assert.Equal(1, shows);
        }
    }
}
