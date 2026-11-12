using System.Text.Json;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Ipc;

/// <summary>
/// The Exit request on the activation channel: how an updater asks a resident tray to leave
/// so an installer can replace the binary it is running. The channel already answered Show
/// and Probe, and an older peer that does not know the new kind refuses it instead of
/// guessing - which is what the update flow reports as "close the tray by hand".
/// </summary>
public sealed class GuiActivationExitTests
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

    private static GuiShowRequest ExitRequest()
        => new(GuiActivationContract.ProtocolVersion, GuiActivationRequestKind.Exit, GuiInstanceRole.Tray);

    [Fact]
    public async Task An_Exit_Request_Is_Acked_Only_After_The_Exit_Path_Has_Run()
    {
        var identity = NewIdentity();
        using var server = new GuiActivationServer(identity, role: GuiInstanceRole.Tray);

        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ExitRequested += () =>
        {
            exited.TrySetResult();
            return Task.CompletedTask;
        };

        await using var pipe = await ConnectAsync(identity);
        var ack = await RoundTripAsync(pipe, ExitRequest());

        Assert.True(ack.Accepted, $"the exit was refused: {ack.Reason}");
        Assert.Equal(GuiInstanceRole.Tray, ack.Role);
        Assert.True(exited.Task.IsCompletedSuccessfully, "the ack must wait for the exit path");
    }

    [Fact]
    public async Task An_Exit_Request_To_A_Peer_That_Cannot_Exit_Is_Refused()
    {
        // A tray with no exit handler (a headless test double, or a peer that has nothing to
        // retire) must say so rather than acking an exit it cannot perform.
        var identity = NewIdentity();
        using var server = new GuiActivationServer(identity, role: GuiInstanceRole.Tray);

        await using var pipe = await ConnectAsync(identity);
        var ack = await RoundTripAsync(pipe, ExitRequest());

        Assert.False(ack.Accepted);
        Assert.Contains("cannot be asked to exit", ack.Reason);
    }

    [Fact]
    public async Task An_Exit_Request_Does_Not_Touch_The_Show_Handler()
    {
        var identity = NewIdentity();
        using var server = new GuiActivationServer(identity, role: GuiInstanceRole.Tray);

        var shows = 0;
        var exits = 0;
        server.ShowRequested += () =>
        {
            shows++;
            return Task.CompletedTask;
        };
        server.ExitRequested += () =>
        {
            exits++;
            return Task.CompletedTask;
        };

        await using var pipe = await ConnectAsync(identity);
        var ack = await RoundTripAsync(pipe, ExitRequest());

        Assert.True(ack.Accepted, $"the exit was refused: {ack.Reason}");
        Assert.Equal(1, exits);
        Assert.Equal(0, shows);
    }

    [Fact]
    public async Task A_Render_Peer_Refuses_An_Exit_Request_Meant_For_The_Tray()
    {
        // The updater runs in the render process, which must never be the peer that is asked
        // to exit: the request names the tray role and the role check refuses it.
        var identity = NewIdentity();
        using var server = new GuiActivationServer(identity, role: GuiInstanceRole.Render);

        var exits = 0;
        server.ExitRequested += () =>
        {
            exits++;
            return Task.CompletedTask;
        };

        await using var pipe = await ConnectAsync(identity);
        var ack = await RoundTripAsync(pipe, ExitRequest());

        Assert.False(ack.Accepted);
        Assert.Contains("Render", ack.Reason);
        Assert.Equal(0, exits);
    }

    [Fact]
    public async Task The_Client_Exit_Request_Succeeds_Against_A_Tray_Peer()
    {
        var identity = NewIdentity();
        using var server = new GuiActivationServer(identity, role: GuiInstanceRole.Tray);

        var exits = 0;
        server.ExitRequested += () =>
        {
            exits++;
            return Task.CompletedTask;
        };
        server.BeginServing();

        var accepted = await GuiActivationClient.TryExitAsync(
            identity,
            GuiActivationOptions.Default,
            TestContext.Current.CancellationToken);

        Assert.True(accepted);
        Assert.Equal(1, exits);
    }

    [Fact]
    public async Task The_Client_Exit_Request_Fails_Against_A_Render_Peer()
    {
        var identity = NewIdentity();
        using var server = new GuiActivationServer(identity, role: GuiInstanceRole.Render);

        var accepted = await GuiActivationClient.TryExitAsync(
            identity,
            GuiActivationOptions.Default,
            TestContext.Current.CancellationToken);

        Assert.False(accepted);
    }

    [Fact]
    public async Task A_Failing_Exit_Handler_Does_Not_Take_The_Channel_Down()
    {
        var identity = NewIdentity();
        using var server = new GuiActivationServer(identity, role: GuiInstanceRole.Tray);

        server.ExitRequested += () => throw new InvalidOperationException("the exit path failed");
        server.ShowRequested += () => Task.CompletedTask;

        await using var pipe = await ConnectAsync(identity);
        var refused = await RoundTripAsync(pipe, ExitRequest());

        Assert.False(refused.Accepted);
        Assert.Contains("the exit request failed", refused.Reason);

        // The next exchange must still be answered: a failed exit is one peer's problem.
        await using var next = await ConnectAsync(identity);
        var shown = await RoundTripAsync(
            next,
            new GuiShowRequest(GuiActivationContract.ProtocolVersion));

        Assert.True(shown.Accepted, $"the channel stopped serving: {shown.Reason}");
    }

    [Fact]
    public async Task An_Unknown_Request_Kind_Is_Still_Refused_Instead_Of_Guessed_At()
    {
        var identity = NewIdentity();
        using var server = new GuiActivationServer(identity, role: GuiInstanceRole.Tray);

        var touched = false;
        server.ExitRequested += () =>
        {
            touched = true;
            return Task.CompletedTask;
        };

        await using var pipe = await ConnectAsync(identity);
        var ack = await RoundTripAsync(
            pipe,
            new GuiShowRequest(GuiActivationContract.ProtocolVersion, (GuiActivationRequestKind)99));

        Assert.False(ack.Accepted);
        Assert.Contains("unsupported activation request kind", ack.Reason);
        Assert.False(touched);
    }

    [Fact]
    public async Task A_Show_Request_Still_Works_On_A_Peer_That_Also_Answers_Exit()
    {
        // Backward compatibility: adding a kind must not change what the existing kinds do.
        var identity = NewIdentity();
        using var server = new GuiActivationServer(identity, role: GuiInstanceRole.Tray);

        var shown = 0;
        server.ShowRequested += () =>
        {
            shown++;
            return Task.CompletedTask;
        };
        server.ExitRequested += () => Task.CompletedTask;

        await using var pipe = await ConnectAsync(identity);
        var ack = await RoundTripAsync(
            pipe,
            new GuiShowRequest(GuiActivationContract.ProtocolVersion));

        Assert.True(ack.Accepted, $"the show was refused: {ack.Reason}");
        Assert.Equal(1, shown);
    }
}
