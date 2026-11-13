using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Profiles;
using DeziroJoy.Platform.Linux.Foreground;
using DeziroJoy.Platform.Windows.Foreground;

namespace DeziroJoy.App.Services;

/// <summary>
/// What the foreground watcher drives. The watcher decides which profile should run - or that
/// mapping should pause - and the target performs it, which is what keeps the watcher testable
/// against a fake and lets a different target be wired without touching the rule logic: the
/// engine host while this process owns mapping - the GUI's own host in local mode
/// (<see cref="EngineHostForegroundTarget"/>), the resident daemon's own host while it does -
/// and nothing at all where a window must only display the rules
/// (<see cref="NullForegroundTarget"/>). Exactly one target applies: two would send every
/// switch and pause twice.
/// </summary>
public interface IForegroundTarget
{
    /// <summary>The profile slot the app is running right now.</summary>
    int ActiveSlot { get; }

    /// <summary>Whether mapping is paused for the program in front.</summary>
    bool IsPaused { get; }

    /// <summary>Moves every connected device to <paramref name="slot"/> as one transaction.</summary>
    void SwitchProfile(int slot);

    /// <summary>Pauses or resumes keyboard and mouse mapping for native gamepad input.</summary>
    void SetPaused(bool paused);
}

/// <summary>
/// The foreground target over a live <see cref="EngineHost"/>. A profile switch goes through
/// <see cref="EngineHost.SwitchPresetSlot"/> - the same atomic path the preset strip and the
/// leader key use - so every device moves together, the app-wide slot stays truthful, and a
/// device that lacks the target slot keeps its own configuration instead of failing the switch.
/// The pause goes through <see cref="EngineHost.SetNativeGamepadPaused"/>, which is independent
/// of the user's mapping pause and the GUI's input hijack lease, so the three can never lift one
/// another's pause. Neither path touches the injection mutex.
/// </summary>
public sealed class EngineHostForegroundTarget : IForegroundTarget
{
    private readonly EngineHost _host;

    private readonly PresetStore _presets;

    public EngineHostForegroundTarget(EngineHost host, PresetStore presets)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(presets);

        _host = host;
        _presets = presets;
    }

    public int ActiveSlot => _host.ActivePresetSlot;

    public bool IsPaused => _host.IsNativeGamepadPaused;

    public void SwitchProfile(int slot)
    {
        var profiles = new Dictionary<string, GamepadProfile>(StringComparer.Ordinal);

        foreach (var controller in _host.Controllers)
        {
            var deviceId = controller.Descriptor.DeviceId;
            // A device that deleted the target slot falls back to its own profile: the rule's
            // intent is "this profile", and the primary anchor is the closest surviving one.
            var profile = _presets.LoadSlot(deviceId, slot)
                ?? _presets.LoadSlot(deviceId, PresetStore.MinSlot);

            if (profile is not null)
            {
                profiles[deviceId] = profile;
            }
        }

        // Nothing connected yet: there is no runtime to move. The watcher re-applies on its next
        // poll, and a device that connects later inherits the slot the host already carries.
        if (profiles.Count > 0)
        {
            _host.SwitchPresetSlot(slot, profiles);
        }
    }

    public void SetPaused(bool paused) => _host.SetNativeGamepadPaused(paused);
}

/// <summary>
/// A foreground target that applies nothing: it records what the rules decided and drives
/// nothing. The GUI in daemon mode is the case for it. The resident daemon owns mapping - the
/// engine, the injection mutex and the app-wide slot - and runs its own watcher over its own
/// engine host for its whole life, so a window that also drove would send every switch and
/// pause twice, once from each side. What the window still owes the user is the panel: the
/// program in front, the rule that matches it and the decision it resolves to, all read off
/// the same shared table the daemon is running.
/// <para>
/// Mirroring the applied slot and pause is what keeps the watcher idempotent against this
/// target, so an unchanged foreground costs one window read exactly as it does against a live
/// engine - and a poll that resolves to what is already displayed calls the target with
/// nothing, which is what makes the display cheap to keep current while the window is closed
/// to the tray.
/// </para>
/// </summary>
public sealed class NullForegroundTarget : IForegroundTarget
{
    private int _activeSlot = PresetStore.MinSlot;

    private bool _paused;

    public int ActiveSlot => _activeSlot;

    public bool IsPaused => _paused;

    public void SwitchProfile(int slot) => _activeSlot = slot;

    public void SetPaused(bool paused) => _paused = paused;
}

