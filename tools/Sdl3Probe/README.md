# SDL3 输入探针（Sdl3Probe）

独立的 SDL3 通用输入探针，不属于当前产品的默认输入路径，也不接入 daemon 的
XInput／evdev provider。它对应路线图 **S1**：在决定默认后端切换前，按真机核实 SDL3 的
部署、枚举、身份、映射、扩展能力与归一化输入语义，并作为 **S2 初始 provider**
（`src/DeziroJoy.Platform.Sdl3`）的独立验证工具。

探针不注入键鼠、不常驻。`list` 一次性枚举后即释放 SDL；`watch` 只读输入，Ctrl+C 或
`--duration` 退出；`joysticks` 列出只能作普通 Joystick 枚举、缺少标准 Gamepad 映射的设备；
`map` 是唯一会写配置的子命令——它为其中一台设备交互式生成标准 SDL 映射，并原子保存到
配置根目录的 `sdl-mappings.json`（与 `input-backend.json` 同处 profiles 目录之外）。

## 依赖

- `SDL3-CS` 3.4.20（托管绑定）+ `SDL3-CS.Linux`／`SDL3-CS.Windows`（原生运行时，
  覆盖 win-x64/arm64 与 linux-x64/arm64；版本集中在 `Directory.Packages.props`）。
- 通过 NuGet RID 原生资产部署，随应用 publish 只携带当前平台的原生库，与
  SkiaSharp 的机制一致。

## 用法

```bash
dotnet run --project tools/Sdl3Probe/Sdl3Probe.csproj -c Release -- [list|watch|joysticks|map] [选项]
```

| 命令／选项 | 说明 |
|---|---|
| `list`（默认） | 一次性报告 SDL 运行时、每只手柄的身份与能力、旧后端对照与建议 |
| `watch` | 持续监听热插拔，并按引擎相同语义打印归一化输入（按钮、双摇杆、扳机） |
| `joysticks` | 只列出只能作普通 Joystick 枚举、无标准 Gamepad 映射的设备，并打印当前映射文件 |
| `map` | 为其中一台设备交互式逐键生成标准 SDL 映射，保存到配置根目录 `sdl-mappings.json` |
| `--json` | JSON 输出（`list`／`joysticks` 为单个对象，`watch` 每轮一个对象） |
| `--interval=<ms>` | `watch` 刷新间隔，默认 100 |
| `--duration=<s>` | `watch` 运行秒数，到时自动退出（脚本化验证用） |
| `--guid=<guid>` | `map`：直接映射该 GUID 的设备，跳过设备选择 |
| `--file=<path>` | `map`：映射文件路径，默认为配置根目录的 `sdl-mappings.json` |

退出码：`0` 成功；`1` SDL3 运行时不可用（或映射被 SDL 拒绝、写入失败）；`2` 参数错误，
或 `map` 需要交互式终端而标准输入已被重定向。

`map` 在标准输入被重定向（CI、管道）时**不会阻塞**：它打印待映射设备的 GUID 与名称后退出，
退出码 2。没有待映射设备时直接说明并退出，退出码 0。

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

## map：为普通 Joystick 补充标准映射

SDL 能识别为标准 Gamepad 的设备走同一条通用读取路径；只能作普通 Joystick 枚举、没有可靠
SDL Gamepad 映射的设备不在支持声明内，需要先用本流程补一份映射。路线图明确不维护型号
白名单、不猜未知布局，因此这里做的是**逐键取证**，而不是按型号套模板。

```bash
# 1. 看哪些设备缺映射（非交互，可脚本化）
dotnet run --project tools/Sdl3Probe -- joysticks
dotnet run --project tools/Sdl3Probe -- joysticks --json

# 2. 对其中一台逐键生成映射
dotnet run --project tools/Sdl3Probe -- map
dotnet run --project tools/Sdl3Probe -- map --guid=03000000790000000006000000000000

# 3. 重启常驻服务后确认该设备已成为标准 Gamepad
dotnet run --project tools/Sdl3Probe -- list
```

流程要点：

- 依次提示 21 个必需项（面键四枚、十字键四向、start/back/guide、左右肩键、左右摇杆点击、
  左右摇杆的 X/Y、左右扳机）与 6 个可选项（截屏／分享键、背键 1–4、触摸板）。每项按提示
  激活对应控件；`s` 跳过（设备没有该项），`q` 取消整个流程。
- 每个项目开始时会取一次基线快照，已经按住／推住的控件不会重复捕获，可以放心逐个按下。
- 采样频率约 60 Hz，每次采样前先泵一次 SDL：`SDL_GetJoystickButton`／`Axis`／`Hat` 读的是
  驱动上次写入的缓存状态，不泵就永远读到设备刚打开时的值，这一条有对应回归测试。
