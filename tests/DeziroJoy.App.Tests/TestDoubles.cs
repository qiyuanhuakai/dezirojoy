using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;

namespace DeziroJoy.App.Tests;

internal sealed class FakeGamepadProvider : IGamepadProvider
{
    private readonly Dictionary<string, GamepadDescriptor> _devices = [];

    private readonly Dictionary<string, IGamepadState> _states = [];

    public void Add(string deviceId, string name)
    {
        _devices[deviceId] = new GamepadDescriptor(deviceId, name);
    }

    public void Remove(string deviceId) => _devices.Remove(deviceId);

    public void SetState(string deviceId, IGamepadState state) => _states[deviceId] = state;

    public IReadOnlyList<GamepadDescriptor> GetConnected() => _devices.Values.ToList();

    public IGamepadState? Poll(string deviceId)
        => _states.TryGetValue(deviceId, out var state) ? state : null;

    public void Dispose()
    {
    }
}

internal sealed class RecordingOutputSink : IOutputSink
{
    public List<string> Calls { get; } = [];

    public void KeyDown(GamepadKey key) => Calls.Add($"down:{key}");

    public void KeyUp(GamepadKey key) => Calls.Add($"up:{key}");

    public void MouseMove(int dx, int dy) => Calls.Add($"move:{dx},{dy}");

    public void ScrollWheel(int horizontalNotches, int verticalNotches)
        => Calls.Add($"wheel:{horizontalNotches},{verticalNotches}");

    public void MouseButtonDown(MouseButton button) => Calls.Add($"mdown:{button}");

    public void MouseButtonUp(MouseButton button) => Calls.Add($"mup:{button}");

    public void Dispose()
    {
    }
}

internal sealed class InMemoryProfileStore : IProfileStore
{
    private readonly Dictionary<string, GamepadProfile> _profiles = [];

    public GamepadProfile? Load(string deviceId) => _profiles.GetValueOrDefault(deviceId);

    public void Save(GamepadProfile profile) => _profiles[profile.DeviceId ?? profile.Name] = profile;

    public bool Delete(string deviceId) => _profiles.Remove(deviceId);

    public IReadOnlyList<GamepadProfile> LoadAll() => _profiles.Values.ToList();
}

internal static class SampleHelper
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 64.0);

    /// <summary>
    /// Puts the engine into the running state without the background loop, so the test drives
    /// every tick itself through <see cref="Sample"/>. The production toggle starts a real
    /// thread that ticks the same <see cref="ControllerRuntime"/> the test ticks, which races
    /// the engine's non-thread-safe state; this keeps the view model's running state identical
    /// while removing the second ticker by construction.
    /// </summary>
    public static void StartEngineManually(MainWindowViewModel viewModel, EngineHost host)
    {
        host.StartManual();
        viewModel.IsEngineRunning = true;
    }

    /// <summary>Runs one host tick and feeds the sampled event to the view model, like the view does.</summary>
    public static void Sample(MainWindowViewModel viewModel, EngineHost host)
    {
        ControllerSampledEventArgs? sampled = null;

        void OnSampled(object? sender, ControllerSampledEventArgs args) => sampled = args;

        host.Sampled += OnSampled;
        try
        {
            host.TickOnce(Tick);
        }
        finally
        {
            host.Sampled -= OnSampled;
        }

        if (sampled is not null)
        {
            viewModel.ApplySample(sampled);
        }
    }
}

/// <summary>
/// In-memory <see cref="IDaemonService"/> for the settings-card tests. It mutates its own state
/// on a completed operation and records every autostart request, so a test can assert both the
/// published state and the calls the card made. <see cref="NextResult"/> simulates the elevated
/// relaunch (Pending) without touching schtasks.
/// </summary>
internal sealed class FakeDaemonService : IDaemonService
{
    public FakeDaemonService(bool isSupported = true, bool isElevated = false)
    {
        IsSupported = isSupported;
        IsElevated = isElevated;
    }

    public bool IsSupported { get; set; }

    public bool IsElevated { get; set; }

    public bool IsDaemonInstalled { get; set; }

    public bool IsAutostartInstalled { get; set; }

    /// <summary>Null means "not installed", mirroring the real service.</summary>
    public string? DaemonRunLevel { get; set; }

    public int RefreshCalls { get; private set; }

    public DaemonOp NextResult { get; set; } = DaemonOp.Completed;

    public List<bool> AutostartRequests { get; } = [];

    public void Refresh() => RefreshCalls++;

    public DaemonActionResult InstallDaemon()
    {
        if (!IsSupported)
        {
            return DaemonActionResult.Unsupported("当前平台不支持常驻服务（仅 Windows）。");
        }

        if (NextResult == DaemonOp.Completed)
        {
            IsDaemonInstalled = true;
            DaemonRunLevel = "Highest";
        }

        return Message("已注册常驻服务");
    }

    public DaemonActionResult UninstallDaemon()
    {
        if (!IsSupported)
        {
            return DaemonActionResult.Unsupported("当前平台不支持常驻服务（仅 Windows）。");
        }

        if (NextResult == DaemonOp.Completed)
        {
            IsDaemonInstalled = false;
            DaemonRunLevel = null;
        }

        return Message("已移除常驻服务");
    }

    public DaemonActionResult SetAutostart(bool enabled)
    {
        if (!IsSupported)
        {
            return DaemonActionResult.Unsupported("当前平台不支持登录自启动（仅 Windows）。");
        }

        AutostartRequests.Add(enabled);

        if (NextResult == DaemonOp.Completed)
        {
            IsAutostartInstalled = enabled;
        }

        return Message(enabled ? "已启用登录自启动" : "已关闭登录自启动");
    }

    private DaemonActionResult Message(string completed)
        => NextResult == DaemonOp.Pending
            ? DaemonActionResult.Pending($"已请求管理员权限，请在提权窗口中确认，完成后点击刷新。（{completed}）")
            : new DaemonActionResult(NextResult, completed);
}
