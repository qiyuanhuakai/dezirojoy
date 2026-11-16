# SDL3 输入探针（Sdl3Probe）

独立的 SDL3 通用输入探针，不属于当前产品的默认输入路径，也不接入 daemon 的
XInput／evdev provider。它对应路线图 **S1**：在决定默认后端切换前，按真机核实 SDL3 的
部署、枚举、身份、映射、扩展能力与归一化输入语义，并作为 **S2 初始 provider**
（`src/DeziroJoy.Platform.Sdl3`）的独立验证工具。

探针不注入键鼠、不修改配置、不常驻。`list` 一次性枚举后即释放 SDL；`watch` 只读输入，
Ctrl+C 或 `--duration` 退出。

## 依赖

- `SDL3-CS` 3.4.20（托管绑定）+ `SDL3-CS.Linux`／`SDL3-CS.Windows`（原生运行时，
  覆盖 win-x64/arm64 与 linux-x64/arm64；版本集中在 `Directory.Packages.props`）。
- 通过 NuGet RID 原生资产部署，随应用 publish 只携带当前平台的原生库，与
  SkiaSharp 的机制一致。

## 用法

```bash
dotnet run --project tools/Sdl3Probe/Sdl3Probe.csproj -c Release -- [list|watch] [选项]
```

| 命令／选项 | 说明 |
|---|---|
| `list`（默认） | 一次性报告 SDL 运行时、每只手柄的身份与能力、旧后端对照与建议 |
| `watch` | 持续监听热插拔，并按引擎相同语义打印归一化输入（按钮、双摇杆、扳机） |
| `--json` | JSON 输出（`list` 为单个对象，`watch` 每轮一个对象） |
| `--interval=<ms>` | `watch` 刷新间隔，默认 100 |
| `--duration=<s>` | `watch` 运行秒数，到时自动退出（脚本化验证用） |

退出码：`0` 成功；`1` SDL3 运行时不可用；`2` 参数错误。

### watch 的状态刷新率诊断

`watch` 每 2 秒打印一行 `SDL 状态刷新 X 次/s（累计 N，枚举间隔 M ms）`。`X` 是 provider
实际调用 `SDL_UpdateGamepads` 的频率——引擎每个 tick 调用一次 `GetConnected`，因此健康
行为下 `X` 应等于 watch 的循环频率（默认 10/s）。若 `X` 停留在枚举间隔附近（默认 4/s），
说明状态刷新被扫描节流拖住，即“动画与操作发涩”的根因；`--json` 模式下每轮对象带
`stateRefreshCount`，可自行计算速率。状态刷新每 tick 一次、与设备数无关；设备枚举和完整
诊断仍按 `M` ms 节流。

## 报告内容（S1 证据）

- SDL 运行时版本与 revision、游戏手柄子系统初始化结果；
- 每设备：稳定设备 ID（GUID＋序列号＋路径的组合规则见 `Sdl3GamepadIdentity`）、
  SDL 类型、GUID、VID／PID、序列号、路径、运行内实例 ID、玩家索引、连接方式；
- 映射字符串与来源（`sdl-database`／`runtime-override`；SDL 不区分内置与用户映射库，
  探针不做更强声明）、面键标签（A/B/X/Y 或 叉/圈/方/三角）；
- 扩展能力计数：触摸板数量与每板触点、传感器类型与数据率（仅探测，不计入输入支持）；
- 旧后端对照：同一时刻 XInput／evdev 枚举到的设备，供迁移期重复设备识别取证；
- 零结果时给出可执行的分类建议（运行时缺失／无标准映射／打开失败）。

## 构建与运行

```bash
dotnet build tools/Sdl3Probe/Sdl3Probe.csproj -c Release
dotnet run --project tools/Sdl3Probe/Sdl3Probe.csproj -c Release
```

发布（含原生库）：

```bash
dotnet publish tools/Sdl3Probe/Sdl3Probe.csproj -c Release -r linux-x64
./bin/Release/net10.0/linux-x64/publish/Sdl3Probe
```

## 本机当前结果（2026-11-16，linux-x64，未接手柄）

| 项目 | 结果 |
|---|---|
| Release 构建 | 通过（0 警告） |
| 原生部署 | publish 输出含 `libSDL3.so`／`libSDL3.so.0`／`libSDL3.so.0.4.20`，独立运行成功 |
| SDL 运行时 | 3.4.20（revision `SDL-3.4.20-release-3.4.20`），`SDL_INIT_GAMEPAD` 初始化成功 |
| 设备枚举 | 0 个标准 Gamepad（本机无手柄），按 `NoGamepads` 输出补充映射建议，退出码 0 |
| 旧后端对照 | XInput 在 Linux 不可用；evdev 报告无 `/dev/input` 目录 |
| 真机输入、热插拔、同型号双设备、映射来源、触摸板／传感器 | **未做真机实测**，见路线图 S1 完成标准 |

自动化覆盖见 `tests/DeziroJoy.Platform.Tests/Sdl3/`：归一化、身份组合、热插拔、
生命周期与诊断均为无真机的确定性测试（53 项），其中包括“扫描节流期间每次
`GetConnected` 都刷新 SDL 状态、多设备 `Poll` 不重复刷新、移除事件在扫描间隙即时生效”
的回归测试。
