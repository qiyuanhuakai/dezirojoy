# DeziroJoy 0.0.5-alpha.1 发布说明

> 本文是 0.0.5-alpha.1 发布说明的源稿。GitHub Release 的 notes 由
> `.github/workflows/release.yml` 生成，「本版提交」一节由工作流自动追加；本文保留相同
> 结构，便于发布门禁核对后原样落入发布页。

## DeziroJoy v0.0.5-alpha.1

本版是 SDL3 统一输入后端的第一个 alpha：交付 S1 的独立 SDL3 探针与最小 provider 骨架，
以及可**显式启用**的 S2 初始统一 provider。**默认输入后端没有改变**：Windows 仍为
XInput + SendInput（提权常驻 daemon 作为唯一注入权威），Linux 仍为 evdev + uinput
（systemd 用户单元常驻）。本版不做 S3 默认切换；SDL3 provider 未经真机验收，请按实验
功能使用。

### 本版更新

#### SDL3 输入后端（实验性，显式启用）

- **设置页「后台服务」卡片新增「输入后端」一节**：默认选项「平台默认（XInput / evdev）」，
  可显式切换到「SDL3 统一后端（实验性）」；点击「保存并重启常驻服务」后选择写入配置目录的
  `input-backend.json` 并随 daemon 安全重启生效（无运行中热切换）。卡片显示 daemon 快照
  报告的「运行中的后端」，保存后重启前提示“重启常驻服务后生效”，重启失败时保持原后端并
  说明原因，不会静默更换。随时切回平台默认即回退，映射、预设、临时层、前台规则与输出
  通道不变。
- **失败不静默回退**：显式选择 SDL3 但启动失败（如原生运行时缺失）时，daemon 不带
  provider 运行并在启动摘要报告 `input backend error`，GUI 快照显示真正在用的后端，
  不会悄悄改读旧后端。
- **初始统一 provider**：SDL3 标准按钮、双摇杆与扳机按 SDL 通用语义归一化到现有
  `IGamepadState`，直接复用预设、临时层、前台规则、手势和 DSL；同一通用读取路径面向
  SDL3 已识别的全部标准 Gamepad 类型，不按品牌分支，也不先把设备转成虚拟 Xbox 手柄。
- **稳定设备身份**：DeviceId 为 `sdl:{guid}[:{serial}][:{path}]`，实例 ID 只在两者皆无时
  兜底并在扫描诊断中标注不稳定；SDL gamepad 类型记录在身份中但不进 DeviceId，重分类
  不会孤立已存配置。旧 XInput／evdev 配置到该身份的自动迁移**未实现**。
- **诊断**：daemon 启动摘要报告实际输入后端与后端错误；SDL3 扫描诊断对“运行时不可用／
  没有标准 Gamepad／打不开”分别给出中文指引，GUI 状态区与快照同一来源。
- **SDL3 运行时随包分发**：托管绑定加每平台原生库（Windows `SDL3.dll`／Linux
  `libSDL3.so`）进入四个 RID 的发布包，打包脚本与发布工作流断言其在包内。
- **独立 SDL3 探针**（`tools/Sdl3Probe`，不属于产品构建）：`list` 一次性输出 SDL3 运行时
  与每台设备的类型、GUID、VID／PID、序列号、路径、运行内实例 ID、player index、连接状态、
  SDL 映射来源与触摸板／传感器计数，并对照旧后端（XInput／evdev）的枚举结果；
  `watch` 持续监听热插拔与归一化状态（按钮、双摇杆、扳机）；退出码 0／1（运行时不可用）／2。
- **测试边界**：SDL3 provider、探针、设置卡片与后端选择均有自动化测试，并包含在本版
  全量测试中（Linux 2764 项 0 失败）；真机设备矩阵（USB／蓝牙、同型号双设备、不同
  SDL Gamepad 类型、休眠恢复）**未验证**，未实测设备不宣称已支持。

### 已知限制与验收边界

- SDL3 provider 是显式实验选项：默认后端与 0.0.2 一致，未经真机验收；覆盖声明按 SDL
  版本和能力记录，不以少数型号成功推导为所有设备已实测。
- **旧配置身份自动迁移未实现**：XInput／evdev 配置保持原样；切换后端后同一手柄可能因
  设备身份不同被视为新设备而使用默认配置，切回默认后端后原配置完好。
- U1（通用控件面板）、U2（输入显示策略）、S3（默认切换）未开始；触摸板／体感等 SDL3
  扩展输入（S4）不在本版范围。
