using DeziroJoy.Core.Engine;
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
    private readonly PresetStore _presets;
    private readonly Dictionary<string, ControllerRuntime> _runtimes = [];
    private readonly Dictionary<string, int> _absenceTicks = [];
    private readonly Lock _sync = new();
    private readonly string _injectionMutexName;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _injectionSuspended;
    private volatile int _injectionVerdict;
    private int _presenceVersion;

    private const int InjectionVerdictPending = 0;
    private const int InjectionVerdictHeld = 1;
    private const int InjectionVerdictRefused = 2;

    /// <summary>
    /// Resolves the injection mutex name for a host constructed without an explicit one.
    /// Production leaves this at <see cref="InjectionMutex.DefaultName"/> - one
    /// machine-wide slot shared by the GUI and the resident daemon. The test assembly
    /// overrides it once at load with a per-instance name: tests construct hosts directly,
    /// xUnit runs collections in parallel, and one GUI test deliberately leaves an engine
    /// running for the whole process, so sharing the production name would serialise the
    /// suite on a single slot and a leaked hold would fail unrelated tests. Tests that
    /// assert exclusion pass their own shared name explicitly.
    /// </summary>
    public static Func<string> DefaultInjectionMutexNameResolver { get; set; }
        = static () => InjectionMutex.DefaultName;

    public EngineHost(IGamepadProvider provider, IOutputSink sink, IProfileStore profileStore)
        : this(provider, sink, profileStore, injectionMutexName: null)
    {
    }

    public EngineHost(IGamepadProvider provider, IOutputSink sink, IProfileStore profileStore, string? injectionMutexName)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(profileStore);

        _provider = provider;
        _sink = sink;
        _profileStore = profileStore;
        _presets = new PresetStore(profileStore);
        _injectionMutexName = injectionMutexName ?? DefaultInjectionMutexNameResolver();
    }

    public bool IsMappingEnabled { get; private set; } = true;

    public bool IsRunning => _running;

    public int ActivePresetSlot { get; private set; } = PresetStore.MinSlot;

    public event Action? ControllersChanged;

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

    /// <summary>
    /// Raised by <see cref="SamplePresence"/> for every connected controller. The stopped-state
    /// counterpart of <see cref="Sampled"/>: it reports the current state without ticking any
    /// engine or dispatching any command, so the UI can highlight the pad and navigate by
    /// physical button while the mapping loop is off.
    /// </summary>
    public event EventHandler<ControllerSampledEventArgs>? PresenceSampled;

    /// <summary>
    /// Raised after a tick's command loop when the engine asked to switch preset slot,
    /// carrying the target slot. <see cref="Platform.CommandDispatcher"/> never injects a
    /// <see cref="SwitchPresetCommand"/> - the host owns the switch - so this is how the
    /// tick's request reaches it: at most one raise per tick, the first request in tick
    /// order winning when one tick named several. The host owns the runtime slot and store;
    /// this event lets the daemon or view model perform the same atomic switch path.
    /// </summary>
    public event EventHandler<int>? PresetSwitchRequested;

    /// <summary>Advances every connected controller by one tick.</summary>
    public void TickOnce(TimeSpan delta)
    {
        int? presetSwitch = null;
        bool presenceChanged;

        lock (_sync)
        {
            var initialPresenceVersion = _presenceVersion;
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

                if (IsMappingEnabled && !_injectionSuspended)
                {
                    var commands = runtime.Engine.Tick(state, (float)delta.TotalSeconds);
                    runtime.Dispatcher.DispatchAll(commands);
                    presetSwitch ??= FirstPresetSwitch(commands);
                }

                Sampled?.Invoke(this, new ControllerSampledEventArgs(runtime, state));
            }

            ReconcileAbsence(present, DeviceAbsenceGraceTicks);
            presenceChanged = initialPresenceVersion != _presenceVersion;
        }

        if (presenceChanged)
        {
            ControllersChanged?.Invoke();
        }

        // Raised after the tick's command loop completes and outside the lock: the host
        // answers by reloading a device, which takes the same lock.
        if (presetSwitch is int slot)
        {
            PresetSwitchRequested?.Invoke(this, slot);
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
        bool presenceChanged;
        lock (_sync)
        {
            var initialPresenceVersion = _presenceVersion;
            var connected = _provider.GetConnected();
            var present = new HashSet<string>(StringComparer.Ordinal);

            foreach (var descriptor in connected)
            {
                present.Add(descriptor.DeviceId);
                EnsureRuntime(descriptor);
            }

            ReconcileAbsence(present, graceTicks: 0);
            presenceChanged = initialPresenceVersion != _presenceVersion;
        }

        if (presenceChanged)
        {
            ControllersChanged?.Invoke();
        }
    }

    public void SetMappingEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (IsMappingEnabled == enabled)
            {
                return;
            }

            IsMappingEnabled = enabled;

            if (!enabled)
            {
                foreach (var runtime in _runtimes.Values)
                {
                    runtime.Engine.Reset();
                    runtime.Dispatcher.ReleaseAll();
                }
            }
        }
    }

    /// <summary>
    /// True while an explicit GUI hijack lease owns physical input. The engine keeps sampling
    /// so the UI can navigate, but dispatches nothing. This is deliberately separate from
    /// <see cref="IsMappingEnabled"/>, the user's independent mapping pause.
    /// </summary>
    public bool IsInjectionSuspended => _injectionSuspended;

    /// <summary>
    /// Suspends or resumes injection. Suspending releases everything currently held so no key
    /// sticks down across the switch; resuming lets the next tick re-press whatever is still
    /// held, because <see cref="MappingEngine.Reset"/> cleared the press edges.
    /// </summary>
    public void SetInjectionSuspended(bool suspended)
    {
        lock (_sync)
        {
            if (_injectionSuspended == suspended)
            {
                return;
            }

            _injectionSuspended = suspended;

            if (suspended)
            {
                foreach (var runtime in _runtimes.Values)
                {
                    runtime.Engine.Reset();
                    runtime.Dispatcher.ReleaseAll();
                }
            }
        }
    }

    /// <summary>
    /// Polls the current state of every connected controller without ticking any engine or
    /// dispatching any command, raising <see cref="PresenceSampled"/> for each. This is the
    /// sampling path the UI uses while the mapping loop is stopped, so button navigation and
    /// live highlighting work with the engine off; <see cref="RefreshPresence"/> stays the
    /// presence-only variant that raises nothing.
    /// </summary>
    public void SamplePresence()
    {
        bool presenceChanged;
        lock (_sync)
        {
            var initialPresenceVersion = _presenceVersion;
            var connected = _provider.GetConnected();
            var present = new HashSet<string>(StringComparer.Ordinal);

            foreach (var descriptor in connected)
            {
                present.Add(descriptor.DeviceId);

                var runtime = EnsureRuntime(descriptor);

                var state = _provider.Poll(descriptor.DeviceId);
                if (state is null)
                {
                    // Transient read failure: keep the runtime, try again next sample.
                    continue;
                }

                PresenceSampled?.Invoke(this, new ControllerSampledEventArgs(runtime, state));
            }

            ReconcileAbsence(present, graceTicks: 0);
            presenceChanged = initialPresenceVersion != _presenceVersion;
        }

        if (presenceChanged)
        {
            ControllersChanged?.Invoke();
        }
    }

    /// <summary>
    /// Replaces the active profile of a device (used by the mapping editor and the preset
    /// delete). The profile's own <see cref="GamepadProfile.PresetSlot"/> is preserved, so the
    /// runtime is tagged with the slot it actually runs - a reload onto a survivor slot tags the
    /// survivor, not the app-wide scalar. Callers that apply an edit to the active slot pass a
    /// profile already carrying that slot.
    /// </summary>
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

    /// <summary>
    /// Switches every supplied device as one transaction. If any reload fails, runtimes
    /// already touched are restored before the exception escapes and the global slot stays
    /// unchanged. New devices created afterwards also inherit the selected slot.
    /// </summary>
    public void SwitchPresetSlot(int slot, IReadOnlyDictionary<string, GamepadProfile> profiles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slot, PresetStore.MinSlot, nameof(slot));

        ArgumentNullException.ThrowIfNull(profiles);
        lock (_sync)
        {
            var previous = new Dictionary<string, (ControllerRuntime Runtime, GamepadProfile Profile)>(StringComparer.Ordinal);
            try
            {
                foreach (var (deviceId, profile) in profiles)
                {
                    ArgumentNullException.ThrowIfNull(profile);
                    if (_runtimes.TryGetValue(deviceId, out var runtime))
                    {
                        previous.Add(deviceId, (runtime, runtime.Profile));
                        runtime.Reload(profile with { PresetSlot = slot });
                    }
                }
            }
            catch
            {
                foreach (var (deviceId, prior) in previous)
                {
                    prior.Runtime.Reload(prior.Profile);
                }

                throw;
            }

            ActivePresetSlot = slot;
        }
    }

    /// <summary>Persists the active profile of a device.</summary>
    public void SaveProfile(string deviceId)
    {
        lock (_sync)
        {
            if (_runtimes.TryGetValue(deviceId, out var runtime))
            {
                _profileStore.Save(runtime.Profile);
            }
        }
    }

    /// <summary>Starts the background mapping loop.</summary>
    /// <exception cref="InjectionAlreadyActiveException">
    /// Another process - the resident service or a second instance - already holds the
    /// injection mutex. The engine refuses to start rather than inject every input twice.
    /// </exception>
    public void Start(TimeSpan interval)
    {
        if (_running)
        {
            return;
        }

        // The loop thread must both acquire and release the injection mutex: .NET mutex
        // ownership is per-thread on every platform, and Stop() can arrive on any thread
        // (the daemon stops its engine from a signal-handler thread), so the loop thread
        // is the only thread that can hold the slot for the loop's whole life. Start waits
        // for the loop thread's verdict so a refused start fails on the caller's thread.
        _injectionVerdict = InjectionVerdictPending;
        _running = true;
        _thread = new Thread(() => Run(interval))
        {
            IsBackground = true,
            Name = "dezirojoy-engine",
        };

        try
        {
            _thread.Start();
        }
        catch
        {
            // A thread that never started must not leave the host claiming to run.
            _running = false;
            _thread = null;
            throw;
        }

        if (!SpinWait.SpinUntil(
                () => _injectionVerdict != InjectionVerdictPending,
                TimeSpan.FromSeconds(10)))
        {
            // Cannot happen in practice: the loop thread's first act is the acquire, which
            // is a WaitOne(0). Fail loudly rather than report a start that never happened.
            _running = false;
            _thread = null;
            throw new InvalidOperationException("引擎线程未能在 10 秒内报告注入互斥体的获取结果。");
        }

        if (_injectionVerdict == InjectionVerdictRefused)
        {
            // One injector at a time, machine-wide. Refusing here is the whole point: a
            // second engine would double every input, and the GUI would claim a start that
            // never happened. The loop thread has already exited without ticking.
            _running = false;
            _thread = null;
            throw new InjectionAlreadyActiveException(
                $"另一个进程正在注入映射（注入互斥体 {_injectionMutexName} 已被占用），已拒绝启动以避免重复注入。");
        }
    }

    /// <summary>
    /// Enters the running state without spawning the background loop, leaving every tick to
    /// the caller's own <see cref="TickOnce"/> calls. The test-only counterpart of
    /// <see cref="Start"/>: <see cref="IsRunning"/>, <see cref="Stop"/> and every
    /// running-state branch behave exactly as they do after <see cref="Start"/>, but no second
    /// thread ever ticks the engines. A background loop ticking the same
    /// <see cref="ControllerRuntime"/> the caller ticks would race the engine's
    /// non-thread-safe state, so a test that drives ticks itself must use this instead.
    /// Production code must call <see cref="Start"/>.
    ///
    /// Deliberately does NOT take the injection mutex: it spawns no loop thread, so there is
    /// no thread that could own the slot for a lifetime, and the caller drives every tick
    /// itself. The mutex guards the background loop production runs.
    /// </summary>
    public void StartManual()
    {
        if (_running)
        {
            return;
        }

        _running = true;
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

        lock (_sync)
        {
            foreach (var runtime in _runtimes.Values)
            {
                runtime.Engine.Reset();
                runtime.Dispatcher.ReleaseAll();
            }
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
        // Acquired here, on the loop thread, and released in the finally below: .NET mutex
        // ownership is per-thread on every platform, and Stop() can arrive on any thread
        // (the daemon stops its engine from a signal-handler thread).
        var injection = InjectionMutex.TryAcquire(_injectionMutexName);

        if (injection is null)
        {
            // Another process holds the slot. Report the refusal and exit without ticking,
            // so Start() fails on the caller's thread instead of running a second engine.
            _injectionVerdict = InjectionVerdictRefused;
            return;
        }

        _injectionVerdict = InjectionVerdictHeld;

        try
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
        finally
        {
            // Released on every exit path - Stop, Dispose and an unexpected loop exit
            // alike - by the thread that owns it.
            injection.Release();
        }
    }

    /// <summary>
    /// The first preset switch a tick's commands ask for, or null when they ask for none.
    /// A tick raises at most one request, so repeated commands for the same slot collapse
    /// into one and the first in tick order wins when a tick named two different slots.
    /// </summary>
    private static int? FirstPresetSwitch(IReadOnlyList<InputCommand> commands)
    {
        foreach (var command in commands)
        {
            if (command is SwitchPresetCommand preset)
            {
                return preset.TargetSlot;
            }
        }

        return null;
    }

    private ControllerRuntime EnsureRuntime(GamepadDescriptor descriptor)
    {
        lock (_sync)
        {
            if (_runtimes.TryGetValue(descriptor.DeviceId, out var existing))
            {
                return existing;
            }

            // EnsureDefaults tags the profile with the slot it actually loaded (the active slot,
            // or the primary anchor when this device lacks the active slot), so the runtime is
            // tagged with the slot it truly runs and is not force-fit to the app-wide scalar.
            var profile = _presets.EnsureDefaults(descriptor, ActivePresetSlot);
            var runtime = new ControllerRuntime(descriptor, profile, _sink);
            _runtimes[descriptor.DeviceId] = runtime;
            _presenceVersion++;

            return runtime;
        }
    }

    /// <summary>
    /// Consecutive ticks a device may be missing from the connected list while the engine runs
    /// before its runtime is torn down. XInputGetState fails transiently on a Bluetooth pad, and
    /// removing the runtime then calls <see cref="CommandDispatcher.ReleaseAll"/> - lifting a
    /// held Alt closes the Alt+Tab switcher, so the tick that brings the device back reopens it
    /// at MRU position 1. Five ticks at 2 ms is 10 ms.
    /// <para>
    /// The UI's presence paths pass zero instead: they run at 300 ms, where five ticks would be
    /// a 1.5 s delay before a genuinely unplugged pad reported as gone, and with the engine
    /// stopped nothing is held for ReleaseAll to lift.
    /// </para>
    /// </summary>
    public const int DeviceAbsenceGraceTicks = 5;

    /// <summary>
    /// Drops runtimes whose device has been absent past <paramref name="graceTicks"/>, and
    /// clears the absence count of any device that is back. Callers hold <c>_sync</c>.
    /// </summary>
    private void ReconcileAbsence(HashSet<string> present, int graceTicks)
    {
        foreach (var deviceId in _runtimes.Keys.Where(id => !present.Contains(id)).ToList())
        {
            var missed = _absenceTicks.GetValueOrDefault(deviceId) + 1;
            _absenceTicks[deviceId] = missed;

            if (missed <= graceTicks)
            {
                continue;
            }

            _absenceTicks.Remove(deviceId);
            Remove(deviceId);
        }

        foreach (var deviceId in _absenceTicks.Keys.Where(present.Contains).ToList())
        {
            _absenceTicks.Remove(deviceId);
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
                _presenceVersion++;
            }
        }
    }
}
