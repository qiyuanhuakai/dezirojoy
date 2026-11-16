using SDL3;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Platform;

namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// SDL3 provider over the standard Gamepad API: enumerates every device SDL recognizes as a
/// standard gamepad, normalizes buttons / dual sticks / triggers into
/// <see cref="IGamepadState"/>, and tracks hot-plug through SDL's event queue with an
/// enumeration pass as the safety net. It is the unified input layer the roadmap migrates
/// to, shared by Xbox, PlayStation, Switch, Joy-Con and third-party pads alike - one generic
/// code path, no per-brand branches.
/// <para>
/// Lifecycle: the SDL gamepad subsystem is initialized lazily on the first scan (constructing
/// the provider never loads the native library), and released on <see cref="Dispose"/>, which
/// also closes every open handle and is safe to call more than once. Device identity is
/// composed per <see cref="Sdl3GamepadIdentity"/>; SDL's run-local instance id is never a
/// config key. The latest scan's evidence - version, per-device identity, mapping, capability
/// counts, open failures - is kept in <see cref="LastScanDiagnostic"/>.
/// </para>
/// </summary>
public sealed class Sdl3GamepadProvider : IGamepadProvider
{
    private static readonly TimeSpan DefaultRescanInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Standard SDL buttons worth polling, excluding the sentinel values.</summary>
    private static readonly SDL.GamepadButton[] PollableButtons =
        Enum.GetValues<SDL.GamepadButton>()
            .Where(button => button is not (SDL.GamepadButton.Count or SDL.GamepadButton.Invalid))
            .ToArray();

    /// <summary>Standard SDL axes worth polling, excluding the sentinel values.</summary>
    private static readonly SDL.GamepadAxis[] PollableAxes =
        Enum.GetValues<SDL.GamepadAxis>()
            .Where(axis => axis is not (SDL.GamepadAxis.Count or SDL.GamepadAxis.Invalid))
            .ToArray();

    private readonly Lock _sync = new();
    private readonly List<Device> _devices = [];
    private readonly ISdl3Api _api;
    private readonly long _rescanIntervalTicks;
    private long _nextScanTimestamp;
    private string? _initError;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// Creates a provider over the real SDL runtime.
    /// </summary>
    /// <param name="scanInterval">
    /// How often <see cref="GetConnected"/> re-enumerates and drains the event queue. The
    /// engine calls it every tick; 250 ms keeps hot-plug latency imperceptible while leaving
    /// SDL calls off the hot path. A shorter interval suits a diagnostic watch loop.
    /// </param>
    public Sdl3GamepadProvider(TimeSpan? scanInterval = null)
        : this(new Sdl3Api(), scanInterval)
    {
    }

