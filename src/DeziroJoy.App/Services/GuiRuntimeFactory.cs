using System.IO.Pipes;
using System.Text;
using DeziroJoy.App.Ipc;
using DeziroJoy.Platform.Windows.Elevation;

namespace DeziroJoy.App.Services;

public sealed class GuiRuntime : IAsyncDisposable
{
    private GuiRuntime(DaemonController? controller, EngineComposition? local, string? unavailableReason)
    {
        Controller = controller;
        Local = local;
        UnavailableReason = unavailableReason;
    }

    public DaemonController? Controller { get; }

    public EngineComposition? Local { get; }

    public string? UnavailableReason { get; }

    public bool IsAvailable => UnavailableReason is null;

    public static GuiRuntime DaemonControlled(DaemonController controller) => new(controller, null, null);

    public static GuiRuntime LocalEngine(EngineComposition local) => new(null, local, null);

    public static GuiRuntime Unavailable(string reason) => new(null, null, reason);

    public async ValueTask DisposeAsync()
    {
        if (Controller is not null)
        {
            await Controller.DisposeAsync().ConfigureAwait(false);
        }

        Local?.Dispose();
    }
}

public sealed class GuiRuntimeFactory
{
    private readonly IDaemonEndpoint _endpoint;
    private readonly Func<EngineComposition> _localCompositionFactory;
    private readonly Func<bool> _isWindows;

    public GuiRuntimeFactory(
        IDaemonEndpoint endpoint,
        Func<EngineComposition>? localCompositionFactory = null,
        Func<bool>? isWindows = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoint = endpoint;
        _localCompositionFactory = localCompositionFactory ?? (static () => new EngineComposition());
        _isWindows = isWindows ?? (static () => OperatingSystem.IsWindows());
    }

    public static GuiRuntimeFactory CreateDefault() => new(new NamedPipeDaemonEndpoint());

    public async Task<GuiRuntime> CreateAsync(CancellationToken cancellationToken)
    {
        if (!_isWindows())
        {
            return GuiRuntime.LocalEngine(_localCompositionFactory());
        }

        var controller = new DaemonController(_endpoint);
        try
        {
            await controller.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return GuiRuntime.DaemonControlled(controller);
        }
        catch (Exception exception) when (exception
            is DaemonUnavailableException or DaemonHandshakeException or IOException or TimeoutException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            await controller.DisposeAsync().ConfigureAwait(false);
            return GuiRuntime.Unavailable(exception.Message);
        }
        catch
        {
            await controller.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

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
