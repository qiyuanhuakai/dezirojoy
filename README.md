# DeziroJoy

现代化手柄映射工具（Windows 优先，Linux 实验性支持）。使用 C# + Avalonia 构建，
参考 AntiMicroX 的核心映射模型，提供更现代的鼠标速度控制与实时校准体验。

## 功能

- **手柄 → 鼠标 / 键盘映射**：任意按键可映射为单键、组合键（如 Ctrl+C）、鼠标按键；
  摇杆可映射为鼠标移动或滚轮滚动。
- **不同手柄的默认配置**：按设备自动识别布局（Xbox / DualShock / 通用），
  内置默认配置：
  - 左摇杆 → 滚轮（垂直 / 水平）
  - 右摇杆 → 鼠标移动
  - 十字键 → 方向键 ↑ ↓ ← →
  - B → Enter，A → Esc，X → Ctrl+C，Y → Ctrl+V
- **现代化鼠标速度控制**（相比 AntiMicroX）：
  - 速度以**物理单位**（像素/秒 @ 满幅）表示，与鼠标 DPI、显示器分辨率解耦；
  - 径向死区 + 响应曲线预设（线性 / 精准 / 激进 / 灵敏）；
  - 指数平滑时间常数：高速度与低抖动兼得，帧率无关；
  - 亚像素累积：慢速微调不丢步进；
  - 精准模式：按住左摇杆临时降速（默认 35%）；
  - 界面内**实时校准预览**：脚本化摇杆扫动直接驱动真实引擎，所见即所得。
- **按设备持久化配置**：每个手柄独立保存，重连自动恢复。
- 现代化深色界面：实时手柄状态可视化、映射表格编辑、灵敏度曲线图。

## 平台支持

| 平台 | 输入 | 输出 | 状态 |
|---|---|---|---|
| Windows 10/11 | XInput | SendInput | 主要目标 |
| Linux（X11 / Wayland） | evdev (`/dev/input`) | uinput (`/dev/uinput`) | 实验性 |

Linux 实验性说明：

- 读取 `/dev/input/event*` 需要用户在 `input` 组，或配置 udev 规则；
- 写入 `/dev/uinput` 需要权限，例如（root 执行一次）：

  ```sh
  echo 'KERNEL=="uinput", MODE="0660", GROUP="input", OPTIONS+="static_node=uinput"' \
    | tee /etc/udev/rules.d/99-uinput.rules
  udevadm control --reload && udevadm trigger
  ```

- 无权限时应用仍可正常启动，仅映射输出不可用（界面与预览不受影响）。

## 构建与运行

要求 .NET 10 SDK。

```sh
dotnet build
dotnet run --project src/DeziroJoy.App
dotnet test
```

## 测试策略（TDD）

- **Core（202 个测试）**：输入模型归一化、映射模型与 JSON 序列化、
  确定性映射引擎（死区 / 曲线 / 平滑 / 精准模式 / 亚像素累积）、内置默认配置。
- **Platform（113 个测试）**：XInput 位掩码→逻辑按键、Win32 结构体布局特征、
  evdev 事件解码、Windows / Linux 键码映射表全覆盖。
- **App（818 个测试）**：配置文件存储、引擎宿主（假手柄 / 记录型输出、
  含真实后台线程冒烟）、全部 ViewModel（灵敏度校准面板驱动真实引擎）、
  常驻守护进程 IPC 协议与快照仲裁。
- **GUI（headless）**：Avalonia.Headless + xUnit v3，覆盖窗口结构、
  控件点击交互、滑块 / 下拉框双向绑定、可视化器按键高亮与断开遮罩、
  编辑器子控件按目标类型显隐、实时预览推进。

合计 1133 个测试，`dotnet test` 一条命令全绿。

引擎为纯函数式设计（显式时间步进、无时钟、无平台调用），
因此全部映射语义都可在无手柄环境下确定性测试。

## 架构

```
src/
  DeziroJoy.Core/             # 领域内核：输入模型、映射模型、确定性引擎、默认配置库
  DeziroJoy.Platform.Windows/ # XInput 读取 + SendInput 输出（P/Invoke）
  DeziroJoy.Platform.Linux/   # evdev 读取 + uinput 输出（实验性）
  DeziroJoy.App/              # Avalonia UI（MVVM）+ 引擎宿主 + 配置存储
tests/
  DeziroJoy.Core.Tests/
  DeziroJoy.Platform.Tests/
  DeziroJoy.App.Tests/        # 含 headless GUI 测试
```

数据流：`平台读取 → IGamepadState → MappingEngine.Tick(state, dt) → InputCommand
→ CommandDispatcher → IOutputSink`。

## 配置位置

- Windows：`%APPDATA%\DeziroJoy\profiles\`
- Linux：`~/.config/DeziroJoy/profiles/`

## 许可

MIT
