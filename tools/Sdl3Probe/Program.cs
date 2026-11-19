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
/// <c>joysticks</c> lists the devices SDL only knows as plain joysticks, and <c>map</c> walks
/// one of them through the mapping-supplement flow and saves a standard SDL mapping.
/// <para>
/// Exit codes: 0 success, 1 SDL runtime unavailable (or a save SDL refused), 2 usage error.
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
        string? guidFilter = null;
        string? mappingFilePath = null;

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
                case "joysticks":
                    command = "joysticks";
                    break;
                case "raw":
                    command = "raw";
                    break;
                case "map":
                    command = "map";
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

                    if (arg.StartsWith("--guid=", StringComparison.Ordinal)
                        && arg["--guid=".Length..].Trim().Length > 0)
                    {
                        guidFilter = arg["--guid=".Length..].Trim();
                        break;
                    }

                    if (arg.StartsWith("--file=", StringComparison.Ordinal)
                        && arg["--file=".Length..].Trim().Length > 0)
                    {
                        mappingFilePath = arg["--file=".Length..].Trim();
                        break;
                    }

                    Console.Error.WriteLine($"未知参数: {arg}");
                    PrintUsage();
                    return ExitUsage;
            }
        }

        return command switch
        {
            "watch" => RunWatch(json, intervalMs, durationSeconds),
            "joysticks" => RunJoysticks(json),
            "raw" => RunRaw(guidFilter, intervalMs, durationSeconds),
            "map" => JoystickMappingFlow.Run(guidFilter, mappingFilePath, json),
            _ => RunList(json),
        };
    }

    /// <summary>
    /// One-shot scan through the provider's own probe path, so what the CLI prints is exactly
    /// what the daemon's scan would report for the same devices. The persisted mapping file is
    /// loaded the same way the daemon loads it, so a device the <c>map</c> flow mapped shows up
    /// here as a standard gamepad instead of silently disappearing from both surfaces.
    /// </summary>
    private static int RunList(bool json)
    {
        var diagnostic = Sdl3GamepadProvider.ProbeOnce(new Sdl3MappingFileSource());
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

    /// <summary>
    /// Plain-joystick scan: the devices SDL only knows as joysticks, with the raw control counts
    /// a mapping is written against. This is the non-interactive half of the mapping flow - what
    /// a user (or a script) runs to see whether a mapping is still missing, without touching
    /// stdin.
    /// </summary>
    private static int RunJoysticks(bool json)
    {
        var scan = Sdl3JoystickCatalog.Scan();

        if (json)
        {
            JoystickFormat.PrintJson(scan);
        }
        else
        {
            JoystickFormat.PrintText(scan);
            JoystickFormat.PrintMappings(Sdl3MappingFile.Load(Sdl3MappingFile.DefaultPath), Sdl3MappingFile.DefaultPath);
        }

        return scan.Initialized ? ExitOk : ExitSdlUnavailable;
    }

    private static int RunRaw(string? guidFilter, int intervalMs, double? durationSeconds)
    {
        var scan = Sdl3JoystickCatalog.Scan();
        if (!scan.Initialized)
        {
            Console.Error.WriteLine(scan.InitError ?? "SDL3 初始化失败");
            return ExitSdlUnavailable;
        }

        var candidates = scan.Unmapped
            .Where(device => guidFilter is null || device.GuidString.Equals(guidFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(device => device.AxisCount + device.HatCount)
            .ThenByDescending(device => device.ButtonCount)
            .ToList();
        var target = candidates.FirstOrDefault();
        if (target is null)
        {
            Console.Error.WriteLine("没有匹配的原始 Joystick。");
            return ExitUsage;
        }

        var opened = Sdl3JoystickCatalog.OpenJoystick(target.InstanceId);
        if (opened.Reader is null)
        {
            Console.Error.WriteLine(opened.Error ?? "无法打开原始 Joystick");
            return ExitSdlUnavailable;
        }

        using var reader = opened.Reader;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(durationSeconds ?? 30);
        Sdl3JoystickSnapshot? previous = null;
        Console.WriteLine($"raw target: {target.Name} instance={target.InstanceId} guid={target.GuidString}");
        while (DateTime.UtcNow < deadline)
        {
            var now = reader.Sample();
            if (!SameRaw(previous, now))
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    stamp = Stamp(),
                    buttons = now.Buttons.Select((pressed, index) => new { index, pressed }).Where(button => button.pressed).Select(button => button.index).ToArray(),
                    axes = now.Axes,
                    hats = now.Hats,
                }));
                previous = now;
            }

            Thread.Sleep(intervalMs);
        }

        return ExitOk;
    }

    private static bool SameRaw(Sdl3JoystickSnapshot? left, Sdl3JoystickSnapshot right)
        => left is not null
            && left.Buttons.SequenceEqual(right.Buttons)
            && left.Axes.SequenceEqual(right.Axes)
            && left.Hats.SequenceEqual(right.Hats);

    private static int RunWatch(bool json, int intervalMs, double? durationSeconds)
    {
        // The provider keeps its own default scan interval (250 ms) on purpose: the watch
        // loop cadence then stays independent of the scan throttle, and the refresh-rate
        // line can show state refreshes climbing at the loop rate while enumeration stays
        // at the scan interval - the exact relationship that was broken when the pump was
        // throttled to the scan.
        using var provider = new Sdl3GamepadProvider();
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
        var lastRefreshCount = provider.StateRefreshCount;
        var lastRefreshStamp = DateTime.UtcNow;

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
                PrintWatchJson(connected, states, provider.StateRefreshCount);
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

            ReportRefreshRate(provider, ref lastRefreshCount, ref lastRefreshStamp, json);

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
        Dictionary<string, IGamepadState?> states,
        long stateRefreshCount)
    {
        var payload = new
        {
            stamp = Stamp(),
            stateRefreshCount,
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

    /// <summary>
    /// Prints how often the provider actually pumps SDL state, which is the observable
    /// that exposes stale-state input lag: a healthy provider refreshes once per tick, so
    /// the printed rate matches the watch interval. A rate pinned near the scan interval
    /// (4/s at the provider default) instead of the tick rate is the bug this line exists
    /// to show. Reported every two seconds; JSON carries the raw count per tick.
    /// </summary>
    private static void ReportRefreshRate(
        Sdl3GamepadProvider provider,
        ref long lastRefreshCount,
        ref DateTime lastRefreshStamp,
        bool json)
    {
        var now = DateTime.UtcNow;
        var elapsed = now - lastRefreshStamp;
        if (elapsed < TimeSpan.FromSeconds(2))
        {
            return;
        }

        var count = provider.StateRefreshCount;
        var rate = (count - lastRefreshCount) / elapsed.TotalSeconds;

        if (!json)
        {
            Console.WriteLine($"[{Stamp()}] SDL 状态刷新 {rate:0.#} 次/s（累计 {count}，枚举间隔 {provider.ScanInterval.TotalMilliseconds:0.#} ms）");
        }

        lastRefreshCount = count;
        lastRefreshStamp = now;
    }

    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    private static void PrintUsage()
    {
        Console.WriteLine("usage: Sdl3Probe [list|watch|joysticks|raw|map] [--json] [--interval=<ms>] [--duration=<s>]");
        Console.WriteLine("                [--guid=<guid>] [--file=<path>]");
        Console.WriteLine();
        Console.WriteLine("  list       一次性枚举并报告 SDL3 运行时、设备身份、映射与能力（默认）");
        Console.WriteLine("  watch      持续监听热插拔并打印归一化输入状态");
        Console.WriteLine("  joysticks  只列出只能作普通 Joystick 枚举、无标准 Gamepad 映射的设备");
        Console.WriteLine("  raw        只读打印原始按钮、轴和方向帽变化，不写配置");
        Console.WriteLine("  map        为这样的设备交互式逐键生成标准 SDL 映射并保存到配置根目录");
        Console.WriteLine("  --json     以 JSON 输出（list/joysticks 为单个对象，watch 每轮一个对象）");
        Console.WriteLine("  --interval=<ms>   watch 刷新间隔，默认 100");
        Console.WriteLine("  --duration=<s>    watch 运行秒数，到时自动退出（脚本化验证用）");
        Console.WriteLine("  --guid=<guid>     map：直接映射该 GUID 的设备，跳过设备选择");
        Console.WriteLine("  --file=<path>     map：映射文件路径，默认为配置根目录的 sdl-mappings.json");
        Console.WriteLine();
        Console.WriteLine("map 需要交互式终端；标准输入被重定向时会打印待映射设备并退出，不会阻塞。");
    }
}
