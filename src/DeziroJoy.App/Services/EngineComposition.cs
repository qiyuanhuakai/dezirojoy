using DeziroJoy.Core.Platform;

namespace DeziroJoy.App.Services;

/// <summary>
/// The single composition point for provider -&gt; sink -&gt; <see cref="EngineHost"/>. The
/// GUI and the headless daemon both build their runtime through it, so the mapping wiring
/// exists in exactly one place; only the lifetime wrapped around it differs.
/// </summary>
public sealed class EngineComposition : IDisposable
{
    private readonly IElevationService _elevationService;

    public EngineComposition(IProfileStore? profileStore = null, IElevationService? elevationService = null)
    {
        _elevationService = elevationService ?? new ElevationService();

        ProfileStore = profileStore ?? new FileProfileStore();
        Provider = PlatformFactory.TryCreateProvider(out var provider) ? provider : new NoGamepadProvider();

        var sinkCreated = PlatformFactory.TryCreateSink(out var sink, out var sinkError);
        Sink = sinkCreated ? sink : new NullOutputSink();
        SinkError = sinkCreated ? null : sinkError;

        Host = new EngineHost(Provider, Sink, ProfileStore);
    }

    public IGamepadProvider Provider { get; }

    public IOutputSink Sink { get; }

    public IProfileStore ProfileStore { get; }

    public EngineHost Host { get; }

    /// <summary>Why the real output sink could not be created, or null when it was.</summary>
    public string? SinkError { get; private set; }

    public static string PlatformName => PlatformFactory.PlatformName;

    public bool IsElevationSupported => _elevationService.IsSupported;

    public bool IsElevated => _elevationService.IsElevated;

    /// <summary>
    /// Disposes the host, which in turn disposes the provider and the sink - the whole
    /// runtime is owned by this one object.
    /// </summary>
    public void Dispose() => Host.Dispose();
}
