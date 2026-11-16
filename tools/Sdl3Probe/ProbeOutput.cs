using System.Globalization;
using System.Text.Json;
using DeziroJoy.Core.Input;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Sdl3;
using DeziroJoy.Platform.Windows.XInput;

namespace DeziroJoy.Tools.Sdl3Probe;

/// <summary>
/// What the legacy backends currently enumerate, recorded next to the SDL view. The roadmap
/// makes this S1 evidence: when SDL and XInput/evdev both see one physical device, the two
/// lists are what later deduplication (S3) is matched against. Neither provider is modified
/// or driven - they are only asked what they see, and each answers "unavailable" instead of
/// throwing when its platform is not the one it runs on.
/// </summary>
internal static class LegacyBackendCrossCheck
{
    internal sealed record Result(IReadOnlyList<LegacyDevice> XInput, EvdevScanDiagnostic? Evdev, string? EvdevError);

    internal sealed record LegacyDevice(string DeviceId, string Name);

    public static Result Collect()
    {
        return new Result(CollectXInput(), CollectEvdev(out var evdevError), evdevError);
    }

    private static List<LegacyDevice> CollectXInput()
    {
        try
        {
            using var provider = new XInputGamepadProvider();
            return provider.GetConnected()
                .Select(descriptor => new LegacyDevice(descriptor.DeviceId, descriptor.Name))
                .ToList();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    private static EvdevScanDiagnostic? CollectEvdev(out string? error)
    {
        error = null;

        try
        {
            return EvdevGamepadProvider.ProbeOnce();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            error = ex.Message;
            return null;
        }
    }
}

/// <summary>Text and JSON rendering of one SDL probe result.</summary>
internal static class ProbeFormat
{
    public static void PrintText(Sdl3ScanDiagnostic diagnostic, LegacyBackendCrossCheck.Result legacy)
    {
        Console.WriteLine($"SDL3 运行时 : {DescribeRuntime(diagnostic.Runtime)}");
        Console.WriteLine($"初始化     : {(diagnostic.Outcome == Sdl3ScanOutcome.SdlUnavailable ? "失败" : "成功")}");
        Console.WriteLine($"手柄数量   : {diagnostic.GamepadCount}");
        Console.WriteLine();

        foreach (var (device, index) in diagnostic.Devices.Select((device, index) => (device, index)))
        {
            PrintDeviceText(device, Sdl3GamepadIdentity.Compose(device), index + 1);
        }

        foreach (var failure in diagnostic.OpenFailures)
        {
            Console.WriteLine($"[打开失败] 实例 #{failure.InstanceId}: {failure.Error}");
            Console.WriteLine();
        }

        PrintLegacyText(legacy);

        if (diagnostic.Guidance.Length > 0)
        {
            Console.WriteLine("建议:");
            foreach (var line in diagnostic.Guidance.Split('\n'))
            {
                Console.WriteLine($"  {line}");
            }

            Console.WriteLine();
        }
    }

    public static void PrintJson(Sdl3ScanDiagnostic diagnostic, LegacyBackendCrossCheck.Result legacy)
    {
        var payload = new
        {
            sdl = new
            {
                version = diagnostic.Runtime?.VersionText,
                versionPacked = diagnostic.Runtime?.Version,
                revision = diagnostic.Runtime?.Revision,
                initialized = diagnostic.Outcome != Sdl3ScanOutcome.SdlUnavailable,
                error = diagnostic.InitError,
            },
            outcome = diagnostic.Outcome.ToString(),
            gamepadCount = diagnostic.GamepadCount,
            devices = diagnostic.Devices.Select(device => DeviceJson(device, Sdl3GamepadIdentity.Compose(device))).ToArray(),
            openFailures = diagnostic.OpenFailures.Select(failure => new { instanceId = failure.InstanceId, error = failure.Error }).ToArray(),
            unstableIdentityInstanceIds = diagnostic.UnstableIdentityInstanceIds.ToArray(),
            legacy = new
            {
                xinput = legacy.XInput.Select(device => new { deviceId = device.DeviceId, name = device.Name }).ToArray(),
                evdev = legacy.Evdev is null
                    ? null
                    : new
                    {
                        outcome = legacy.Evdev.Outcome.ToString(),
                        nodeCount = legacy.Evdev.NodeCount,
                        gamepadCount = legacy.Evdev.GamepadCount,
                    },
                evdevError = legacy.EvdevError,
            },
            guidance = diagnostic.Guidance,
        };

        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static void PrintDeviceStateLine(GamepadDescriptor descriptor, IGamepadState? state)
    {
        if (state is null)
        {
            Console.WriteLine($"{descriptor.Name} ({descriptor.DeviceId}): 设备已断开");
            return;
        }

        var buttons = state.PressedButtons == GamepadButtonMask.None ? "-" : state.PressedButtons.ToString();
        var extras = state.ExtraButtons.Count == 0
            ? string.Empty
            : $" extra[{string.Join(",", state.ExtraButtons)}]";

        var left = $"({Fmt(state.LeftStickX)},{Fmt(state.LeftStickY)})";
        var right = $"({Fmt(state.RightStickX)},{Fmt(state.RightStickY)})";
        var leftTrigger = state.PressedButtons.Has(GamepadButton.LeftTrigger) ? "按下" : "-";
        var rightTrigger = state.PressedButtons.Has(GamepadButton.RightTrigger) ? "按下" : "-";

        Console.WriteLine($"{descriptor.Name} ({descriptor.DeviceId}): 按钮 {buttons}{extras} | 左摇杆 {left} 右摇杆 {right} | 扳机 左 {leftTrigger} 右 {rightTrigger}");
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Guidance and sensor names are Chinese; the relaxed encoder keeps them readable
        // in a terminal instead of \uXXXX escapes.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static object DeviceJson(Sdl3GamepadInfo device, Sdl3GamepadIdentity identity) => new
    {
        instanceId = device.InstanceId,
        deviceId = identity.DeviceId,
        identityStable = identity.IsStable,
        name = device.Name,
        type = device.TypeName,
        typeOrdinal = device.Type,
        guid = device.GuidString,
        vendor = device.Vendor.ToString("x4", CultureInfo.InvariantCulture),
        product = device.Product.ToString("x4", CultureInfo.InvariantCulture),
        productVersion = device.ProductVersion.ToString("x4", CultureInfo.InvariantCulture),
        serial = device.Serial,
        path = device.Path,
        playerIndex = device.PlayerIndex,
        connection = device.ConnectionState,
        mapping = device.Mapping,
        mappingSource = device.MappingSource,
        faceButtonLabels = device.FaceButtonLabels.ToArray(),
        declaredButtonCount = device.DeclaredButtonCount,
        declaredButtons = device.DeclaredButtons.ToArray(),
        declaredAxes = device.DeclaredAxes.ToArray(),
        touchpads = device.TouchpadFingerCounts.Select(fingers => new { fingers }).ToArray(),
        sensors = device.Sensors.ToArray(),
    };

    private static void PrintDeviceText(Sdl3GamepadInfo device, Sdl3GamepadIdentity identity, int ordinal)
    {
        Console.WriteLine($"[{ordinal}] {device.Name}");
        Console.WriteLine($"    设备 ID     : {identity.DeviceId}");
        Console.WriteLine($"    身份稳定性  : {(identity.IsStable ? "稳定（GUID + 序列号/路径）" : "不稳定（无序列号与路径，回退实例 ID）")}");
        Console.WriteLine($"    实例 ID     : {device.InstanceId}");
        Console.WriteLine($"    类型        : {device.TypeName} ({device.Type})");
        Console.WriteLine($"    GUID        : {device.GuidString}");
        Console.WriteLine($"    VID/PID     : {device.Vendor:x4}:{device.Product:x4}（版本 {device.ProductVersion:x4}）");
        Console.WriteLine($"    序列号      : {Or(device.Serial, "无")}");
        Console.WriteLine($"    路径        : {Or(device.Path, "无")}");
        Console.WriteLine($"    玩家索引    : {device.PlayerIndex}");
        Console.WriteLine($"    连接        : {device.ConnectionState}");
        Console.WriteLine($"    映射来源    : {device.MappingSource}");
        Console.WriteLine($"    映射        : {Or(device.Mapping, "无")}");
        Console.WriteLine($"    面键标签    : {string.Join(" ", device.FaceButtonLabels)}");
        Console.WriteLine($"    声明按钮数  : {device.DeclaredButtonCount}");
        Console.WriteLine($"    声明控件    : 按钮 [{string.Join(" ", device.DeclaredButtons)}]，轴 [{string.Join(" ", device.DeclaredAxes)}]");
        Console.WriteLine($"    触摸板      : {DescribeTouchpads(device)}");
        Console.WriteLine($"    传感器      : {DescribeSensors(device)}");
        Console.WriteLine();
    }

    private static void PrintLegacyText(LegacyBackendCrossCheck.Result legacy)
    {
        Console.WriteLine("旧后端对照（迁移期证据，不参与本次读取）:");

        if (legacy.XInput.Count == 0)
        {
            Console.WriteLine("    XInput      : 未枚举到手柄（或当前平台不可用）");
        }
        else
        {
            foreach (var device in legacy.XInput)
            {
                Console.WriteLine($"    XInput      : {device.DeviceId} {device.Name}");
            }
        }

        if (legacy.Evdev is null)
        {
            Console.WriteLine($"    evdev       : 不可用（{legacy.EvdevError ?? "未知错误"}）");
        }
        else
        {
            Console.WriteLine($"    evdev       : {legacy.Evdev.Outcome}，扫描 {legacy.Evdev.NodeCount} 个节点，{legacy.Evdev.GamepadCount} 个手柄");
        }

        Console.WriteLine();
    }

    private static string DescribeRuntime(Sdl3RuntimeInfo? runtime)
        => runtime is null || runtime.Version == 0
            ? "不可用（未找到原生 SDL3 运行时）"
            : $"{runtime.VersionText}{(runtime.Revision.Length == 0 ? string.Empty : $" (revision {runtime.Revision})")}";

    private static string DescribeTouchpads(Sdl3GamepadInfo device)
        => device.TouchpadCount == 0
            ? "无"
            : string.Join("，", device.TouchpadFingerCounts.Select(fingers => $"{fingers} 触点"));

    private static string DescribeSensors(Sdl3GamepadInfo device)
        => device.Sensors.Count == 0 ? "无" : string.Join("，", device.Sensors);

    private static string Or(string value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    private static string Fmt(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
