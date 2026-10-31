using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;
using DeziroJoy.Core.Profiles;

namespace DeziroJoy.App.Services;

public sealed class ControllerSampledEventArgs : EventArgs
{
    public ControllerSampledEventArgs(ControllerRuntime runtime, IGamepadState state)
    {
        Runtime = runtime;
        State = state;
    }

    public ControllerRuntime Runtime { get; }

    public IGamepadState State { get; }
}

/// <summary>
/// Ties provider, per-device engines, dispatchers and the output sink together.
/// <see cref="TickOnce"/> is deterministic and thread-free (used by tests and the UI);
/// <see cref="Start"/> runs the same loop on a background thread for live mapping.
/// </summary>
public sealed class EngineHost : IDisposable
{
    private readonly IGamepadProvider _provider;
    private readonly IOutputSink _sink;
    private readonly IProfileStore _profileStore;
    private readonly Dictionary<string, ControllerRuntime> _runtimes = [];
    private readonly Lock _sync = new();
    private Thread? _thread;
    private volatile bool _running;

    public EngineHost(IGamepadProvider provider, IOutputSink sink, IProfileStore profileStore)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(profileStore);

        _provider = provider;
        _sink = sink;
        _profileStore = profileStore;
    }

    public bool IsMappingEnabled { get; private set; } = true;

    public bool IsRunning => _running;

    public IReadOnlyList<ControllerRuntime> Controllers
    {
        get
        {
            lock (_sync)
            {
                return _runtimes.Values.ToList();
            }
        }
    }

    public event EventHandler<ControllerSampledEventArgs>? Sampled;

    /// <summary>Advances every connected controller by one tick.</summary>
    public void TickOnce(TimeSpan delta)
    {
        var connected = _provider.GetConnected();
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var descriptor in connected)
        {
            present.Add(descriptor.DeviceId);

            var runtime = EnsureRuntime(descriptor);

            var state = _provider.Poll(descriptor.DeviceId);
            if (state is null)
            {
                // Transient read failure: keep the runtime, try again next tick.
                continue;
            }

            if (IsMappingEnabled)
            {
                var commands = runtime.Engine.Tick(state, (float)delta.TotalSeconds);
                runtime.Dispatcher.DispatchAll(commands);
            }

            Sampled?.Invoke(this, new ControllerSampledEventArgs(runtime, state));
        }

        lock (_sync)
        {
            foreach (var deviceId in _runtimes.Keys.Where(id => !present.Contains(id)).ToList())
            {
                Remove(deviceId);
            }
        }
    }

    /// <summary>
    /// Reconciles the runtime table with the provider's connected-device list without
    /// ticking any engine or raising <see cref="Sampled"/>. Lets the UI reflect gamepad
    /// presence while the mapping loop is stopped; <see cref="EnsureRuntime"/> stays the
    /// single place a profile is loaded, so a later <see cref="Start"/> reuses the runtime
    /// instead of reloading the profile.
    /// </summary>
    public void RefreshPresence()
    {
        var connected = _provider.GetConnected();
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var descriptor in connected)
        {
            present.Add(descriptor.DeviceId);
            EnsureRuntime(descriptor);
        }

        lock (_sync)
        {
            foreach (var deviceId in _runtimes.Keys.Where(id => !present.Contains(id)).ToList())
            {
                Remove(deviceId);
            }
        }
    }

    public void SetMappingEnabled(bool enabled)
    {
        if (IsMappingEnabled == enabled)
        {
            return;
        }

        IsMappingEnabled = enabled;

        if (!enabled)
        {
            foreach (var runtime in Controllers)
            {
                runtime.Engine.Reset();
                runtime.Dispatcher.ReleaseAll();
            }
        }
    }

    /// <summary>Replaces the active profile of a device (used by the mapping editor).</summary>
    public void Reload(string deviceId, GamepadProfile profile)
    {
        lock (_sync)
        {
            if (_runtimes.TryGetValue(deviceId, out var runtime))
            {
                runtime.Reload(profile);
            }
        }
    }

    /// <summary>Persists the active profile of a device.</summary>
    public void SaveProfile(string deviceId)
    {
        var runtime = Controllers.SingleOrDefault(r => string.Equals(r.Descriptor.DeviceId, deviceId, StringComparison.Ordinal));
        if (runtime is not null)
        {
            _profileStore.Save(runtime.Profile);
        }
    }

    /// <summary>Starts the background mapping loop.</summary>
    public void Start(TimeSpan interval)
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _thread = new Thread(() => Run(interval))
        {
            IsBackground = true,
            Name = "dezirojoy-engine",
        };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;

        foreach (var runtime in Controllers)
        {
            runtime.Engine.Reset();
            runtime.Dispatcher.ReleaseAll();
        }
    }

    public void Dispose()
    {
        Stop();
        _provider.Dispose();
        _sink.Dispose();
    }

    private void Run(TimeSpan interval)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var last = stopwatch.Elapsed;

        while (_running)
        {
            var now = stopwatch.Elapsed;
            var delta = now - last;
            last = now;

            try
            {
                TickOnce(delta);
            }
            catch (Exception)
            {
                // A failing tick must never kill the mapping loop.
            }

            var sleep = interval - (stopwatch.Elapsed - now);
            if (sleep > TimeSpan.Zero)
            {
                Thread.Sleep(sleep);
            }
        }
    }

    private ControllerRuntime EnsureRuntime(GamepadDescriptor descriptor)
    {
        lock (_sync)
        {
            if (_runtimes.TryGetValue(descriptor.DeviceId, out var existing))
            {
                return existing;
            }

            var profile = _profileStore.Load(descriptor.DeviceId) ?? DefaultProfileLibrary.CreateDefault(descriptor);
            var runtime = new ControllerRuntime(descriptor, profile, _sink);
            _runtimes[descriptor.DeviceId] = runtime;

            return runtime;
        }
    }

    private void Remove(string deviceId)
    {
        lock (_sync)
        {
            if (_runtimes.Remove(deviceId, out var runtime))
            {
                runtime.Engine.Reset();
                runtime.Dispatcher.ReleaseAll();
            }
        }
    }
}
