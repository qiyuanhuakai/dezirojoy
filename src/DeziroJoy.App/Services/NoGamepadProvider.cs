using DeziroJoy.Core.Input;
using DeziroJoy.Core.Platform;

namespace DeziroJoy.App.Services;

/// <summary>Fallback provider for unsupported platforms - reports no controllers.</summary>
public sealed class NoGamepadProvider : IGamepadProvider
{
    public IReadOnlyList<GamepadDescriptor> GetConnected() => [];

    public IGamepadState? Poll(string deviceId) => null;

    public void Dispose()
    {
    }
}
