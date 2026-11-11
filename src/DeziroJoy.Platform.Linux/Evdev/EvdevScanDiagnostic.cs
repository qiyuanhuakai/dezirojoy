namespace DeziroJoy.Platform.Linux.Evdev;

/// <summary>Why an evdev scan ended with the gamepad count it did.</summary>
public enum EvdevScanOutcome
{
    /// <summary>At least one node opened and passed the capability check.</summary>
    GamepadsFound,

    /// <summary><c>/dev/input</c> itself is missing: this environment has no input subsystem.</summary>
    NoInputDirectory,

    /// <summary><c>/dev/input</c> exists but holds no <c>event*</c> node at all.</summary>
    NoEventNodes,

    /// <summary>Event nodes exist, but opening them was denied (EACCES): group/udev permissions.</summary>
    AccessDenied,

    /// <summary>Nodes opened fine, but none reported gamepad capabilities.</summary>
    NoGamepad,
}

/// <summary>
/// What one evdev scan saw, with the actionable Chinese guidance that explains a zero-gamepad
/// result. The daemon prints <see cref="Guidance"/> in its startup summary, the settings card
/// quotes <see cref="LinuxDeviceAccess.DeviceAccessGuidance"/>, and the GUI status area shows
/// the one-line <see cref="Hint"/> - all three come from here so the surfaces never disagree
/// about what the user should do.
/// </summary>
public sealed record EvdevScanDiagnostic
{
    public required EvdevScanOutcome Outcome { get; init; }

    /// <summary>How many <c>/dev/input/event*</c> nodes the scan saw.</summary>
    public required int NodeCount { get; init; }

    /// <summary>How many of those nodes this user may not open (EACCES).</summary>
    public required int DeniedNodeCount { get; init; }

    /// <summary>How many nodes passed the gamepad capability check.</summary>
    public required int GamepadCount { get; init; }

    /// <summary>The first node whose open() was denied, when any was.</summary>
    public string? FirstDeniedPath { get; init; }

    /// <summary>The errno of that first denied open(), when any was (13 == EACCES on Linux).</summary>
    public int? Errno { get; init; }

    /// <summary>
    /// The one place the outcome rules live: gamepads win, then a missing directory, then an
    /// empty one, then a permission wall, and only then "nothing that looks like a pad".
    /// </summary>
    public static EvdevScanOutcome ClassifyOutcome(
        bool directoryExists,
        int nodeCount,
        int deniedCount,
        int gamepadCount)
    {
        if (gamepadCount > 0)
        {
            return EvdevScanOutcome.GamepadsFound;
        }

        if (!directoryExists)
        {
            return EvdevScanOutcome.NoInputDirectory;
        }

        if (nodeCount == 0)
        {
            return EvdevScanOutcome.NoEventNodes;
        }

        if (deniedCount > 0)
        {
            return EvdevScanOutcome.AccessDenied;
        }

        return EvdevScanOutcome.NoGamepad;
    }

    /// <summary>
    /// One-line hint sized for the GUI status area, which trims to a single line. Empty once a
    /// gamepad was found - there is nothing to guide the user towards.
    /// </summary>
    public string Hint => Outcome switch
    {
        EvdevScanOutcome.NoInputDirectory => "未找到 /dev/input 目录，当前环境无法检测手柄",
        EvdevScanOutcome.NoEventNodes => "未找到手柄节点，请检查连接与蓝牙配对",
        EvdevScanOutcome.AccessDenied => $"权限不足（errno {Errno}）：{LinuxDeviceAccess.InputGroupCommand} 后重新登录",
        EvdevScanOutcome.NoGamepad => $"已扫描 {NodeCount} 个节点，未识别到手柄",
        _ => string.Empty,
    };

    /// <summary>
    /// The full, actionable guidance: what the daemon prints where a human will read it.
    /// Empty once a gamepad was found.
    /// </summary>
    public string Guidance => Outcome switch
    {
        EvdevScanOutcome.NoInputDirectory =>
            "未找到 /dev/input 目录：当前环境没有输入子系统（容器或最小化安装），手柄无法被检测到。",
        EvdevScanOutcome.NoEventNodes =>
            "/dev/input 下没有 event 节点：请确认手柄已连接（蓝牙手柄需重新配对），或更换 USB 口与线材后重试。",
        EvdevScanOutcome.AccessDenied =>
            $"无法读取手柄节点 {FirstDeniedPath}（errno {Errno}，权限不足）：请执行 {LinuxDeviceAccess.InputGroupCommand} 后重新登录，" +
            $"或添加 udev 规则 {LinuxDeviceAccess.UdevRulesPath}：{LinuxDeviceAccess.UdevRulesText}",
        EvdevScanOutcome.NoGamepad =>
            $"已扫描 {NodeCount} 个 event 节点，但没有识别到手柄（鼠标、键盘、触摸板不会被当作手柄）。",
        _ => string.Empty,
    };
}
