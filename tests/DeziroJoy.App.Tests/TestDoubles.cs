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

    /// <summary>Runs one host tick and feeds the sampled event to the view model, like the view does.</summary>
    public static void Sample(MainWindowViewModel viewModel, EngineHost host)
    {
        ControllerSampledEventArgs? sampled = null;
        host.Sampled += (_, args) => sampled = args;

        host.TickOnce(Tick);

        if (sampled is not null)
        {
            viewModel.ApplySample(sampled);
        }
    }
}
