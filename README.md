# DeziroJoy

把游戏手柄变成顺手的鼠标与键盘。现代化手柄映射工具，**Windows 优先，Linux 实验性支持**，
C# + .NET 10 + Avalonia 构建。参考 AntiMicroX 的核心映射模型，提供更现代的鼠标速度控制、
实时校准、预设 / 临时层、前台应用规则与完整的配置分享能力。

> 手柄优先：所有配置都可以**只用手柄**完成；映射启动后界面可完全隐藏、常驻后台，
> 不打断你正在操作的游戏或应用。

## 目录

- [功能特性](#功能特性)
- [平台支持](#平台支持)
- [安装](#安装)
- [快速上手](#快速上手)
- [使用指南](#使用指南)
- [配置文件](#配置文件)
- [常驻架构](#常驻架构)
- [构建与运行](#构建与运行)
- [测试](#测试)
- [文档](#文档)
- [许可](#许可)

## 功能特性

### 映射

- **任意来源 → 任意目标**：按键 / 扳机 / 十字键 / 摇杆 → 单键、组合键（如 Ctrl+C）、
  鼠标按键、鼠标移动、滚轮、特殊键、短文本、复杂快捷键程序。
- **手势**：单击 / 双击 / 长按三档，可为双击、长按分别指定动作；支持单击自动重复、
  长按重复。未配置手势的按键零延迟触发。
- **复杂快捷键 DSL**：一段小脚本描述动作序列，触发方式（按下 / 松开 / 长按 / 双击）、
  执行策略（执行一次 / 按住期间 / 再按切换）、重复策略（重启 / 忽略 / 排队）相互解耦；
  指令含 `tap / hold / release / wait / every / click / text`，界面与脚本共用同一动作模型，
  可预览实际按下 / 松开顺序。
- **媒体键与短文本**：音量 / 静音 / 播放暂停 / 上一首下一首、常用系统 / 浏览器功能键；
  「文本输出」目标直接注入一段短 Unicode（搜索框、文件重命名、短网址），配手柄可导航的
  屏幕键盘。

### 鼠标速度控制（相比 AntiMicroX 的核心改进）

- 速度以**物理单位**（像素/秒 @ 满幅）表示，与鼠标 DPI、显示器分辨率解耦。
  注意：输出为相对位移，系统指针速度设置（Windows「提高指针精确度」、Linux 指针加速度）
  会在其后再缩放，因此 px/s 不保证等于每种系统设置下的实际屏幕速度。
- 径向死区 + 响应曲线（线性 / 精准 / 激进 / 灵敏），曲线强度限定 `[0,1]`；
  指数平滑（高速度与低抖动兼得、帧率无关）、亚像素累积（慢速微调不丢步）。
- **精准模式**：按住精准键临时降速（默认 35%）。
- **真实手柄校准**：保留演示模式，另有真实输入模式，显示原始摇杆 / 死区过滤后 / 最终速度
  三级读数，采集松手漂移分布并给出可确认的死区建议，日常移动与精准模式分别调。

### 预设、特殊键与临时层

- **多预设**：每设备多个独立预设槽，可增删；整体级活动槽随切换持久化。
- **精准键 / Leader 键**：设备级共享。Leader 键可选「切换预设」（按下 / 按住）或
  「切换临时层」。
- **临时层**：默认一个临时层，按住（或按下切换）Leader 进入；只覆盖填写的几个按钮，
  未覆盖映射继承基础层，相同持续动作跨层保持（拖拽不中断），松开 / 断连可靠释放。
- **命令提示浮层**：按住层 Leader 时在屏幕边缘显示当前层按键的可读动作名（如「X：复制」），
  复用实时手柄图，不夺取目标应用键盘焦点。

### 前台应用规则

- 按前台程序自动选择预设，无匹配回退桌面默认；可手动锁定某个预设并显示锁定状态。
- 指定程序使用**原生手柄输入**时暂停键鼠映射（游戏直接读手柄）。
- 规则带优先级并解释当前配置为何生效；全屏可作辅助条件且可覆盖；可直接从当前打开的
  窗口选择进程名。常驻守护进程独立轮询，关窗到托盘后仍生效。

### 配置分享与更新

- **完整方案包**：导入 / 导出一整套方案（基础配置 + 临时层 + 全局特殊键 + 动作名称 +
  版本 + 使用说明），导入前预览替换内容、原子保存、可撤销；内置 Windows 模板。
- **内置自动更新**：设置页检查更新、opt-in 自动下载、sha256 校验、一次点击安装。
- **按设备持久化**：每个手柄独立保存，重连自动恢复；配置解析容错（未知映射保留、可删）。

### 界面与常驻

- 手柄模型即编辑器：点击模型上的键就地配置，实时高亮；浅色 / 深色主题。
- 托盘常驻：关窗 / 最小化进托盘、映射继续；守护进程作为唯一注入权威（见[常驻架构](#常驻架构)）。

## 平台支持

| 平台 | 输入 | 输出 | 状态 |
|---|---|---|---|
| Windows 10/11 | XInput | SendInput（提权守护进程） | 主要目标 |
| Linux（X11 / Wayland） | evdev (`/dev/input`) | uinput (`/dev/uinput`) | 实验性 |

Linux 实验性说明：

- 读取 `/dev/input/event*` 需要用户在 `input` 组，或配置 udev 规则；应用内提供
  「一键修复设备访问权限」。
- 写入 `/dev/uinput` 需要权限，例如（root 执行一次）：

  ```sh
  echo 'KERNEL=="uinput", MODE="0660", GROUP="input", OPTIONS+="static_node=uinput"' \
    | tee /etc/udev/rules.d/99-uinput.rules
  udevadm control --reload && udevadm trigger
  ```

- 无权限时应用仍可正常启动，仅映射输出不可用（界面与预览不受影响），且会明确报告。
- 前台规则在 Linux 仅 X11 尽力而为，Wayland 暂不支持（回退桌面默认）。

## 安装

发布页提供 **8 个自包含安装包**（内含 .NET 运行时，无需单独安装），各附 `.sha256` 校验和：

| 文件 | 平台 |
| --- | --- |
| `DeziroJoy-<version>-win-x64-setup.exe` | Windows，Intel / AMD 64 位 |
| `DeziroJoy-<version>-win-arm64-setup.exe` | Windows，ARM64（如骁龙笔记本） |
| `DeziroJoy_<version>_amd64.deb` | Linux（Debian / Ubuntu 系），Intel / AMD 64 位 |
| `DeziroJoy_<version>_arm64.deb` | Linux，ARM64（如树莓派） |
| `DeziroJoy-<version>-x86_64.rpm` | Linux（Fedora / RHEL / openSUSE），Intel / AMD 64 位 |
| `DeziroJoy-<version>-aarch64.rpm` | Linux，ARM64 |
| `DeziroJoy-<version>-x86_64.AppImage` | Linux 便携，Intel / AMD 64 位 |
| `DeziroJoy-<version>-aarch64.AppImage` | Linux 便携，ARM64 |

> **升级提示**：`0.0.2-beta.3` 及更早版本的应用内自动更新存在缺陷，从这些版本升级请
> 到发布页**手动下载**安装 `0.0.2-beta.4` 或更新版本；自 beta.4 起自动更新恢复正常。

### Windows

双击 `setup.exe` 按向导完成。安装器为简体中文（可切换 English），per-user 模式：默认装到
`%LOCALAPPDATA%\Programs\DeziroJoy`，无需管理员权限，也不注册开机自启动——登录自启动与
提权常驻服务请在应用内「设置」按需开启（见 [docs/ELEVATION-AND-DAEMON.md](docs/ELEVATION-AND-DAEMON.md)）。
卸载通过「设置 → 应用」或安装目录的 `unins000.exe`；卸载不删除 `%APPDATA%\DeziroJoy` 的配置。
未做代码签名，首次运行若弹 SmartScreen，选「更多信息 → 仍要运行」。

### Linux

```sh
# Debian / Ubuntu（用 apt 而非 dpkg -i，依赖一并解析）
sudo apt install ./DeziroJoy_<version>_amd64.deb

# Fedora / RHEL / openSUSE
sudo dnf install ./DeziroJoy-<version>-x86_64.rpm

# 便携 AppImage
chmod +x DeziroJoy-<version>-x86_64.AppImage && ./DeziroJoy-<version>-x86_64.AppImage
```

deb / rpm 装入 `/opt/dezirojoy` 并注册应用菜单入口；三种 Linux 格式载荷一致。卸载
（`sudo dpkg -r dezirojoy` / `sudo dnf remove dezirojoy` / 直接删除 AppImage）均保留
`~/.config/DeziroJoy/` 的配置。运行还需 `evdev` / `uinput`（见[平台支持](#平台支持)）。
暂不提供 Flatpak（沙箱不授予 uinput 写权限）。

## 快速上手

1. **安装并启动**，连接手柄。左侧「连接」卡片显示设备与布局，中间手柄模型实时反映按键 / 摇杆。
2. **启动映射**：默认配置即可用（右摇杆 = 鼠标，左摇杆 = 滚轮，十字键 = 方向键，
   B=Enter、A=Esc、X=Ctrl+C、Y=Ctrl+V）。
3. **改一个映射**：点击手柄模型上的键（或右侧映射行）→ 就地打开编辑器 → 选目标 → 确定。
   也可以直接按键盘录入目标，或从分类列表选。全程可用手柄操作。
4. **调鼠标手感**：在灵敏度卡片调最高速度 / 死区 / 曲线 / 平滑，实时预览；需要时用
   「真实输入校准」采集你这只手柄的漂移并采纳死区建议。
5. **隐藏界面**：关窗或最小化进托盘，映射继续；需要时从托盘「显示主窗口」。

## 使用指南

### 手势与复杂快捷键

- 在编辑器的「单击 / 双击 / 长按」页签分别配置目标；未配置的手势不产生等待。
- 复杂快捷键放在**长按动作**（也可配到其它触发），用 DSL 书写，例如
  `hold Alt` + `every 1s tap Tab`（按住期间持续按住 Alt、每秒点按 Tab）。
  用 `trigger / run / repeat` 头行解耦触发与执行；非法脚本会被拦截并给出行 / 列原因。

### 预设、Leader 与临时层

- 顶部预设条切换 / 新增 / 删除预设槽；每个预设独立。
- 把一个键设为 **Leader 键**：选「切换预设」在两个槽间切换，或选「切换临时层」。
- **临时层**：把几个映射的「所属层」设为临时层，按住层 Leader 时它们覆盖基础层，
  其余按键照常；松开即恢复。适合「按住 RB 临时用一套快捷键」。
- 按住层 Leader 时屏幕边缘出现命令提示浮层，告诉你这一层每个键做什么。

### 前台应用规则

- 在「前台规则」面板添加规则：选进程名（可从当前窗口选）、选动作（用某预设 / 原生手柄）、
  设优先级。面板会解释当前为何用这个配置。
- 例：浏览器 / 编辑器用办公预设，游戏用原生手柄（暂停键鼠映射），其余回退桌面默认。

### 导入 / 导出方案

- 「设置 → 导出方案」把整套预设 + 特殊键 + 临时层 + 说明存为一个文件；
  「导入方案」预览将替换的内容后原子应用，可撤销。旧的单配置文件也能作为单预设方案导入。
- 想快速开始，可「导入 Windows 内置模板」。

### 精准模式与校准

- 把一个键（默认左摇杆按下）设为精准键，按住时鼠标降速，便于精细瞄准。
- 灵敏度面板切到「真实输入校准」，松手采集漂移，采纳建议死区；日常 / 精准分别调。

## 配置文件

| 文件 | 内容 | 位置 |
|---|---|---|
| 预设配置（profile） | 单槽映射表 | `%APPDATA%\DeziroJoy\profiles\`（Win）/ `~/.config/DeziroJoy/profiles/`（Linux） |
| 方案包（scheme） | 整套预设 + 特殊键 + 临时层 + 说明 | 任意位置，经「导入方案」导入 |
| 前台规则 | 按程序切换 / 暂停 | `%APPDATA%\DeziroJoy\foreground-rules.json` / `~/.config/DeziroJoy/foreground-rules.json` |

均为人类可读 JSON（枚举用名称、多态对象带 `$type`）。**想让 AI 助手帮你写 / 改配置**，
本仓库内置了技能 [`dezirojoy-config`](.kimi-code/skills/dezirojoy-config/SKILL.md)：
完整 schema、枚举取值、引擎校验规则与可直接套用的示例都在其中。

## 常驻架构

三个进程各司其职（Windows / Linux 同形态）：

```
渲染进程（GUI，默认入口）── 关窗/最小化 ──▶ 托盘进程（无窗口，监督 daemon）
        │                                          │ 显示主窗口
        ▼                                          ▼
   daemon 进程（常驻，唯一注入权威）──▶ 注入输入（SendInput / uinput）
        └──▶ 命名管道 IPC（GUI 只表达意图、呈现状态）
```

- **守护进程是唯一权威**：持有 provider / sink / 引擎与注入互斥体。Windows 上提权
  （绕过 UIPI），Linux 上以当前用户经 systemd 用户单元常驻。
- **GUI 无本地引擎回退**：连不上 daemon 只进入连接错误状态，不在自己进程里注入。
- 前台规则由 daemon 独立轮询，关窗到托盘后仍生效。详见
  [docs/ELEVATION-AND-DAEMON.md](docs/ELEVATION-AND-DAEMON.md)。

## 构建与运行

要求 .NET 10 SDK。

```sh
dotnet build
dotnet run --project src/DeziroJoy.App
dotnet test
```

```
src/
  DeziroJoy.Core/             # 领域内核：输入模型、映射模型、确定性引擎、默认配置库
  DeziroJoy.Platform.Windows/ # XInput 读取 + SendInput 输出 + 前台检测（P/Invoke）
  DeziroJoy.Platform.Linux/   # evdev 读取 + uinput 输出 + X11 前台检测（实验性）
  DeziroJoy.App/              # Avalonia UI（MVVM）+ 引擎宿主 + 配置存储 + daemon
tests/
  DeziroJoy.Core.Tests/  DeziroJoy.Platform.Tests/  DeziroJoy.App.Tests/  # 含 headless GUI
```

数据流：`平台读取 → IGamepadState → MappingEngine.Tick(state, dt) → InputCommand
→ CommandDispatcher（输出所有权）→ IOutputSink`。

## 测试

引擎为纯函数式设计（显式时间步进、无时钟、无平台调用），全部映射语义可在无手柄环境下
确定性测试。合计 **2400+ 测试**，`dotnet test` 一条命令全绿：

- **Core**：输入归一化、映射模型与 JSON 序列化 / 容错、确定性引擎（死区 / 曲线 / 平滑 /
  精准 / 亚像素 / 手势 / 临时层 / 输出所有权 / 采样间断）、DSL、前台规则、配置迁移。
- **Platform**：XInput 位掩码→逻辑按键、Win32 结构体布局、evdev 解码、键码映射表、
  文本注入、前台检测。
- **App**：配置 / 预设 / 方案存储、引擎宿主、全部 ViewModel、daemon IPC 与快照仲裁、
  自动更新编排。
- **GUI（headless）**：Avalonia.Headless + xUnit v3，窗口结构、控件交互、双向绑定、
  可视化器、编辑器显隐、实时预览。

## 文档

- [DESIGN.md](DESIGN.md) — 界面设计系统、交互模型与常驻架构基准。
- [docs/ELEVATION-AND-DAEMON.md](docs/ELEVATION-AND-DAEMON.md) — 提权、daemon 与 IPC 安全边界。
- [docs/QA-CHECKLIST.md](docs/QA-CHECKLIST.md) — Windows 真机人工验收清单。
- [docs/MEMORY.md](docs/MEMORY.md) — 内存占用实测。
- [.kimi-code/skills/dezirojoy-config/](.kimi-code/skills/dezirojoy-config/SKILL.md) — 配置文件编写技能。

## 许可

MIT