- 扳机与"用轴表示的方向键"按 SDL 的解析规则写成半轴 `+aN`／`-aN`；摇杆轴写成 `aN`，只有
  在轴向与提示方向相反时才加 `~` 反转。这些符号规则由 `Sdl3MappingActivation.TokenFor`
  统一生成，并有对应单元测试。
- **映射键用 SDL 自己的拼写，不用枚举名。** 面键在映射字符串里是 `a`／`b`／`x`／`y`
  （SDL 的枚举叫 South/East/West/North，但映射键不是它）；背键 `paddle1` 对应
  `RightPaddle1`、`paddle2` 对应 `LeftPaddle1`，编号不按物理左右。SDL 的解析器按表格精确
  匹配，**认不出的元素会被静默丢弃、不报错**——写成 `south:b0` 的映射能载入、能生效，
  但面键完全没有绑定。因此键表集中在 `Sdl3ControlNames`（与
  `SDL_GetGamepadStringForButton`／`Axis` 逐项对齐，有对应该原生函数的校验测试），
  `Sdl3MappingFile.Validate` 也会把 `south:`／`east:` 这类位置名作为非法映射点名，而不是
  等用户发现某个键没有反应。
- 生成后先本地校验、再交给 `SDL_AddGamepadMapping`：被拒绝的映射**不会**写入文件。
- 保存前会明确列出跳过了哪些项；跳过的必需项会在结尾再次点名，映射因此是不完整但诚实的
  描述，不会把设备伪装成已有完整标准布局。
- 同一 GUID 重复映射是**更新**而非追加：文件按 GUID 排序、同 GUID 只保留最后一条。
- 写入是原子的（临时文件 + 替换），daemon 与探针都读到旧整体或新整体。

## sdl-mappings.json

配置根目录（Windows `%APPDATA%\DeziroJoy\`、Linux `~/.config/DeziroJoy/`）下的
`sdl-mappings.json`，与 `input-backend.json` 同处 profiles 目录之外：

```json
{
  "version": 1,
  "mappings": [
    {
      "guid": "03000000790000000006000000000000",
      "name": "Generic USB Joystick",
      "mapping": "03000000790000000006000000000000,Generic USB Joystick,platform:Linux,a:b0,...",
      "updatedUtc": "2026-11-16T00:00:00.0000000Z"
    }
  ]
}
```

- **provider 在枚举前载入**：`Sdl3GamepadProvider` 在 SDL 初始化成功后、第一次
  `SDL_GetGamepads` 之前，逐条调用 `SDL_AddGamepadMapping`。SDL 在构建游戏手柄列表时决定
  一个 Joystick 是不是标准 Gamepad，映射加晚了要等到下次枚举才生效。
- **daemon 与探针读同一文件**：App 通过 `Sdl3Backend` 用同一个
  `Sdl3MappingFileSource` 构造 provider；探针的 `list`／`watch` 同样载入。
- **重启后生效，没有热切换**：daemon 只在启动时读一次。这也是 SDL 自身的行为——按实例 ID
  缓存的"是不是 Gamepad"结论在进程生命周期内不会因新增映射而失效。
- 文件损坏或不可读时**宁可不映射也不覆盖**：扫描诊断会给出
  `持久 SDL 映射文件（sdl-mappings.json）读取失败：…`，本次运行只使用 SDL 内置映射库。
- `platform:` 记录映射是在哪个平台的驱动下取证的；SDL 的 `SDL_AddGamepadMapping` 不校验它，
  它只是证据，不是限制。

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
| `joysticks` | 0 个待映射 Joystick，映射文件 0 条，退出码 0 |
| `map`（stdin 重定向） | 打印"需要交互式终端"与可选设备后退出，不阻塞，退出码 2 |
| `map`（无待映射设备） | 说明没有待映射设备后退出，退出码 0 |
| 旧后端对照 | XInput 在 Linux 不可用；evdev 报告无 `/dev/input` 目录 |
| 真机输入、热插拔、同型号双设备、映射来源、触摸板／传感器 | **未做真机实测**，见路线图 S1 完成标准 |

自动化覆盖见 `tests/DeziroJoy.Platform.Tests/Sdl3/`：归一化、身份组合、热插拔、
生命周期与诊断均为无真机的确定性测试（53 项），其中包括“扫描节流期间每次
`GetConnected` 都刷新 SDL 状态、多设备 `Poll` 不重复刷新、移除事件在扫描间隙即时生效”
的回归测试。