    /// <summary>
    /// Test seam: the same provider over an injected api, so enumeration, hot-plug, remap,
    /// failure and cleanup paths run deterministically without SDL or a device.
    /// </summary>
    internal Sdl3GamepadProvider(ISdl3Api api, TimeSpan? scanInterval = null)
    {
        _api = api;
        var interval = scanInterval ?? DefaultRescanInterval;
        ScanInterval = interval;
        _rescanIntervalTicks = (long)(interval.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
    }

    /// <summary>
    /// What the most recent scan found, and why. Null until the first scan. A zero count
    /// means nothing on its own - "SDL unavailable", "no gamepad recognized" and "open
    /// failed" need three different fixes - so the daemon prints
    /// <see cref="Sdl3ScanDiagnostic.Guidance"/> and the GUI status area quotes
    /// <see cref="Sdl3ScanDiagnostic.Hint"/>.
    /// </summary>
    public Sdl3ScanDiagnostic? LastScanDiagnostic { get; private set; }

    /// <summary>
    /// Diagnostics only: how many per-tick SDL state pumps this provider has performed -
    /// one per <see cref="GetConnected"/> call once SDL is initialized. The engine host
    /// ticks continuously, so a healthy provider makes this climb at the tick rate; a
    /// count that climbs far slower than the tick rate is the signature of stale-state
    /// input lag (state read from the last pump instead of the current one). The CLI
    /// probe's watch mode prints the derived rate so the cadence is observable without
    /// a profiler. Not part of the input contract.
    /// </summary>
    public long StateRefreshCount { get; private set; }

    /// <summary>
    /// How often this provider re-enumerates devices and rebuilds
    /// <see cref="LastScanDiagnostic"/>. State itself is refreshed every tick (see
    /// <see cref="StateRefreshCount"/>); this interval only governs enumeration and
    /// diagnostics, so it is expected to be far slower than the refresh rate.
    /// </summary>
    public TimeSpan ScanInterval { get; }

    public IReadOnlyList<GamepadDescriptor> GetConnected()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return Array.Empty<GamepadDescriptor>();
            }

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now >= _nextScanTimestamp)
            {
                // Scan ticks do the same per-tick refresh as every other tick, plus the
                // throttled enumeration and diagnostic rebuild (see Scan).
                Scan();
                _nextScanTimestamp = now + _rescanIntervalTicks;
            }
            else if (_initialized)
            {
                // Between scans the device table is cached, but SDL's state is not:
                // SDL_GetGamepadButton/Axis read what the last pump left behind, so
                // skipping the pump here would pin input at the scan rate (~4 Hz at the
                // default interval) - the observed animation and response lag. The pump
                // runs once per tick regardless of how many pads are connected, and the
                // queued lifecycle events are drained in the same tick, which is the
                // documented common practice ("process the event queue once every
                // frame") and keeps a removal from waiting for the next scan.
                RefreshState();
                ApplyEvents();
            }

            return _devices
                .Select(device => new GamepadDescriptor(device.Identity.DeviceId, device.Info.Name)
                {
                    ButtonCount = device.ButtonCount,
                })
                .ToList();
        }
    }

    /// <summary>
    /// Reads the current SDL state of one device and normalizes it. Returns null once the
    /// device is gone (or the provider is disposed) - the same contract the XInput and evdev
    /// providers keep, so the engine's device lifecycle is provider-agnostic.
    /// <para>
    /// The read reflects the most recent <see cref="GetConnected"/> pump on the calling
    /// thread: SDL_GetGamepadButton/Axis serve the state <c>SDL_UpdateGamepads</c> last
    /// wrote, so Poll itself never pumps (the engine host calls GetConnected once per tick
    /// before polling every pad, which is what keeps multi-device polls to one pump).
    /// </para>
    /// </summary>
    public IGamepadState? Poll(string deviceId)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return null;
            }

            var device = _devices.FirstOrDefault(d => string.Equals(d.Identity.DeviceId, deviceId, StringComparison.Ordinal));
            if (device is null)
            {
                return null;
            }

            foreach (var button in PollableButtons)
            {
                device.Tracker.ApplyButton(button, _api.GetButton(device.Handle, button));
            }

            foreach (var axis in PollableAxes)
            {
                device.Tracker.ApplyAxis(axis, _api.GetAxis(device.Handle, axis));
            }

            return device.Tracker.Snapshot();
        }
    }

    /// <summary>
    /// Closes every open gamepad and releases the SDL gamepad subsystem this provider
    /// initialized. Idempotent: a second call - or a call that races a scan - does nothing,
    /// and no handle is closed twice. After disposal the provider reports no devices and
    /// polls null instead of touching SDL again.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var device in _devices)
            {
                _api.CloseGamepad(device.Handle);
                device.Tracker.Reset();
            }

            _devices.Clear();

            if (_initialized)
            {
                _api.Shutdown();
            }
        }
    }

    /// <summary>
    /// One-shot diagnostic scan: initializes SDL if needed, enumerates, reports, and releases
    /// everything again - keeping no open handles. The GUI is a client of the daemon and owns
    /// no devices itself, so this is how it explains an empty device table with the same
    /// evidence the daemon's own scan produces; it is also what the CLI probe prints.
    /// </summary>
    public static Sdl3ScanDiagnostic ProbeOnce()
    {
        using var provider = new Sdl3GamepadProvider();
        provider.GetConnected();
        return provider.LastScanDiagnostic ?? UnscannedDiagnostic();
    }

    /// <summary>Test seam: the same one-shot scan over an injected api.</summary>
    internal static Sdl3ScanDiagnostic ProbeOnce(ISdl3Api api)
    {
        using var provider = new Sdl3GamepadProvider(api);
        provider.GetConnected();
        return provider.LastScanDiagnostic ?? UnscannedDiagnostic();
    }

    private static Sdl3ScanDiagnostic UnscannedDiagnostic() => new()
    {
        Outcome = Sdl3ScanOutcome.SdlUnavailable,
        GamepadCount = 0,
        InitError = "扫描未执行",
    };

    private void Scan()
    {
        if (!EnsureInitialized())
        {
            LastScanDiagnostic = new Sdl3ScanDiagnostic
            {
                Outcome = Sdl3ScanOutcome.SdlUnavailable,
                Runtime = _api.GetRuntimeInfo(),
                InitError = _initError,
                GamepadCount = 0,
            };
            return;
        }

        // A scan tick refreshes state exactly like every other tick; only enumeration
        // and the diagnostic rebuild are throttled.
        RefreshState();

        ApplyEvents();

        var openFailures = new List<Sdl3OpenFailure>();
        Reconcile(openFailures);

        LastScanDiagnostic = new Sdl3ScanDiagnostic
        {
            Outcome = ClassifyOutcome(_devices.Count, openFailures.Count),
            Runtime = _api.GetRuntimeInfo(),
            GamepadCount = _devices.Count,
            Devices = _devices.Select(device => device.Info).ToList(),
            UnstableIdentityInstanceIds = _devices
                .Where(device => !device.Identity.IsStable)
                .Select(device => device.Info.InstanceId)
                .ToList(),
            OpenFailures = openFailures,
        };
    }

    /// <summary>
    /// Pumps SDL's gamepad state once. <c>SDL_UpdateGamepads</c> is the documented manual
    /// pump for an application without the video event loop - this daemon has none - and
    /// <c>SDL_GetGamepadButton</c>/<c>SDL_GetGamepadAxis</c> read the state it maintains,
    /// so without a per-tick pump every poll would return the previous pump's snapshot.
    /// It is safe from any thread, which the engine's poll thread relies on.
    /// <para>
    /// Deliberately once per <see cref="GetConnected"/> and never per device: the engine
    /// host polls every connected pad once per tick, so pumping inside
    /// <see cref="Poll"/> would multiply identical work by the pad count.
    /// </para>
    /// </summary>
    private void RefreshState()
    {
        _api.UpdateGamepads();
        StateRefreshCount++;
    }

    private bool EnsureInitialized()
    {
        if (_initialized)
        {
            return true;
        }

        _initialized = _api.TryEnsureInitialized(out _initError);
        return _initialized;
    }

    /// <summary>
    /// Drains the queued lifecycle events and applies them. Runs once per tick - the
    /// documented common practice of processing the event queue every frame - so a removal
    /// closes its handle within a tick instead of waiting for the enumeration throttle,
    /// and a remap re-reads the device's info (the mapping string is diagnostic evidence).
    /// The tracker also resets on a remap, which is invisible to the engine: one poll is
    /// a reset plus a full re-apply, so a button held across the remap comes back held in
    /// the same snapshot, while a button the new layout no longer describes cannot linger
    /// from the old one.
    /// </summary>
    private void ApplyEvents()
    {
        foreach (var gamepadEvent in _api.PumpEvents())
        {
            var device = _devices.FirstOrDefault(d => d.Info.InstanceId == gamepadEvent.InstanceId);

            switch (gamepadEvent.Type)
            {
                case Sdl3GamepadEventType.Removed:
                    if (device is not null)
                    {
                        _api.CloseGamepad(device.Handle);
                        device.Tracker.Reset();
                        _devices.Remove(device);
                    }

                    break;

                case Sdl3GamepadEventType.Remapped:
                    if (device is not null)
                    {
                        device.Info = _api.ReadInfo(device.Handle);
                        device.Tracker.Reset();
                    }

                    break;

                case Sdl3GamepadEventType.Added:
                    // Enumeration below is the source of truth for opens; the Added event
                    // itself carries nothing the enumeration pass would miss.
                    break;
            }
        }
    }

    /// <summary>
    /// Reconciles the open devices against SDL's enumeration: opens devices that appeared,
    /// closes devices that vanished. The enumeration pass is what makes hot-plug reliable
    /// even when an event was never queued (SDL drops the oldest events when its queue
    /// overflows) - a device that disappears without a Removed event still gets closed here.
    /// </summary>
    private void Reconcile(List<Sdl3OpenFailure> openFailures)
    {
        var present = new HashSet<uint>();

        foreach (var instanceId in _api.GetGamepadIds())
        {
            present.Add(instanceId);

            if (_devices.Any(d => d.Info.InstanceId == instanceId))
            {
                continue;
            }

            var handle = _api.OpenGamepad(instanceId, out var error);
            if (handle is null)
            {
                openFailures.Add(new Sdl3OpenFailure(instanceId, error ?? "未知错误"));
                continue;
            }

            var info = _api.ReadInfo(handle);
            _devices.Add(new Device
            {
                Handle = handle,
                Info = info,
                Identity = Sdl3GamepadIdentity.Compose(info),
                Tracker = new Sdl3GamepadStateTracker(),
                ButtonCount = info.DeclaredButtonCount,
            });
        }

        for (var i = _devices.Count - 1; i >= 0; i--)
        {
            if (!present.Contains(_devices[i].Info.InstanceId))
            {
                _api.CloseGamepad(_devices[i].Handle);
                _devices[i].Tracker.Reset();
                _devices.RemoveAt(i);
            }
        }
    }

    private static Sdl3ScanOutcome ClassifyOutcome(int gamepadCount, int openFailureCount)
    {
        if (gamepadCount > 0)
        {
            // A working device is the operational state even when another failed to open;
            // the failure is listed in the diagnostic and guidance, not hidden.
            return Sdl3ScanOutcome.GamepadsFound;
        }

        return openFailureCount > 0 ? Sdl3ScanOutcome.OpenFailed : Sdl3ScanOutcome.NoGamepads;
    }

    private sealed class Device
    {
        public required Sdl3GamepadHandle Handle { get; init; }

        public required Sdl3GamepadInfo Info { get; set; }

        public required Sdl3GamepadIdentity Identity { get; init; }

        public required Sdl3GamepadStateTracker Tracker { get; init; }

        public required int ButtonCount { get; init; }
    }
}
