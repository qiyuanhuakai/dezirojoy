using DeziroJoy.Core.Input;
using DeziroJoy.Core.Platform;

namespace DeziroJoy.Platform.Windows.XInput;

/// <summary>
/// Polling provider over XInput user slots 0-3. Device ids are slot based
/// ("xinput:0".."xinput:3"): XInput exposes no stable hardware id.
/// </summary>
public sealed class XInputGamepadProvider : IGamepadProvider
{
    public IReadOnlyList<GamepadDescriptor> GetConnected()
    {
        var connected = new List<GamepadDescriptor>();

        for (var slot = 0; slot < XInputNative.MaxControllerCount; slot++)
        {
            if (TryGetState(slot, out _))
            {
                connected.Add(new GamepadDescriptor($"xinput:{slot}", $"Xbox 兼容手柄 #{slot + 1}"));
            }
        }

        return connected;
    }

    public IGamepadState? Poll(string deviceId)
    {
        if (!TryParseSlot(deviceId, out var slot))
        {
            return null;
        }

        // The mapper keys its trigger hysteresis by device, so the slot-parsed id must
        // travel with the sample: the engine polls every connected pad once per tick on
        // one thread, and id-less calls would share one trigger state across pads.
        return TryGetState(slot, out var state) ? XInputStateMapper.ToGamepadState(in state.Gamepad, deviceId) : null;
    }

    public void Dispose()
    {
    }

    private static bool TryGetState(int slot, out XInputState state)
    {
        state = default;

        try
        {
            var candidate = new XInputState();
            if (XInputNative.GetState(slot, ref candidate) != XInputNative.ErrorSuccess)
            {
                return false;
            }

            state = candidate;
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryParseSlot(string? deviceId, out int slot)
    {
        slot = 0;

        if (deviceId is null || !deviceId.StartsWith("xinput:", StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(deviceId.AsSpan("xinput:".Length), out slot)
               && slot >= 0
               && slot < XInputNative.MaxControllerCount;
    }
}
