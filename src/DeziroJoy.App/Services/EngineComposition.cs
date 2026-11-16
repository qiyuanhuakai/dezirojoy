using DeziroJoy.Core.Platform;
using DeziroJoy.Platform.Windows.Output;

namespace DeziroJoy.App.Services;

/// <summary>
/// The single composition point for provider -&gt; sink -&gt; <see cref="EngineHost"/>. The
/// GUI and the headless daemon both build their runtime through it, so the mapping wiring
/// exists in exactly one place; only the lifetime wrapped around it differs.
/// </summary>
public sealed class EngineComposition : IDisposable
{
    private readonly IElevationService _elevationService;

    public EngineComposition(
        IProfileStore? profileStore = null,
        IElevationService? elevationService = null,
        bool presentationOnly = false,
        InputBackend? backend = null)
    {
        _elevationService = elevationService ?? new ElevationService();

        ProfileStore = profileStore ?? new FileProfileStore();

        var selection = backend ?? InputBackend.Platform;
        Backend = selection;
        if (PlatformFactory.TryCreateProvider(selection, out var created, out var providerError))
        {
            Provider = created;
            ProviderError = null;
        }
        else
        {
            // The selection was explicit and could not be honoured, so the composition says so
            // instead of quietly reading through the platform backend: a user who asked for
            // SDL3 and silently got evdev would be mapping through a backend they never chose.
            // The daemon's startup line and the settings card both quote this error.
            Provider = new NoGamepadProvider();
            ProviderError = providerError ?? "输入后端不可用";
        }

        if (Provider is VirtualGamepadProvider virtualProvider)
        {
            virtualProvider.SeedProfiles(ProfileStore);
        }

        if (presentationOnly)
        {
            Sink = new NullOutputSink();
            SinkError = null;
        }
        else
        {
            var sinkCreated = PlatformFactory.TryCreateSink(out var sink, out var sinkError);
            Sink = sinkCreated ? sink : new NullOutputSink();
            SinkError = sinkCreated ? null : sinkError;
        }

        InjectionLog = new InjectionLog();
        if (Sink is SendInputOutputSink sendInputSink)
        {
            sendInputSink.InjectionEmitted += InjectionLog.Record;
        }

        Host = new EngineHost(Provider, Sink, ProfileStore);
    }

    public IGamepadProvider Provider { get; }

    public IOutputSink Sink { get; }

    /// <summary>
    /// Always-on record of what the output sink injected and when. The Windows SendInput sink
    /// feeds it; other platforms leave it empty. Bind <see cref="InjectionLog.DumpLast"/> to show
    /// the tail. Read-only from the UI's perspective - the engine thread is the only writer.
    /// </summary>
    public InjectionLog InjectionLog { get; }

    public IProfileStore ProfileStore { get; }

    public EngineHost Host { get; }

    /// <summary>The backend this composition was built for, whether or not it started.</summary>
    public InputBackend Backend { get; }

    /// <summary>
    /// Why the requested input provider could not be created, or null when it was. Set only
    /// for an explicit selection that failed - the composition then runs a no-gamepad
    /// provider rather than falling back to the platform backend.
    /// </summary>
    public string? ProviderError { get; }

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