/// <summary>
/// Watches the program in front and keeps the rule table in charge: read the foreground, match
/// the rules, and drive the target - a profile switch, or the native-gamepad pause. One poll per
/// <see cref="DefaultInterval"/> is enough; the foreground changes on the user's timescale, not
/// the engine's, so this never runs on the mapping loop and never touches the injection mutex.
/// <para>
/// Driven from one thread - the UI's timer, the resident daemon's own poll loop, or a test - and
/// idempotent: a poll that resolves to what is already in effect calls the target with nothing,
/// so an unchanged foreground costs one window read and no reload. That idempotence is also the
/// self-healing path: a device that reconnects, or a manual preset switch the user made, is
/// brought back in line on the next poll.
/// </para>
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    /// <summary>How often the foreground is read. The engine ticks at 2 ms; this does not.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(400);

    private readonly IForegroundSource _source;

    private readonly IForegroundTarget _target;

    private ForegroundSettings _settings;

    private ForegroundContext _context = ForegroundContext.Empty;

    private ForegroundDecision? _decision;

    private ForegroundDecision? _applied;

    private bool _locked;

    private bool _disposed;

    public ForegroundWatcher(
        IForegroundSource source,
        IForegroundTarget target,
        ForegroundSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        _source = source;
        _target = target;
        _settings = settings ?? new ForegroundSettings();
    }

    public ForegroundSettings Settings => _settings;

    /// <summary>
    /// What this platform can actually see, in the words the panel shows. The Wayland limitation
    /// belongs here rather than in the panel: it is a property of the source, and a test with a
    /// fake source must be able to say "nothing is detectable" without a display server.
    /// </summary>
    public string SourceNote => _source switch
    {
        WindowsForegroundSource => "前台检测：Windows（GetForegroundWindow + 进程名），实时读取当前前台窗口所属进程。",
        X11ForegroundSource =>
            "前台检测：Linux X11（最佳努力，依赖窗口管理器的 _NET_ACTIVE_WINDOW）。"
                + "Wayland 不提供全局前台查询，读取不到前台时规则不会命中，始终使用桌面默认配置。",
        _ => "前台检测：当前平台不支持读取前台程序，所有规则都不会命中，始终使用桌面默认配置。",
    };

    /// <summary>The program in front as the last poll read it, or null when there is none.</summary>
    public string? ForegroundProcessName => _context.ProcessName;

    /// <summary>What the last poll resolved to, including the reason the UI quotes.</summary>
    public ForegroundDecision? LastDecision => _decision;

    /// <summary>
    /// What is actually in effect: the last decision that was applied, or the last one evaluated
    /// while nothing has been applied yet. The two differ only while the profile is locked - the
    /// rules keep saying what they want, and this keeps saying what is running - which is what
    /// lets the panel explain the lock instead of quoting a rule that is not in charge.
    /// </summary>
    public ForegroundDecision? AppliedDecision => _applied ?? _decision;

    /// <summary>
    /// Whether the profile in effect is locked. Locked freezes whatever is running: the
    /// foreground keeps being read - the UI still shows what is in front - but no rule is
    /// applied, so a game that keeps taking the foreground cannot take the profile with it.
    /// </summary>
    public bool IsLocked => _locked;

    /// <summary>Raised after every poll that changed what the UI shows.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Replaces the rule table and re-evaluates at once, so an edit lands without waiting for
    /// the next poll.
    /// </summary>
    public void Update(ForegroundSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        Refresh();
    }

    /// <summary>Reads the foreground, resolves the rules, and applies the outcome.</summary>
    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _context = _source.Read();
        _decision = ForegroundRules.Evaluate(_settings, _context);

        // Locked is the one state that applies nothing: the profile in effect is the user's
        // choice, and the rules stand down until it is unlocked again.
        if (!_locked)
        {
            Apply(_decision);
            _applied = _decision;
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    /// Locks or unlocks the profile in effect. Locking does not change what runs - it stops the
    /// rules from changing it - and unlocking hands control back on the next refresh.
    /// </summary>
    public void SetLocked(bool locked)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_locked == locked)
        {
            return;
        }

        _locked = locked;

        if (!locked)
        {
            Refresh();
            return;
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    /// Performs one decision. A pause leaves the profile alone - mapping is off, so which
    /// profile would run is moot - and everything else resolves the desired slot against the
    /// target's actual one, which is what makes a poll with nothing to do call the target with
    /// nothing.
    /// </summary>
    private void Apply(ForegroundDecision decision)
    {
        if (decision.Kind == ForegroundDecisionKind.Paused)
        {
            if (!_target.IsPaused)
            {
                _target.SetPaused(true);
            }

            return;
        }

        if (_target.IsPaused)
        {
            _target.SetPaused(false);
        }

        if (_target.ActiveSlot != decision.Slot)
        {
            _target.SwitchProfile(decision.Slot);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The pause is a foreground rule's, not the user's: leaving it behind would keep
        // mapping off in a program that no longer has the foreground.
        if (_decision?.Kind == ForegroundDecisionKind.Paused && _target.IsPaused)
        {
            _target.SetPaused(false);
        }

        // The source is this watcher's for its lifetime - one source per watcher, and nothing
        // else reads it - so a source that holds a connection (the X11 one does) is closed here
        // rather than left to the process exit.
        if (_source is IDisposable source)
        {
            source.Dispose();
        }

        StateChanged = null;
    }
}

/// <summary>
/// Picks the foreground source for this platform. Windows reads the real foreground window and
/// its process; Linux asks X11 best-effort, which answers under X11 and stays silent under
/// Wayland - where no global foreground query exists at all. Everywhere else, and on a Linux box
/// with no X server, the source reports nothing and the desktop default profile runs.
/// </summary>
public static class ForegroundSourceFactory
{
    public static IForegroundSource Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsForegroundSource();
        }

        if (OperatingSystem.IsLinux())
        {
            return new X11ForegroundSource();
        }

        return NullForegroundSource.Instance;
    }
}