- 后端切换需要重启（daemon 启动时读取 `input-backend.json`），没有运行中热切换。
- daemon 已报告实际输入后端、后端启动错误与 SDL3 扫描诊断（运行时不可用／没有标准
  Gamepad／打不开）；逐设备的完整“以何种类型、GUID、映射打开”证据以探针输出为准，
  provider 侧可诊断覆盖的验收未完成。Guide／Share 在 SDL3 下的可用性取决于设备与映射，
  尚未实测。
- 应用内自动更新按语义版本选取最新发布（含预发布）：0.0.2 正式版用户检查更新会看到
  本 alpha；不想升级 alpha 请在设置页关闭自动检查／自动下载，或到发布页手动选择版本。
  `0.0.2-beta.3` 及更早版本的应用内自动更新存在缺陷，从那些版本升级请手动下载。

### 升级与回退

- 升级不改变配置位置（Windows `%APPDATA%\DeziroJoy\`、Linux `~/.config/DeziroJoy/`），
  不改写旧配置；不显式启用 SDL3 时行为与 0.0.2 一致。
- 回退：设置里切回平台默认后端，或重新安装 0.0.2；两种方式都保留配置。
- SDL3 使用独立的 `sdl:` 前缀设备身份，旧配置自动迁移未实现：启用后同一手柄可能被
  视为新设备而使用默认配置，切回平台默认后端后原配置完好。
- 完整说明见 [SDL3 使用与回退说明](https://github.com/qiyuanhuakai/dezirojoy/blob/v0.0.5-alpha.1/docs/SDL3-BACKEND.md)。

### 下载

| 文件 | 平台 | 说明 |
| --- | --- | --- |
| `DeziroJoy-0.0.5-alpha.1-win-x64-setup.exe` | Windows | 自包含 per-user 安装包，Intel / AMD 64 位 |
| `DeziroJoy-0.0.5-alpha.1-win-arm64-setup.exe` | 同上，ARM64 |
| `DeziroJoy_0.0.5-alpha.1_amd64.deb` | Linux | 自包含 deb，Intel / AMD 64 位 |
| `DeziroJoy_0.0.5-alpha.1_arm64.deb` | 同上，ARM64 |
| `DeziroJoy-0.0.5-alpha.1-x86_64.rpm` | Linux | 自包含 rpm，Intel / AMD 64 位 |
| `DeziroJoy-0.0.5-alpha.1-aarch64.rpm` | 同上，ARM64 |
| `DeziroJoy-0.0.5-alpha.1-x86_64.AppImage` | Linux | 便携 AppImage，Intel / AMD 64 位 |
| `DeziroJoy-0.0.5-alpha.1-aarch64.AppImage` | 同上，ARM64 |
| `*.sha256` | — | SHA256 校验和 |

安装器不注册自启动、计划任务或 udev 规则；卸载不删除用户配置。未做代码签名，Windows
首次运行可能出现 SmartScreen 提示。

### 质量门禁

- Release 构建 0 警告 0 错误；全量测试在 Linux 通过：**2764 项，2754 通过、0 失败、
  10 项 Windows 专属跳过**（SDL3 provider、探针、设置卡片与后端选择的自动化测试均已
  包含在内），`dotnet test` 一条命令可复跑。SDL3 探针在本机实际运行通过：SDL3 3.4.20
  初始化成功，设备枚举与旧后端对照输出正常（本机无手柄，退出码 0）。
- SDL3 provider 与探针的新增功能有自动化测试；本文所列真机矩阵、Wayland 桌面与
  Windows 提权场景**未做人工验收**——自动化通过不等于这些硬件场景已经验证。
- 四 RID 打包、Windows Inno 安装／卸载与 Linux deb／rpm／AppImage 的构建和包体验证由
  Release workflow 执行。
- 人工验收清单：[QA-CHECKLIST](https://github.com/qiyuanhuakai/dezirojoy/blob/v0.0.5-alpha.1/docs/QA-CHECKLIST.md)；SDL3 使用与回退：
  [SDL3-BACKEND](https://github.com/qiyuanhuakai/dezirojoy/blob/v0.0.5-alpha.1/docs/SDL3-BACKEND.md)；阶段进度：
  [ROADMAP](https://github.com/qiyuanhuakai/dezirojoy/blob/v0.0.5-alpha.1/ROADMAP.md)。

### 本版提交

（由 release.yml 在生成发布 notes 时自动追加 `git log`。）
