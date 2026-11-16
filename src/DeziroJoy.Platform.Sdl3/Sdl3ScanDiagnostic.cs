namespace DeziroJoy.Platform.Sdl3;

/// <summary>Why an SDL3 scan ended with the gamepad count it did.</summary>
public enum Sdl3ScanOutcome
{
    /// <summary>The SDL gamepad subsystem initialized and at least one gamepad is open.</summary>
    GamepadsFound,

    /// <summary>The gamepad subsystem would not initialize (missing native library, ...).</summary>
    SdlUnavailable,

    /// <summary>SDL is up, but no joystick is currently recognized as a standard gamepad.</summary>
    NoGamepads,

    /// <summary>SDL lists gamepads, but opening one of them failed.</summary>
    OpenFailed,
}

/// <summary>
/// What one SDL3 scan saw, with the actionable Chinese guidance that explains a zero-gamepad
/// result. The provider keeps the latest scan here, the daemon prints <see cref="Guidance"/>
/// in its startup summary, and the probe dumps the full record - all from the same source, so
/// the surfaces never disagree about what the user should do.
/// </summary>
public sealed record Sdl3ScanDiagnostic
{
    public required Sdl3ScanOutcome Outcome { get; init; }

    /// <summary>Runtime version of the linked SDL library, or null when it could not be read.</summary>
    public Sdl3RuntimeInfo? Runtime { get; init; }

    /// <summary>Why SDL would not initialize, when it would not; null otherwise.</summary>
    public string? InitError { get; init; }

    /// <summary>How many gamepads the provider currently holds open.</summary>
    public required int GamepadCount { get; init; }

    /// <summary>
    /// Per-device identity and capability records, in enumeration order. This is the S1
    /// evidence table: type, GUID, VID/PID, serial, path, instance id, player index,
    /// connection state, mapping, touchpads and sensors.
    /// </summary>
    public IReadOnlyList<Sdl3GamepadInfo> Devices { get; init; } = [];

    /// <summary>
    /// Devices whose identity falls back to the run-local instance id (no serial and no path).
    /// Their configs reattach within a run but not across restarts; the probe lists them so
    /// the tradeoff is visible instead of silent.
    /// </summary>
    public IReadOnlyList<uint> UnstableIdentityInstanceIds { get; init; } = [];

    /// <summary>
    /// Enumeration entries SDL reported as gamepads that could not be opened, with the
    /// instance id and SDL's own error text. A device here is one the daemon can never read.
    /// </summary>
    public IReadOnlyList<Sdl3OpenFailure> OpenFailures { get; init; } = [];

    /// <summary>
    /// How many persisted SDL mappings were handed to SDL before this scan enumerated. Zero
    /// means none were loaded - which is the normal state for an install that has never mapped
    /// a plain joystick - not that none were needed.
    /// </summary>
    public int AppliedMappingCount { get; init; }

    /// <summary>
    /// Why the persisted mapping file's mappings were not all applied, or null when they were:
    /// the file could not be read, or an entry in it describes a mapping SDL will not honour.
    /// A device mapped through the <c>map</c> flow that still does not appear as a gamepad is
    /// the signature of the first; a device that works except for one dead button is the
    /// signature of the second.
    /// </summary>
    public string? MappingLoadError { get; init; }

    /// <summary>
    /// Whether any persisted mapping is in force, so a surface can distinguish "no mapping file
    /// exists" from "one exists but was not applied".
    /// </summary>
    public bool HasAppliedMappings => AppliedMappingCount > 0;

    /// <summary>
    /// One-line hint sized for the GUI status area, which trims to a single line. Empty once
    /// a gamepad was found - there is nothing to guide the user towards.
    /// </summary>
    public string Hint => Outcome switch
    {
        Sdl3ScanOutcome.SdlUnavailable => "SDL3 运行时不可用，无法检测手柄",
        Sdl3ScanOutcome.NoGamepads => "SDL3 已初始化，但没有识别为标准 Gamepad 的手柄",
        Sdl3ScanOutcome.OpenFailed => $"SDL3 报告了 {GamepadCount} 个手柄，但有 {OpenFailures.Count} 个无法打开",
        _ => string.Empty,
    };

    /// <summary>
    /// The full, actionable guidance: what the daemon prints where a human will read it.
    /// Composes the outcome's own guidance with the open-failure and unstable-identity
    /// sections whenever either is non-empty, so a partial result never hides a device the
    /// user cannot use. Empty when every reported gamepad opened with a stable identity.
    /// </summary>
    public string Guidance
    {
        get
        {
            var sections = new List<string>();

            var outcomeGuidance = Outcome switch
            {
                Sdl3ScanOutcome.SdlUnavailable =>
                    $"SDL3 游戏手柄子系统初始化失败：{InitError ?? "未知错误"}。" +
                    "请确认应用分发了当前平台的原生 SDL3 运行时（Windows 为 SDL3.dll，Linux 为 libSDL3.so）。",
                Sdl3ScanOutcome.NoGamepads =>
                    "SDL3 已初始化，但没有任何设备被识别为标准 Gamepad。" +
                    "能以普通 Joystick 枚举、却没有可靠 SDL Gamepad 映射的设备需要先补充映射，" +
                    "不能当作已有标准布局使用。用 Sdl3Probe 的 map 流程逐键生成映射并保存到配置根目录的 " +
                    "sdl-mappings.json，随后重启常驻服务即可生效。",
                Sdl3ScanOutcome.OpenFailed =>
                    "SDL3 报告了标准 Gamepad 设备，但打开全部失败。",
                _ => string.Empty,
            };

            if (outcomeGuidance.Length > 0)
            {
                sections.Add(outcomeGuidance);
            }

            if (OpenFailures.Count > 0)
            {
                sections.Add(
                    "无法打开的标准 Gamepad 设备：" +
                    string.Join("；", OpenFailures.Select(failure => $"实例 #{failure.InstanceId}：{failure.Error}")));
            }

            if (UnstableIdentityInstanceIds.Count > 0)
            {
                var unstable = string.Join("、", UnstableIdentityInstanceIds.Select(id => $"#{id}"));
                sections.Add(
                    $"以下手柄既无序列号也无路径，身份回退为本次运行内的实例 ID（{unstable}）：" +
                    "同一型号的多只手柄在本运行内仍可区分，但重启后配置可能无法自动重新关联。");
            }

            if (MappingLoadError is not null)
            {
                sections.Add(
                    $"持久 SDL 映射文件（sdl-mappings.json）问题：{MappingLoadError}。" +
                    "本次运行只使用 SDL 内置映射库与文件中可用的部分，map 流程生成的映射没有完全生效。");
            }

            return string.Join('\n', sections);
        }
    }
}

/// <summary>
/// One enumeration entry SDL called a gamepad that <c>SDL_OpenGamepad</c> refused, with the
/// run-local <paramref name="InstanceId"/> and SDL's own <paramref name="Error"/> text.
/// </summary>
public sealed record Sdl3OpenFailure(uint InstanceId, string Error);
