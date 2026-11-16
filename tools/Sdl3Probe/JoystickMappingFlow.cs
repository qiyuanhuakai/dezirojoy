using System.Globalization;
using System.Text;
using DeziroJoy.Platform.Sdl3;

namespace DeziroJoy.Tools.Sdl3Probe;

/// <summary>
/// Rendering of one plain-joystick scan, and of the mapping file the capture flow writes.
/// The joystick list is the evidence a device needs a hand-written mapping: GUID, name, path,
/// VID/PID and the raw button/axis/hat counts a mapping is written against - with the devices
/// SDL already reads as standard gamepads counted separately, so "nothing to map" is never
/// confused with "SDL sees nothing".
/// </summary>
internal static class JoystickFormat
{
    public static void PrintText(Sdl3JoystickScan scan)
    {
        Console.WriteLine($"SDL3 运行时 : {DescribeRuntime(scan.Runtime)}");
        Console.WriteLine($"初始化     : {(scan.Initialized ? "成功" : "失败")}");
        Console.WriteLine($"Joystick   : {scan.Joysticks.Count}（其中 {scan.MappedAsGamepadCount} 个已有标准 Gamepad 映射）");
        Console.WriteLine();

        if (!scan.Initialized)
        {
            Console.WriteLine($"初始化失败：{scan.InitError ?? "未知错误"}");
            Console.WriteLine();
            return;
        }

        if (scan.Unmapped.Count == 0)
        {
            Console.WriteLine("没有只能作普通 Joystick 枚举、缺少标准 Gamepad 映射的设备。");
            Console.WriteLine();
        }

        foreach (var (joystick, index) in scan.Unmapped.Select((joystick, index) => (joystick, index)))
        {
            PrintJoystickText(joystick, index + 1);
        }

        Console.WriteLine(scan.Hint);
        Console.WriteLine();
    }

    public static void PrintJson(Sdl3JoystickScan scan)
    {
        var payload = new
        {
            sdl = new
            {
                version = scan.Runtime?.VersionText,
                revision = scan.Runtime?.Revision,
                initialized = scan.Initialized,
                error = scan.InitError,
            },
            platform = scan.Platform,
            joystickCount = scan.Joysticks.Count,
            mappedAsGamepadCount = scan.MappedAsGamepadCount,
            unmappedCount = scan.Unmapped.Count,
            unmapped = scan.Unmapped.Select(JoystickJson).ToArray(),
            gamepadMapped = scan.Joysticks.Where(joystick => joystick.HasStandardMapping).Select(JoystickJson).ToArray(),
            hint = scan.Hint,
        };

        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
            payload,
            ProbeFormat.JsonOptions));
    }

    public static void PrintMappings(Sdl3MappingSnapshot snapshot, string path)
    {
        Console.WriteLine($"映射文件   : {path}");
        foreach (var line in Sdl3MappingFileFormat.Describe(snapshot))
        {
            Console.WriteLine(line);
        }

        Console.WriteLine();
    }

    private static object JoystickJson(Sdl3JoystickInfo joystick) => new
    {
        instanceId = joystick.InstanceId,
        name = joystick.Name,
        guid = joystick.GuidString,
        vendor = joystick.Vendor.ToString("x4", CultureInfo.InvariantCulture),
        product = joystick.Product.ToString("x4", CultureInfo.InvariantCulture),
        productVersion = joystick.ProductVersion.ToString("x4", CultureInfo.InvariantCulture),
        serial = joystick.Serial,
        path = joystick.Path,
        type = joystick.TypeName,
        buttonCount = joystick.ButtonCount,
        axisCount = joystick.AxisCount,
        hatCount = joystick.HatCount,
        hasStandardMapping = joystick.HasStandardMapping,
    };

    private static void PrintJoystickText(Sdl3JoystickInfo joystick, int ordinal)
    {
        Console.WriteLine($"[{ordinal}] {joystick.Name}");
        Console.WriteLine($"    实例 ID     : {joystick.InstanceId}");
        Console.WriteLine($"    类型        : {joystick.TypeName}");
        Console.WriteLine($"    GUID        : {joystick.GuidString}");
        Console.WriteLine($"    VID/PID     : {joystick.Vendor:x4}:{joystick.Product:x4}（版本 {joystick.ProductVersion:x4}）");
        Console.WriteLine($"    序列号      : {Or(joystick.Serial, "无")}");
        Console.WriteLine($"    路径        : {Or(joystick.Path, "无")}");
        Console.WriteLine($"    原始控件数  : 按钮 {joystick.ButtonCount}，轴 {joystick.AxisCount}，方向帽 {joystick.HatCount}");
        Console.WriteLine($"    标准映射    : 无（SDL 只把它当普通 Joystick，不能当作已有标准布局使用）");
        Console.WriteLine();
    }

    private static string DescribeRuntime(Sdl3RuntimeInfo? runtime)
        => runtime is null || runtime.Version == 0
            ? "不可用（未找到原生 SDL3 运行时）"
            : $"{runtime.VersionText}{(runtime.Revision.Length == 0 ? string.Empty : $" (revision {runtime.Revision})")}";

    private static string Or(string value, string fallback)
        => string.IsNullOrEmpty(value) ? fallback : value;
}

