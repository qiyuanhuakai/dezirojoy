using System.IO.Pipes;
using System.Text;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Ipc;

public sealed class DaemonPipeAcceptTests
{
    [Fact]
    public async Task A_Second_Client_Connects_While_The_First_Remains_Open()
    {
        var host = new FakeDaemonHost();
        var pipeName = $"DeziroJoy.Test.{Guid.NewGuid():N}";
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(host, new InMemoryProfileStore(), new DaemonStateStore(statePath));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var acceptTask = DaemonHost.ServePipeAsync(server, cancellation.Token, pipeName);

        await using var first = await ConnectAsync(pipeName);
        var firstHello = await ExchangeAsync(first, new HelloMessage("gui-1", IpcContract.ProtocolVersion, "client-1", IpcSession.Current));
        Assert.True(Assert.IsType<HelloAckMessage>(firstHello).Accepted);

        await using var second = await ConnectAsync(pipeName);
        var secondHello = await ExchangeAsync(second, new HelloMessage("gui-2", IpcContract.ProtocolVersion, "client-2", IpcSession.Current));
        Assert.True(Assert.IsType<HelloAckMessage>(secondHello).Accepted);

        var firstState = Assert.IsType<StateChangedMessage>(
            await ExchangeAsync(first, new GetStateMessage("first-state")));
        var secondState = Assert.IsType<StateChangedMessage>(
            await ExchangeAsync(second, new GetStateMessage("second-state")));

        Assert.True(first.IsConnected);
        Assert.True(second.IsConnected);
        Assert.Equal(firstState.DaemonInstanceId, secondState.DaemonInstanceId);

        await first.DisposeAsync();
        await second.DisposeAsync();
        await cancellation.CancelAsync();
        await acceptTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        return client;
    }

    private static async Task<IpcMessage> ExchangeAsync(Stream stream, IpcMessage message)
    {
        var payload = Encoding.UTF8.GetBytes(IpcSerializer.Serialize(message));
        await PipeFraming.WriteFrameAsync(stream, payload, TestContext.Current.CancellationToken);
        var response = await PipeFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken);
        return IpcSerializer.Deserialize(Encoding.UTF8.GetString(response));
    }
}
