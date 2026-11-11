using System.IO.Pipes;
using System.Text;
using DeziroJoy.App.Ipc;
using DeziroJoy.Platform.Windows.Elevation;

namespace DeziroJoy.App.Services;

internal sealed class NamedPipeDaemonChannel(PipeStream pipe) : IDaemonChannel
{
    public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(IpcSerializer.Serialize(message));
        await PipeFraming.WriteFrameAsync(pipe, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IpcMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        var payload = await PipeFraming.ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
        return IpcSerializer.Deserialize(Encoding.UTF8.GetString(payload));
    }

    public ValueTask DisposeAsync()
    {
        pipe.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class NamedPipeDaemonEndpoint : IDaemonEndpoint
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public async Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken)
    {
        if (await ProbeAsync(ProbeTimeout, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (!allowLaunch || !OperatingSystem.IsWindows()
            || !WindowsProcessElevation.RelaunchElevated("--daemon"))
        {
            return false;
        }

        return await ProbeAsync(LaunchTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
    {
        var pipe = CreateClient();
        try
        {
            await pipe.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            return new NamedPipeDaemonChannel(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<bool> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = CreateClient();
            await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            return pipe.IsConnected;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static NamedPipeClientStream CreateClient() => new(
        ".",
        IpcContract.PipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous);
}
