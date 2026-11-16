using DeziroJoy.Core.Input;
using DeziroJoy.Platform.Sdl3;

namespace DeziroJoy.Tools.Sdl3Probe;

/// <summary>
/// Standalone SDL3 input probe (roadmap S1). It owns no devices between runs: <c>list</c>
/// initializes SDL, prints the runtime and every recognized gamepad - type, GUID, VID/PID,
/// serial, path, instance id, player index, connection, mapping and its source, touchpad and
/// sensor capabilities, and what the legacy XInput/evdev backends see alongside - then
/// releases SDL again. <c>watch</c> keeps the provider open and prints hot-plug transitions
/// plus the normalized state (buttons, dual sticks, triggers) the engine would consume.
/// <para>
/// Exit codes: 0 success, 1 SDL runtime unavailable, 2 usage error.
/// </para>
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitSdlUnavailable = 1;
    private const int ExitUsage = 2;

    private static int Main(string[] args)
    {
        var command = "list";
        var json = false;
        var intervalMs = 100;
        double? durationSeconds = null;

        foreach (var arg in args)
        {
            switch (arg)
            {
                case "list":
                    command = "list";
                    break;
                case "watch":
                    command = "watch";
                    break;
                case "--json":
                    json = true;
                    break;
                case "-h" or "--help":
                    PrintUsage();
                    return ExitOk;
                default:
                    if (arg.StartsWith("--interval=", StringComparison.Ordinal)
                        && int.TryParse(arg["--interval=".Length..], out var parsedInterval)
                        && parsedInterval is > 0 and <= 60000)
                    {
                        intervalMs = parsedInterval;
                        break;
                    }

                    if (arg.StartsWith("--duration=", StringComparison.Ordinal)
                        && double.TryParse(arg["--duration=".Length..], out var parsedDuration)
                        && parsedDuration > 0)
                    {
                        durationSeconds = parsedDuration;
                        break;
                    }

                    Console.Error.WriteLine($"未知参数: {arg}");
                    PrintUsage();
                    return ExitUsage;
            }
        }

        return command == "watch"
            ? RunWatch(json, intervalMs, durationSeconds)
            : RunList(json);
    }

    /// <summary>
    /// One-shot scan through the provider's own probe path, so what the CLI prints is exactly
    /// what the daemon's scan would report for the same devices.
    /// </summary>
    private static int RunList(bool json)
    {
        var diagnostic = Sdl3GamepadProvider.ProbeOnce();
        var legacy = LegacyBackendCrossCheck.Collect();

        if (json)
        {
            ProbeFormat.PrintJson(diagnostic, legacy);
        }
        else
        {
            ProbeFormat.PrintText(diagnostic, legacy);
        }

        return diagnostic.Outcome == Sdl3ScanOutcome.SdlUnavailable ? ExitSdlUnavailable : ExitOk;
    }

    private static int RunWatch(bool json, int intervalMs, double? durationSeconds)
    {
        using var provider = new Sdl3GamepadProvider(TimeSpan.FromMilliseconds(intervalMs));
        var stopping = false;

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // Graceful: finish the loop, dispose the provider (which closes every handle),
            // and let the process exit normally.
            eventArgs.Cancel = true;
            stopping = true;
        };

        DateTime? deadline = durationSeconds is null
            ? null
            : DateTime.UtcNow + TimeSpan.FromSeconds(durationSeconds.Value);

        if (!json)
        {
            Console.WriteLine($"监听中（每 {intervalMs} ms 刷新，Ctrl+C 或 --duration 退出）...");
        }

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var states = new Dictionary<string, IGamepadState?>(StringComparer.Ordinal);
        var everSeen = 0;

        while (!stopping)
        {
            var connected = provider.GetConnected();

            foreach (var descriptor in connected)
            {
                if (!seen.ContainsKey(descriptor.DeviceId))
                {
                    seen[descriptor.DeviceId] = descriptor.Name;
                    states[descriptor.DeviceId] = null;
                    everSeen++;
                    if (!json)
                    {
                        Console.WriteLine($"[{Stamp()}] 已连接  {descriptor.Name} ({descriptor.DeviceId})");
                    }
                }

                states[descriptor.DeviceId] = provider.Poll(descriptor.DeviceId);
            }

            foreach (var deviceId in seen.Keys.Where(id => connected.All(d => d.DeviceId != id)).ToList())
            {
                if (!json)
                {
                    Console.WriteLine($"[{Stamp()}] 已断开  {seen[deviceId]} ({deviceId})");
                }

                seen.Remove(deviceId);
                states.Remove(deviceId);
            }

            if (json)
            {
                PrintWatchJson(connected, states);
            }
            else
            {
                foreach (var descriptor in connected)
                {
                    ProbeFormat.PrintDeviceStateLine(
                        descriptor,
                        states.TryGetValue(descriptor.DeviceId, out var state) ? state : null);
                }
            }

            if (deadline is not null && DateTime.UtcNow >= deadline)
            {
                break;
            }

            Thread.Sleep(intervalMs);
        }

        if (!json)
        {
            Console.WriteLine($"已停止。本次监听共出现 {everSeen} 个设备，结束时 {seen.Count} 个在位。");
        }

        return ExitOk;
    }

    private static void PrintWatchJson(
        IReadOnlyList<GamepadDescriptor> connected,
        Dictionary<string, IGamepadState?> states)
    {
        var payload = new
        {
            stamp = Stamp(),
            devices = connected.Select(descriptor => new
            {
                deviceId = descriptor.DeviceId,
                name = descriptor.Name,
                buttons = states.TryGetValue(descriptor.DeviceId, out var state) && state is not null
                    ? state.PressedButtons.ToString()
                    : null,
                extraButtons = states.TryGetValue(descriptor.DeviceId, out var extraState) && extraState is not null
                    ? extraState.ExtraButtons.ToArray()
                    : Array.Empty<int>(),
                leftStick = states.TryGetValue(descriptor.DeviceId, out var leftState) && leftState is not null
                    ? new { x = leftState.LeftStickX, y = leftState.LeftStickY }
                    : null,
                rightStick = states.TryGetValue(descriptor.DeviceId, out var rightState) && rightState is not null
                    ? new { x = rightState.RightStickX, y = rightState.RightStickY }
                    : null,
                leftTrigger = states.TryGetValue(descriptor.DeviceId, out var triggerState) && triggerState is not null
                    && triggerState.PressedButtons.Has(GamepadButton.LeftTrigger),
                rightTrigger = states.TryGetValue(descriptor.DeviceId, out var rightTriggerState) && rightTriggerState is not null
                    && rightTriggerState.PressedButtons.Has(GamepadButton.RightTrigger),
            }).ToArray(),
        };

        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload));
    }

    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    private static void PrintUsage()
    {
        Console.WriteLine("usage: Sdl3Probe [list|watch] [--json] [--interval=<ms>] [--duration=<s>]");
        Console.WriteLine();
        Console.WriteLine("  list    一次性枚举并报告 SDL3 运行时、设备身份、映射与能力（默认）");
        Console.WriteLine("  watch   持续监听热插拔并打印归一化输入状态");
        Console.WriteLine("  --json  以 JSON 输出（list 为单个对象，watch 每轮一个对象）");
        Console.WriteLine("  --interval=<ms>   watch 刷新间隔，默认 100");
        Console.WriteLine("  --duration=<s>    watch 运行秒数，到时自动退出（脚本化验证用）");
    }
}