/// <summary>
/// The interactive <c>map</c> flow: pick one plain joystick, walk the standard control list,
/// and produce a mapping string SDL accepts. It is deliberately the only part of the probe that
/// touches stdin - and it refuses to start without a terminal, so a scripted run can never hang
/// waiting for a keypress.
/// </summary>
internal static class JoystickMappingFlow
{
    /// <summary>How long to wait between samples while a step is open for input.</summary>
    private static readonly TimeSpan CapturePollInterval = TimeSpan.FromMilliseconds(16);

    public static int Run(string? guidFilter, string? filePath, bool json)
    {
        var mappingPath = filePath ?? Sdl3MappingFile.DefaultPath;
        var scan = Sdl3JoystickCatalog.Scan();

        if (!scan.Initialized)
        {
            Console.Error.WriteLine($"SDL3 运行时不可用：{scan.InitError ?? "未知原因"}");
            return 1;
        }

        if (scan.Unmapped.Count == 0)
        {
            if (json)
            {
                JoystickFormat.PrintJson(scan);
            }
            else
            {
                Console.WriteLine("没有只能作普通 Joystick 枚举、缺少标准 Gamepad 映射的设备。");
                Console.WriteLine("（SDL 已能识别为标准 Gamepad 的设备不需要也不应该手写映射。）");
                JoystickFormat.PrintMappings(Sdl3MappingFile.Load(mappingPath), mappingPath);
            }

            return 0;
        }

        if (Console.IsInputRedirected)
        {
            // A capture needs someone at a keyboard; a redirected stdin can never answer. Say
            // what is on offer and stop, rather than blocking a CI run on a read that cannot
            // complete.
            Console.Error.WriteLine("map 流程需要在交互式终端中运行（当前标准输入已被重定向）。");
            Console.Error.WriteLine("可先用 joysticks 子命令查看待映射设备：");
            foreach (var joystick in scan.Unmapped)
            {
                Console.Error.WriteLine($"    {joystick.GuidString}  {joystick.Name}");
            }

            return 2;
        }

        var target = SelectDevice(scan, guidFilter);
        if (target is null)
        {
            return 2;
        }

        if (json)
        {
            Console.Error.WriteLine("map 是交互式流程，--json 只影响 joysticks 与 list 的输出。");
        }

        Console.WriteLine();
        Console.WriteLine($"开始为「{target.Name}」生成 SDL 标准 Gamepad 映射。");
        Console.WriteLine($"    GUID  {target.GuidString}");
        Console.WriteLine($"    路径  {target.Path}");
        Console.WriteLine($"    原始控件：按钮 {target.ButtonCount}，轴 {target.AxisCount}，方向帽 {target.HatCount}");
        Console.WriteLine();
        Console.WriteLine("每个项目按提示激活对应控件；输入 s 跳过（设备没有该项），q 取消整个流程。");
        Console.WriteLine("已激活过的控件不会重复捕获，可以放心逐个按下。");

        var opened = Sdl3JoystickCatalog.OpenJoystick(target.InstanceId);
        if (!opened.Opened || opened.Reader is null)
        {
            Console.Error.WriteLine($"无法打开该设备：{opened.Error ?? "未知原因"}");
            return 1;
        }

        using var reader = opened.Reader;

        var session = new Sdl3MappingCaptureSession();
        var outcome = Sdl3MappingCaptureRunner.Run(
            session,
            reader,
            new ConsoleCaptureInput(),
            CapturePollInterval);

        if (outcome.Cancelled)
        {
            Console.WriteLine();
            Console.WriteLine("已取消，未写入任何映射。");
            return 0;
        }

        var mapping = session.BuildMapping(target.GuidString, target.Name, scan.Platform);

        Console.WriteLine();
        Console.WriteLine("生成的映射：");
        Console.WriteLine($"    {mapping}");
        Console.WriteLine();

        ReportGaps(session);

        var entry = Sdl3MappingFile.TryBuild(target.GuidString, target.Name, mapping);
        if (entry is null)
        {
            var problems = Sdl3MappingFile.Validate(new Sdl3MappingEntry
            {
                GuidString = target.GuidString,
                DeviceName = target.Name,
                Mapping = mapping,
                UpdatedUtc = Sdl3MappingFile.Stamp(),
            });

            Console.Error.WriteLine("生成的映射不合法，未保存：");
            foreach (var problem in problems)
            {
                Console.Error.WriteLine($"    {problem}");
            }

            return 2;
        }

        // SDL is asked first: a mapping it would reject must not be persisted, because the file
        // is loaded on every start and a rejected entry there is a silent failure forever.
        var applied = Sdl3JoystickCatalog.ApplyMapping(entry.Mapping, entry.GuidString);
        if (!applied.Applied)
        {
            Console.Error.WriteLine($"SDL 拒绝该映射，未保存：{applied.Error ?? "未知原因"}");
            return 1;
        }

        Console.WriteLine($"保存到 {mappingPath}？[Y/n]");
        var answer = Console.ReadLine();
        if (string.Equals(answer?.Trim(), "n", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("未保存。");
            return 0;
        }

        var saved = Sdl3MappingFile.Save(mappingPath, entry);
        if (!saved.Saved)
        {
            Console.Error.WriteLine($"保存失败：{saved.Describe()}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("已保存。");
        if (applied.RegisteredMapping is not null)
        {
            Console.WriteLine($"SDL 已登记该 GUID 的映射：{applied.RegisteredMapping}");
        }
        else
        {
            Console.WriteLine("注意：SDL 未能在本次运行中登记该 GUID 的映射，重启后请用 joysticks 复核。");
        }

        Console.WriteLine("映射在 daemon 启动时通过 SDL_AddGamepadMapping 载入，重启常驻服务后生效；");
        Console.WriteLine("同一文件也会被 Sdl3Probe 载入，因此重启后用 list 即可看到该设备成为标准 Gamepad。");

        return 0;
    }

    /// <summary>
    /// Names exactly what the mapping does not cover. A skipped required control is a hole in
    /// the mapping, and the flow says so instead of presenting the result as a full standard
    /// layout.
    /// </summary>
    private static void ReportGaps(Sdl3MappingCaptureSession session)
    {
        var skippedRequired = session.UncoveredRequired;
        if (skippedRequired.Count > 0)
        {
            Console.WriteLine("以下必需项目没有被捕获，映射因此不完整：");
            foreach (var step in skippedRequired)
            {
                Console.WriteLine($"    - {step.Label}（{step.Key}）");
            }

            Console.WriteLine("这些输入在本版中不可用；设备不会被当成已有完整标准布局使用。");
        }

        var skippedOptional = session.Skipped.Where(step => !step.Required).ToList();
        if (skippedOptional.Count > 0)
        {
            Console.WriteLine("以下可选项目未捕获（设备未提供或已跳过）：");
            foreach (var step in skippedOptional)
            {
                Console.WriteLine($"    - {step.Label}（{step.Key}）");
            }
        }
    }

    /// <summary>
    /// Picks the device to map: the one named by <c>--guid</c>, or the one the user selects from
    /// the list. Returns null - after printing why - when the choice cannot be made.
    /// </summary>
    private static Sdl3JoystickInfo? SelectDevice(Sdl3JoystickScan scan, string? guidFilter)
    {
        if (!string.IsNullOrEmpty(guidFilter))
        {
            var match = scan.Unmapped.FirstOrDefault(joystick =>
                string.Equals(joystick.GuidString, guidFilter, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                Console.Error.WriteLine($"没有 GUID 为 {guidFilter} 的待映射设备。当前可选：");
                foreach (var joystick in scan.Unmapped)
                {
                    Console.Error.WriteLine($"    {joystick.GuidString}  {joystick.Name}");
                }

                return null;
            }

            return match;
        }

        JoystickFormat.PrintText(scan);

        while (true)
        {
            Console.Write("选择要映射的设备编号（q 退出）：");
            var line = Console.ReadLine();
            if (string.Equals(line?.Trim(), "q", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (int.TryParse(line, out var ordinal)
                && ordinal >= 1
                && ordinal <= scan.Unmapped.Count)
            {
                return scan.Unmapped[ordinal - 1];
            }

            Console.WriteLine($"请输入 1 到 {scan.Unmapped.Count} 之间的编号。");
        }
    }
}

/// <summary>
/// The console side of the capture input seam: a keypress when one is waiting, nothing when
/// there is not. Never blocks, so the runner keeps sampling the device between keystrokes.
/// </summary>
internal sealed class ConsoleCaptureInput : ISdl3MappingCaptureInput
{
    public char? TryReadCommand()
    {
        if (!Console.KeyAvailable)
        {
            return null;
        }

        return Console.ReadKey(intercept: true).KeyChar;
    }
}
