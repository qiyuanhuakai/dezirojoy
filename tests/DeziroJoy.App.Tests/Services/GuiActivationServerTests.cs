using System.IO.Pipes;
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

    private static async Task<NamedPipeClientStream> ConnectAsync(GuiInstanceIdentity identity)
    {
        var pipe = new NamedPipeClientStream(".", identity.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        return pipe;
    }

    private static async Task<GuiShowAck> RoundTripAsync(NamedPipeClientStream pipe, GuiShowRequest request)
    {
        var cancellation = TestContext.Current.CancellationToken;

        await PipeFraming.WriteFrameAsync(
            pipe,
            JsonSerializer.SerializeToUtf8Bytes(request, IpcSerializer.Options),
            cancellation);
        var payload = await PipeFraming.ReadFrameAsync(pipe, cancellation)
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
}
