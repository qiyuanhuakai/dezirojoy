# 输入注入权限模型与常驻架构

本文记录三类已实证结论：**为何在部分"强制置顶"应用中映射失效**、**Avalonia 应用的内存构成**，
以及**由此定稿的常驻与注入架构**。三者共同指向同一个决定：把引擎与输入注入移入一个不加载
UI 栈的常驻进程，并由它作为唯一权威；GUI 只是它的一个客户端。

定稿架构见 [第 4 节](#4-定稿架构守护进程权威--命名管道-ipc)。第 1 至 3 节是根因与实测记录，
其中标"当时"的陈述是调查快照，现状以第 4 节与 [`../DESIGN.md`](../DESIGN.md) 为准。

---

## 1. 置顶应用中映射失效 —— 根因

### 1.1 现象

用户测试发现：打开部分"强制置顶"的应用后，手柄映射失效（鼠标位移与按键注入均无效果）。

### 1.2 根因：UIPI（用户界面特权隔离）

这不是缺陷，而是 Windows 的既定安全设计。

DeziroJoy 的 `src/DeziroJoy.App/app.manifest` 将 `<requestedExecutionLevel>` 声明为
`level="asInvoker"`，因此普通 GUI 以**中等完整性级别（medium integrity）**运行，未提权。

而 Microsoft 对 `SendInput` 的官方文档明确写道：

> This function is subject to UIPI. Applications are permitted to inject input only into
> applications that are at an equal or lesser integrity level.

> This function fails when it is blocked by UIPI. Note that neither GetLastError nor the
> return value will indicate the failure was caused by UIPI blocking.

即：**SendInput 只能注入到完整性级别相等或更低的应用；当被 UIPI 阻止时函数失败，
且错误码与返回值都不会指明失败源于 UIPI。**

Raymond Chen 补充了决定性机制：SendInput 位于输入栈最底层，**不接受目标窗口句柄**，
投递由窗口管理器依据**前台窗口**决定。因此 UIPI 的校验对象是前台窗口所属进程的完整性级别。

Microsoft 工程师 Jeffrey Tan 在 MSDN 论坛给出直接结论：

> elevated UI process runs under high IL level and the Vista UIPI feature protects the high IL
> process windows from receiving input (SendInput) from medium or low IL processes
> (non-elevated processes). If you want to use SendInput to control the input of an elevated
> process window, your calling process must also be elevated.

**结论**：用户遇到的"强制置顶应用"是**以管理员身份运行（high integrity）的进程**。
DeziroJoy 以中等完整性运行，其 SendInput 被 UIPI 静默丢弃。这与置顶本身无关——
置顶只影响 Z 序，不影响完整性级别；真正的原因是这些应用同时是提权进程。

### 1.3 代码侧的放大因素（调查当时）

`src/DeziroJoy.Platform.Windows/Output/SendInputOutputSink.cs:121` 的 `Send()` **当时完全丢弃了
SendInput 的返回值，且从不读取 GetLastError**（尽管 P/Invoke 声明了 `SetLastError = true`）：

```csharp
private static void Send(in INPUT input)
{
    Span<INPUT> buffer = [input];
    SendInputNative.SendInput(1, buffer, Marshal.SizeOf<INPUT>());
}
```

叠加官方文档"错误码不会表明是 UIPI 所致"这一点，失败表现为**完全无声**：
无异常、无日志、无返回码，映射只是"停止工作"。这与用户观察到的现象一致。

**此项已修复。** `SendInputOutputSink` 现在捕获 SendInput 的返回值与
`Marshal.GetLastWin32Error()`，并通过 `InjectionFailed` 事件、`LastInjectionFailed`、
`LastErrorCode`、`FailedInjectionCount` 以及常驻环形缓冲 `InjectionLog` 把结果暴露出来；
守护进程退出时打印 `injected: N events · M blocked`。失败不再无声。

但修复只解决了"看得见"，没有解决"归因"：被 UIPI 阻止时 Win32 **不设置**错误码，
所以计数器非零只证明"有注入被丢弃"，不能反推"这一次就是 UIPI"。边界见
[4.9 节](#49-sendinput-失败计数与-uipi-的边界)。

另有两项已确认的实现细节，影响修复方案：

- 鼠标位移使用 `MOUSEEVENTF_MOVE` **相对**位移（非绝对坐标），因此可**排除** DPI、
  多显示器、虚拟桌面坐标映射等因素；键使用 `WVk` + `WScan`（虚拟键 + 扫描码）。
- 组合键由 `src/DeziroJoy.Core/Platform/CommandDispatcher.cs:103-131` 拆成**多次独立 SendInput**
  （如 Ctrl+C 为 4 次调用）。若其中一次被丢弃，修饰键可能卡住；目前只有 `ReleaseAll()`
  能清理，且它仅在切换配置/暂停/停止时触发。

### 1.4 已验证的输入路径事实（根因调查当时的快照）

> 下表记录的是**调查当时（修复前）**的仓库状态，保留它是为了说明推理依据。
> 标 ✗ 的三行现已不成立，见 [1.6 节](#16-调查之后的现状修正)。

| 项 | 事实 |
|---|---|
| 唯一注入点 | `SendInputOutputSink.cs:121` |
| 鼠标 | `MOUSEEVENTF_MOVE` 相对位移，无 `ABSOLUTE`/`VIRTUALDESK` |
| 滚轮 | `MOUSEEVENTF_WHEEL` / `MOUSEEVENTF_HWHEEL`，单位 120 |
| 键盘 | 虚拟键 `WVk` + `MapVirtualKeyW` 扫描码；`KEYEVENTF_SCANCODE` 已定义但未使用 |
| 错误处理 | ✗ 当时：无。返回值丢弃，`GetLastError` 从不读取 |
| 提权感知 | ✗ 当时：全仓无 `requireAdministrator` / `runas` / `IsUserAnAdmin` / 完整性查询 |
| 前台窗口感知 | ✗ 当时：全仓无 `GetForegroundWindow` / `AttachThreadInput` / `SetForegroundWindow` |

### 1.5 修复方向

注入必须发生在**提权进程**中。可选路径：

1. **提权的用户会话代理进程**（推荐）——GUI 保持非提权，注入移入提权代理。
2. **UIAccess 应用**——`uiAccess="true"` + Authenticode 代码签名 + 安装于 `Program Files`，
   可使中等完整性进程绕过 UIPI。需要代码签名证书，作为备选记录。

**已定稿采用第 1 条**，定稿形态见第 4 节。第 2 条（UIAccess）**未实现，也不在计划中**：
它要求代码签名证书与受保护安装位置，代价远高于一个提权的用户会话代理；本文保留它只是
为了记录"为什么没有选它"。

### 1.6 调查之后的现状修正

调查当时得出的三条否定性结论已经过期，明确纠正如下，以免被当作现状引用：

| 当时的结论 | 现状 |
|---|---|
| "错误处理不存在" | 已存在。返回值与 `GetLastError` 均被捕获并计数，见 1.3 节 |
| "提权感知全仓没有" | 已存在。`WindowsProcessElevation.IsElevated` 查询当前进程令牌，`IElevationService` / `ElevationService` 把它暴露给 UI |
| "前台窗口感知全仓没有" | 已存在。`IForegroundSource` 读取当前前台窗口所属进程：Windows 用 `GetForegroundWindow` + `GetWindowThreadProcessId`（`WindowsForegroundSource`），Linux 用 X11（`X11ForegroundSource`）；`ForegroundWatcher` 轮询它并按 `ForegroundRule` 决定映射是否生效 |
| "守护进程不存在" | 已存在。`Program.cs --daemon` 走 `DaemonHost.Run()`，不加载任何 Avalonia 程序集；登录时计划任务由 `DaemonInstaller` 注册 |

仍未解决的一项：**组合键部分丢弃时强制释放已按下的修饰键**。`CommandDispatcher` 目前只在
`ReleaseAll()`（切配置 / 暂停 / 停止）时统一释放；单次组合键里若某几个事件被丢弃，
dispatcher 得不到反馈，因此无法只补发丢失的那几个。这需要一条从 sink 回到 dispatcher 的
失败通道，属于独立改动，不与本文定稿架构耦合。

---

## 2. 为什么常驻进程必须是"用户会话代理"而非 Windows 服务

一个自然的想法是把引擎做成 Windows 服务。**此路不通**：

Windows 自 Vista 起实施 **Session 0 隔离**——服务运行于 Session 0，与用户交互桌面
（Session 1+）隔离。Session 0 中的进程**无法**向用户桌面注入输入。

因此正确形态是：**在用户会话内运行的提权代理进程**，由它持有真实的
`SendInputOutputSink`。启动途径：

| 途径 | 说明 |
|---|---|
| 登录时计划任务（`schtasks /rl HIGHEST`） | 一次性授权，之后免每次 UAC 弹窗，**默认途径** |
| 按需 `ShellExecute` `runas` | 未安装计划任务时可用，**不要求先安装任务**；UAC 被拒绝时界面显示"不可用" |

GUI（非提权）通过**命名管道 IPC** 与代理通信。

> **已废弃的早期设想**：本文一度把改造点定在
> `src/DeziroJoy.App/Services/PlatformFactory.cs:39-67` 的 `TryCreateSink`，即"在 GUI 进程内
> 把本地 sink 换成一个 IPC sink"。该处方**不再有效**，定稿架构见第 4 节。
> `TryCreateSink` 至今仍只做平台选择（Windows → `SendInputOutputSink`，Linux → `UInputOutputSink`），
> GUI 不再通过它取得注入能力。

---

## 3. Avalonia 内存构成（实测）

Release 构建，Linux/Xvfb 环境，`/proc/<pid>/smaps` 采样：

| 层 | RSS |
|---|---|
| 裸 .NET 10 控制台（`Thread.Sleep`） | 28 MB |
| 最小 Avalonia 空窗（`FluentTheme` + Inter 字体） | 145 MB |
| DeziroJoy 完整应用 | 185 MB（4s/12s/24s 稳定，12 线程） |

即：**约 78% 是框架地板，应用自身增量约 40 MB**。

### 145 MB 地板的原生库构成

| 组件 | 大小 | 说明 |
|---|---|---|
| `libLLVM.so.20.1` | **130 MB** | 由 Mesa 的 **llvmpipe 软件光栅化器**引入 |
| `libicudata.so.74.2` | 29 MB | ICU 全球化数据 |
| `libgallium-*.so` | 24 + 15 MB | Mesa 软件光栅化 |
| `libSkiaSharp.so` | 6.7 MB | 实际渲染库 |
| `coreclr` + `clrjit` | 8.5 MB | .NET 运行时 |

`smaps_rollup`：Rss 145 MB、Anonymous 35 MB、Private_Dirty 48 MB、Pss 102 MB（大量共享）。

### 关键判断

**那 130 MB 的 LLVM 不是 Avalonia 的开销，而是本机无 GPU 驱动时 Mesa 退化为
llvmpipe 软件渲染所致。** Windows 上没有 Mesa/llvmpipe，Skia 走 D3D，
因此用户在 Windows 上看到的约 160 MB **不包含这 130 MB**，构成完全不同。

其余可优化项：

- **ICU 29 MB** 可通过 `<InvariantGlobalization>true</InvariantGlobalization>` 消除，
  代价是仅剩序数比较；对含中文的 UI 需先评估文化相关行为影响。
- .NET 运行时 28 MB 地板不可压缩。
- 应用自身 40 MB：程序集 + Inter 字体 + CommunityToolkit + 3 个 UserControl + 计时器 + DI 容器 + 轮询。

### 这如何与三进程架构互相印证

若引擎与注入移入**无窗口的常驻守护进程**，该进程永不加载 Avalonia/Skia/字体栈，
其 RSS 回落至 .NET 地板（约 30 MB）。托盘进程同样无窗口，只驻留图标与菜单。
渲染进程是可释放的呈现资源——关闭窗口即结束进程，其计时器、采样订阅与渲染
资源一并释放，UI 栈只在窗口存在时占用内存。

这直接回答了用户的方案选择：**"大部分时间不显示前端窗口"不仅改善体验，也消除内存顾虑。**

---

## 4. 定稿架构：守护进程权威 + 命名管道 IPC

### 4.1 权威归属

**提升的常驻守护进程是唯一权威。** 它持有 `IGamepadProvider`、`IOutputSink`、`EngineHost`
与注入互斥体，是唯一真正注入输入的进程。GUI 是客户端，只表达意图与呈现状态。

- 守护进程**自动启动**（登录时计划任务），不等 GUI 发起；配置来自持久化的
  `FileProfileStore`（Windows `%APPDATA%\DeziroJoy\profiles\`，Linux `~/.config/DeziroJoy/profiles/`），
  因此 GUI 从未打开过也能按既有配置映射。
- GUI 的 Start / Stop / Apply 都是**期望状态命令**，不是"让 GUI 自己开始注入"。
- **没有本地引擎回退，任何平台。** GUI 连不上守护进程时不会退而在自己进程内跑引擎——
  那正是 UIPI 静默丢弃注入的形态（Windows），也是渲染进程持有注入能力的形态（Linux）。
  渲染进程在启动时拉起守护进程（Windows 提权、Linux 同用户），托盘只在无窗口时段监督它。

### 4.2 渲染进程的组合入口

渲染进程（默认入口，由托盘在窗口关闭后按需拉起）**不共享 `EngineComposition`**：它只装配管道客户端、
状态镜像，以及一个**不注入**的呈现用宿主——`NoGamepadProvider` + `NullOutputSink` 的
`EngineHost`，存在性检测与可视化器高亮都走它，且从不启动。注入路径只存在于
守护进程内；`EngineComposition` 退居库/测试能力，生产组合根不再构造它。显式“劫持手柄按键”
开启且窗口输入在作用域内时，GUI 通过 `SetInjectionSuspended` 取得按连接划分的输入租约；
关闭劫持、失焦、离开映射页或关窗都会释放租约。编辑器是否打开不再隐式决定注入是否暂停。
浮窗屏幕键盘打开时同样取同一份租约（它与劫持租约取或，不互相顶掉）：键盘是手柄在驱动，
此时映射注入必须停下，而键盘自己要求输入的字符仍照常经 daemon 送出。渲染进程与托盘进程
各自持有一个键盘浮窗（托盘没有窗口，却正是键盘最该存在的形态），两者经各自的
`DaemonController` 镜像 daemon 的同一个开关，因此一套键盘、一份租约。

### 4.3 两个不同的内核对象

管道与互斥体是**两个独立对象：名字不同，用途不同，不可互相替代**。

| 对象 | 名字 | 作用 |
|---|---|---|
| 命名管道 | `DeziroJoy.Mapping` | GUI 与守护进程之间的控制通道 |
| 命名互斥体 | `Global\DeziroJoy.Injection` | 全机唯一注入者仲裁 |
| 命名互斥体 | `Global\DeziroJoy.Daemon` | 守护进程全生命周期实例仲裁 |

- 管道名是 `DeziroJoy.Mapping`；Windows 上的完整路径是 `\\.\pipe\DeziroJoy.Mapping`
  （C# 字面量 `@"\\.\pipe\DeziroJoy.Mapping"`）。
- 互斥体名保持 `Global\DeziroJoy.Injection` **不变**（`InjectionMutex.DefaultName`）。
  `Global\` 前缀使它跨会话全机可见，GUI 与提升的守护进程因此竞争同一个槽位。
- 管道**不承担**互斥语义，互斥体**不承担**通信语义。守护进程持有互斥体的时长，
  与它是否正在监听管道无关。
- **GUI↔tray 激活通道是第四条独立通道**，与上面的 daemon 命令管道 `DeziroJoy.Mapping`
  完全分离：它只做 GUI 单实例仲裁（二次启动 `Show`、交接角色探活），永不触碰 daemon。
  Windows 用含会话身份的知名命名管道（`CurrentUserOnly`）；Linux 因命名管道会被映射到一个
  会 `unlink` 的文件系统 socket、旧持有方退出会删掉继任方同一路径的 socket，改用**每实例唯一的
  抽象 Unix socket** + 一个随锁路径派生的 discovery 文件，从而旧持有方退出既不 unlink 继任方
  socket 也无 stale 堆积（抽象名随进程退出被内核回收）。详见 [`DESIGN.md`](../DESIGN.md)
  常驻架构一节的“激活管道的跨进程所有权（Linux）”。

### 4.4 协议版本 1

消息按类型携带必要字段：协议版本用于握手协商，请求 ID 用于变更配对，守护进程实例 ID
与单调递增序号用于识别快照来源和顺序。版本 1 的消息集：

| 消息 | 方向 | 负载 | 语义 |
|---|---|---|---|
| `Hello` | GUI → daemon | 客户端标识、协议版本、实例 ID、Windows 会话 ID、可选输入样本订阅 | 握手 |
| `HelloAck` | daemon → GUI | 实例 ID、协议版本、当前配置修订号 | 握手应答 |
| `GetState` | GUI → daemon | — | 拉取全量快照 |
| `ApplyConfiguration` | GUI → daemon | 期望配置修订号 + 完整 profile JSON | 提交配置变更 |
| `Start` | GUI → daemon | 期望配置修订号 | 请求开始映射 |
| `Stop` | GUI → daemon | — | 请求停止映射 |
| `SetMappingEnabled` | GUI → daemon | 请求 ID、启用状态 | 暂停或恢复分发，不停止引擎 |
| `SetInjectionSuspended` | GUI/tray → daemon | 请求 ID、挂起状态 | 显式劫持生效期间按连接暂停注入 |
| `SwitchPresetSlot` | GUI → daemon | 请求 ID、目标槽位（`DeviceId` 保留线上传输但服务端忽略） | 切换整体级活动预设槽 |
| `SetScreenKeyboard` | GUI → daemon | 请求 ID、开/关 | 开关浮窗屏幕键盘（引擎的屏幕键盘键在 daemon 内部驱动同一开关） |
| `InjectText` | GUI → daemon | 请求 ID、要输入的文本 | 把浮窗键盘的一个键帽经 daemon 自己的输出通道输入到当前聚焦应用 |
| `InjectKey` | GUI → daemon | 请求 ID、键、修饰键 | 点按一个键（退格 / Ctrl+A + Delete），同样经 daemon 的输出通道 |
| `ShutdownDaemon` | GUI/tray → daemon | 请求 ID | 释放输入并优雅退出常驻进程 |
| `MutationAck` | daemon → GUI | 请求 ID、结果、新修订号、新序号 | 变更已被受理 |
| `StateChanged` | daemon → GUI | 全量快照 | 状态推送 |
| `InputSample` | daemon → 已订阅 GUI | 实例 ID、独立样本序号、设备 ID、按键/摇杆/额外按钮 | 将真实物理输入送往导航、捕获与可视化 |
| `Error` | daemon → GUI | 请求 ID、错误码、可读信息 | 请求失败 |

一致性规则：

- **请求 ID**：每个请求唯一，应答原样带回，用于把响应配对到发起它的调用。
- **守护进程实例 ID**：每次守护进程启动生成一个。GUI 用它判断"我连的还是同一个守护进程吗"；
  实例 ID 变了说明守护进程重启过，GUI 必须重新握手，不能继续沿用旧会话状态。
- **单调序号**：守护进程侧每次状态变更递增。GUI 用它检测丢帧与乱序；发现缺号就补一次
  `GetState`，不猜测中间状态。
- **全量快照**：`StateChanged` 与 `GetState` 的应答都携带完整状态，不做增量。快照包含
  运行状态、活动配置修订号及其完整 `ProfileJson`、整体级活动预设槽、实际注入者身份和计数，
  以及每个真实连接设备的 `DeviceId`、`Name`、`Layout`。连接状态由这些描述呈现具体设备名和
  Xbox / DualShock / 通用布局，不再用“由常驻守护进程映射”代替设备身份。
- **心跳、重连与有序推送**：客户端周期 `GetState`，兼作心跳与权威状态刷新；断线后串行重连并
  重新握手，实例 ID 与序号在新会话重新建立基线。每个 daemon 连接只有一个写队列，响应和推送
  不会因并发线程不同而乱序。快照推送与权威应答在同一同步边界检查并采用，晚到应答不能覆盖更新快照。
  主动 `ShutdownDaemon` 成功后客户端不再自动拉起 daemon。
- **实时输入样本**：新 GUI 在 `Hello` 中显式订阅；旧客户端省略该可选字段，daemon 因而不向它发送
  新消息类型，协议仍保持版本 1。运行 tick 与停止后的 250ms 存在性采样都发布物理状态，重复状态不发送；
  每连接只合并同一设备且数字键状态相同的连续模拟量样本，设备之间以及所有按下/松开转换逐项保序，
  既不积压摇杆流量也不吞按键边沿。样本序号独立于快照序号，合并造成的跳号不代表丢配置；
  GUI 只采用当前 daemon 实例的递增样本，并用它驱动导航、捕获和可视化。导航与捕获边沿按 DeviceId
  分别维护，另一台设备的持续模拟量不会重置基准；断开、重连和离开输入作用域会清理对应状态。
- **设备变化及时推送**：daemon 的存在性监视器在映射停止时也继续扫描；连接或断开只推进快照序号，
  不伪造配置 revision。新设备首次出现时初始化四个彼此独立的默认槽，旧槽内容不被覆盖。若快照 profile 的
  设备已断开，权威 `ProfileJson` 改报首个剩余设备的当前槽；GUI 以 revision、实例和设备身份共同决定 rebase，
  编辑中草稿不被覆盖，取消草稿或提交时采用剩余设备，apply 不会向已断开设备报告成功。
- **期望配置修订号**：`ApplyConfiguration` 与 `Start` 都带 GUI 认为当前的修订号。守护进程
  发现不一致就拒绝该请求并回 `MutationAck(Accepted=false, ErrorCode="revision-mismatch")`，
  同时携带当前修订号；GUI 的编辑草稿固定其基线修订，冲突后必须重新读取权威 profile 或明确合并，
  **不得只更新修订号后提交旧草稿，也不自动重放**。
- **浮窗屏幕键盘由 daemon 持有开关，窗口只镜像**：引擎的 `ToggleScreenKeyboardCommand`
  与 Leader 键的 `SwitchPresetCommand` 一样不进入输出 sink，由
  `EngineHost.ScreenKeyboardToggleRequested` 在 tick 线程交给 daemon（`DaemonServer.RequestScreenKeyboardToggle`），
  daemon 翻转自己的开关并推送快照，快照的 `ScreenKeyboardOpen` 就是窗口显示/隐藏的唯一依据；
  窗口自己关闭（完成 / 返回 / B 键）时发 `SetScreenKeyboard` 把同一开关清掉，否则下一次引擎切换
  会把用户已经收起的键盘又打开。该开关是会话状态、不落 `DaemonState`，daemon 重启即为关闭。
  键盘的输出一律经 daemon：`InjectText` 走 `TextCommand`（文本通道，不按本地布局拆键），
  `InjectKey` 走一次修饰键-按键-释放的点按（退格、清空的 Ctrl+A 与 Delete）。两者都以"已连接设备数 > 0"
  为前提，没有可用输出通道时明确回 `Error`，不假装输入成功；也都**不受挂起租约门控**——为输入租约
  暂停注入不能顺便让用户正看着的键盘失声。长度以 Core 自己的文本上界为准，超出即拒。
- **整体级预设槽**：`SwitchPresetSlot` 切换的是整个应用唯一且持久化的活动槽标量，不属于任何设备，
  daemon 重启后恢复同一槽。所有可切换设备在同一宿主锁内重载；任一设备失败会回滚已经重载的设备，
  活动槽与 revision 保持原值，不能半切换。消息中的 `DeviceId` 保留在线上但服务端忽略，属于软兼容，
  协议版本不因此升级。Leader 键经 `EngineHost.PresetSwitchRequested` 由守护进程内部驱动同一标量，
  与 GUI 切换走同一条逻辑（tick 线程同步执行，与切换本身执行的重载相同）。GUI 保存与本地导入通过
  `PresetStore.SaveSlot` 写当前活动槽；daemon 模式从权威 `Devices`/`ProfileJson` 解析目标设备，不依赖本地 runtime。
  非空但损坏的 `ProfileJson` 禁止保存、导入和 apply，只有字段缺失时才回退到连接设备；daemon 导入由
  `ApplyConfiguration` 持久化当前槽，成功后不再二次写裸设备键。
- **完整 profile JSON，不是路径**：配置随消息传输序列化后的 profile 本体。传路径会让
  守护进程去读一个 GUI 有权限而它未必有权限的位置，也会让"到底改没改"无法从消息本身判断。
- **同一时刻只有一个未完成变更**：GUI 在收到上一条 `MutationAck` 之前，不发下一条
  `ApplyConfiguration` / `Start` / `Stop` / `SetMappingEnabled` / `SetInjectionSuspended`。
- **结果未知时对账，绝不重放**：请求超时或连接中断时，GUI 无法知道守护进程是否已经应用了
  这条变更。此时它重新连接、握手、`GetState`，按真实状态决定下一步。**永不重放**同一条
  变更——重放会把一次已经生效的变更应用两次。

### 4.5 命令语义

| 命令 | 守护进程行为 | 应答时机 |
|---|---|---|
| `Start` | 取得注入互斥体，启动引擎循环 | **在引擎与互斥体都就位之后**才回 ack |
| `Stop` | `ReleaseAll()` 释放所有按住的键与鼠标键，再释放互斥体，停止循环 | **在释放与互斥体释放都完成之后**才回 ack |
| `ApplyConfiguration` | 运行中**直接换 profile，不停引擎**；未运行则只更新待用配置 | 配置已生效后 |
| `SetMappingEnabled` | 暂停或恢复分发；引擎与互斥体保持运行 | 分发状态已改变后 |
| `SetInjectionSuspended` | 按连接持有显式劫持租约；多个客户端叠加，断开自动释放自己的租约 | 聚合挂起状态已改变后 |
| `SetScreenKeyboard` | 翻转 daemon 持有的浮窗键盘开关；幂等，重复同一状态不推送 | 开关已改变后 |
| `InjectText` / `InjectKey` | 经 daemon 自己的输出通道输入一个键帽；不持有任何输出、不受三个暂停门控 | 输入已发出后 |
| `ShutdownDaemon` | 停止映射、释放所有按键与互斥体，然后退出 daemon | **退出开始前**先写完 ack |
| GUI 关闭 / 断开 | 不停引擎、不释放注入互斥体，只断开管道并撤销该连接的劫持租约 | — |

- `Start` 的 ack 必须晚于互斥体获取。否则 GUI 会显示"已启动"，而注入槽位其实在另一个进程手里。
- `Stop` 之后**守护进程继续存活**：它只是不再注入。停止映射不等于退出守护进程；完整退出必须
  使用 `ShutdownDaemon`（客户端详细结果为 `ShutdownDaemonAsync`，布尔便捷入口为 `RequestShutdownAsync`）。
- `ApplyConfiguration` 运行中生效是核心场景：改灵敏度不该要求用户先停止映射。
- GUI 关闭只断开连接，守护进程按当前配置继续映射。这正是常驻架构的意义所在。

### 4.6 安全边界

- **同用户 ACL**：创建管道时只授予当前用户 SID 的 ReadWrite 与 CreateNewInstance
  （后者允许同一用户创建后续管道实例；Windows 会自动补 Synchronize），不用绑定完整性级别的
  `CurrentUserOnly`；同一用户的普通 GUI 与提权守护进程均可连接，其他用户不可写。
- **同会话验证**：任何控制消息之前必须完成握手，拒绝后立即断开；Windows 上通过
  `GetNamedPipeClientProcessId` 取得真实对端 PID/SessionId，不信任客户端自报的会话字段。
- **最小攻击面**：管道只暴露 4.4 节那 17 条映射控制消息，不提供任意代码执行、文件读写或
  进程启动能力。`ShutdownDaemon` 只能请求本 daemon 按既定清理路径退出，不接受命令行或任意 PID。

### 4.7 第二个守护进程

守护进程启动时先取得 `Global\DeziroJoy.Daemon` 实例互斥体，再检查管道；另一实例已存在时
**干净退出**：以 0 返回，不抢管道、不重复注入。注入互斥体只在映射运行时持有，不能承担
常驻实例仲裁。管道持续接受新连接，首个 GUI 不断开也允许第二个 GUI 握手和读取状态。

### 4.8 按需提权与 UAC 失败

- 未安装登录计划任务时，守护进程仍可**按需**通过 `runas` 启动
  （`WindowsProcessElevation.RelaunchElevated`），**不要求先安装计划任务**。
- UAC 被用户取消、或 shell 拒绝启动时，界面显示**"不可用"**并说明原因，不显示虚假的
  "已连接 / 已提权"。`DaemonSettingsViewModel.ElevationText` 的"无法读取其运行级别"分支
  同理：读不到运行级别时报告"未知"，绝不推断为"已提权"。

### 4.9 SendInput 失败计数与 UIPI 的边界

`SendInputOutputSink` 暴露 `LastInjectionFailed`、`LastErrorCode`、`FailedInjectionCount` 与
`InjectionFailed` 事件；`InjectionLog` 常驻记录每次注入的成败；守护进程退出时打印
`injected: N events · M blocked`。这些计数器**必须**在界面上呈现。

但两条边界必须同时说明，否则计数器会被当成 UIPI 探测器用：

1. **不能从每次错误反推 UIPI。** 官方文档明确：被 UIPI 阻止时 SendInput 失败，而
   "`GetLastError` 的返回值与函数返回值都不会指明失败源于 UIPI"。计数器非零只证明
   "有注入被丢弃"，不证明"这一次是 UIPI"。
2. **Linux 测试不能证明 UIPI。** UIPI 是 Windows 的完整性机制；Linux 上 uinput 路径的测试
   全绿，与 Windows 上的提权行为无关。`docs/QA-CHECKLIST.md` 第 10 节的管理员目标真机验证
   才是这项的唯一证据。

### 4.10 仍未完成

- **组合键部分丢弃时强制释放已按下的修饰键**（见 1.6 节）：需要一条从 sink 回到
  `CommandDispatcher` 的失败通道，与本文定稿架构不耦合。
- **`InvariantGlobalization` 取舍**：已记录于 `docs/MEMORY.md`，按内存之外的理由单独决定，
  不作为本架构的一部分。

---

## 5. Linux：同用户模型、systemd 用户单元与设备访问权

本节记录 Linux 侧与第 4 节**同形态但不同机制**的部分。核心事实：
**Linux 不存在第 1 至 3 节描述的 UIPI 问题，也不需要提权**——`uinput` 是内核级接口，
注入目标的高低权限与注入者无关，因此守护进程以**当前用户**身份运行（`DESIGN.md`
常驻架构一节同此结论）。X11 / Wayland 同样不是变量：evdev 读取与 uinput 写入都在内核里，
与显示服务器无关。

### 5.1 常驻形态：systemd 用户单元

| 项 | Windows | Linux |
|---|---|---|
| 注册处 | 登录计划任务（schtasks） | `~/.config/systemd/user/dezirojoy-daemon.service` |
| 运行身份 | 提权（`/RL HIGHEST`） | 当前用户 |
| 安装途径 | 设置卡片 / `--install-daemon`（UAC） | 设置卡片 / `--install-daemon`（无需提权） |
| 登录自启（GUI） | `DeziroJoy-Autostart` 计划任务 | `~/.config/autostart/DeziroJoy.desktop` |
| 退出登录后常驻 | 计划任务本就跨登录 | 需 `loginctl enable-linger $USER` |

单元文件内容（由 `LinuxDaemonService.BuildUnitFile` 生成，测试钉住）：

```ini
[Unit]
Description=DeziroJoy 手柄映射守护进程（systemd 用户单元）
After=default.target

[Service]
Type=simple
ExecStart="<安装的二进制>" --daemon
Restart=on-failure
RestartSec=2

[Install]
WantedBy=default.target
```

两个刻意的选择：

- **`Restart=on-failure`**：没有 GUI 在场观察崩溃，守护进程必须自己回来。
- **不依赖 `graphical-session.target`**：uinput 无头可用，把单元绑到图形登录会恰恰破坏
  最需要映射的 SSH / 控制台会话。登录自启用 `default.target`。

安装 = 写单元 + `daemon-reload` + `enable` + `start`；卸载 = `stop` + `disable` + 删文件
+ `daemon-reload`。GUI 登录自启动（XDG `.desktop`）与单元启用是**两个独立开关**，
与 Windows 的 daemon 任务 / autostart 任务分离一一对应。

**linger**：用户单元的 systemd 实例随登录会话结束而终止，因此"退出登录后继续常驻"需要
`loginctl enable-linger $USER`（非 root 执行时可能要求 polkit 认证，见 4.8 的同一约束）。
安装成功文案会带上这条命令；不配 linger 时，"登录时常驻"仍然成立。

### 5.2 设备访问权：真正的门槛

Linux 上映射失效几乎只有一个原因：**当前用户读不到 `/dev/input/event*` 或写不到
`/dev/uinput`**。两种修复，均为应用内 opt-in（安装器按
[`QA-CHECKLIST.md`](QA-CHECKLIST.md) 第 20 节不注册 udev 规则）：

```bash
# 其一：加入 input 组后重新登录（udev 默认把 event* 与 uinput 归属 input 组）
sudo usermod -aG input $USER

# 其二：udev 规则 /etc/udev/rules.d/99-dezirojoy.rules
KERNEL=="event*",GROUP="input",MODE="0660"
KERNEL=="uinput",GROUP="input",MODE="0660",OPTIONS+="static_node=uinput"
```

`uinput` 一行里的 `static_node=uinput` 不能省：内核模块不会自动创建该节点，缺失时
即使权限正确也会 ENOENT。

### 5.2.1 一键修复（应用内 opt-in）

上述两种修复可以在设置页「后台服务」卡片上一键完成（仅 Linux 显示「修复设备访问权限」
按钮）。它把"照抄命令"升级为显式动作，但**不改变 opt-in 原则**：安装器与包体仍然不注册
任何规则，helper 脚本随包放在 `/opt/dezirojoy` 里也只是待命，只有用户点击按钮才会运行。

流程（`DeviceAccessFixer`，测试以 mock 进程/探针覆盖各分支）：

1. 先探测，且**只按权限信号判定缺口**：`EvdevGamepadProvider.ProbeOnce()` 的拒读节点数
   （`DeniedNodeCount`，含"有手柄可读但有节点被拒"的逐节点形态）与 `/dev/uinput` 可写性
   （`UInputOutputSink.ProbeUnavailableReason()`）。两者皆无为 NoGap：直接告知"无需修复"，
   不提权、不跑任何进程。
2. **没有手柄不是缺口**：evdev 扫描的展示用 Hint（"未识别到手柄"/"没有 event 节点"/"没有
   输入子系统"）不是权限信号——udev 规则授予权限，不能凭空创建设备。权限正常但没插手柄时
   点按钮同样得到 NoGap，而不是白跑一次 pkexec。
3. 有缺口时把 `LinuxDeviceAccess.UdevRulesText`（规则文本的单一来源）写入临时文件，
   用 `pkexec` 运行随包 helper `tools/Packaging/linux/fix-device-access.sh`（装入
   `/opt/dezirojoy/fix-device-access.sh`）：安装上述规则 → `udevadm control --reload &&
   udevadm trigger` → `usermod -aG input <调用者>`。helper 自身不含规则副本，规则正文
   始终由应用生成，两处不会漂移。
4. 无 polkit 代理、pkexec 不在 PATH、对话框被取消、helper 缺失（非 deb 安装）或 helper
   因工具缺失/执行失败未能完成承诺步骤（非 0 退出）时降级：状态行回退为可照抄的命令
   （即 `DeviceAccessGuidance` 文案，stderr 原因在前），按钮 tooltip 说明本次降级的原因。
5. UI 明示修改内容：tooltip 写明将写入 `/etc/udev/rules.d` 并把当前用户加入 input 组；
   udev 规则重载后对后续 open 立即生效，而组变更需要重新登录。

边界：一键修复只解决设备访问权。权限修好但注入仍然无效时，原因在注入路径本身
（uinput ioctl 失败等），由 5.3 的 sink 错误常驻展示暴露——"提权了还是没反应"不该
再表现为无声的空注入。

### 5.3 诊断：空结果不再无声

`EvdevGamepadProvider` 现在**枚举 `/dev/input` 目录**而不是探测 `event0..31`（上限消失），
每次扫描产出 `EvdevScanDiagnostic`，把空结果分成五种（`GamepadsFound` /
`NoInputDirectory` / `NoEventNodes` / `AccessDenied` / `NoGamepad`），各带 errno、
路径与中文指引。三个消费方：

- **daemon 启动摘要**（`DaemonHost.BuildStartupSummary`）：`devices detected: 0` 之后
  追加一整行指引；systemd 用户单元运行为 `journalctl --user -u dezirojoy-daemon`。
- **GUI 状态区**（`MainWindowViewModel`）：渲染进程不持有设备（守护进程才是权威），
  因此通过 `LinuxRuntimeProbe` 做一次性探测——枚举 + 开/关节点，不开读线程、不抢设备——
  把"请连接手柄"换成真实原因（权限不足带 errno 与 usermod 命令 / 无节点 / 无输入子系统），
  并附 `uinput` 可写性检查。
- **设置面板「后台服务」卡片**：注入权限文案说明同用户设计，并引用同一段设备权限指引；
  卡片上的「修复设备访问权限」按钮是一键修复的入口（见 [5.2.1 节](#521-一键修复应用内-opt-in)）。

设备识别判据（`EvdevGamepadProvider.LooksLikeGamepad`，纯函数、正反例测试钉住）：
第二支摇杆（`ABS_RX`/`ABS_RY`）或任一显式手柄按键（`BTN_JOYSTICK`、面部键簇
`BTN_SOUTH/EAST/NORTH/WEST`、十字键 `BTN_DPAD_UP`、`BTN_TRIGGER_HAPPY`）判为手柄。
`ABS_X+ABS_Y` 单独**不**判——那正是触摸板（`ABS_X/ABS_Y + BTN_TOUCH`）与数位板的形状，
而街机摇杆没有右摇杆也仍带上述显式按键。此前"必须有 `ABS_RX`"的判据会静默漏掉街机摇杆类。

`uinput` 打不开时，`UInputOutputSink` 的异常消息与 `ProbeUnavailableReason()` 使用同一
段文案（errno + usermod + udev 规则），`EngineComposition.SinkError` 把它带进 daemon 摘要
——不再出现"降级为空注入而界面上什么都没有"的形态。

---

## 6. Linux 包格式：deb / rpm / AppImage（与 Flatpak 延期）

三种 Linux 格式打包**同一份自包含发布产物**：`/opt/dezirojoy` 载荷逐字节一致，
只是容器不同。三者都随包带 `dezirojoy.ico`、`fix-device-access.sh`（5.2.1 的一键
修复 helper，装入即待命、不自行注册任何规则）、desktop 入口与 hicolor 主题图标
（AppImage 例外，见下），且都不写 `/etc`、不附 scriptlet / 维护脚本——登录自启动
与常驻服务保持应用内 opt-in（与 5.1 的 daemon 模型一致）。

| 格式 | 构建 | 安装 | 依赖声明 | 图标载荷 |
|---|---|---|---|---|
| `.deb` | `dpkg-deb`（`tools/Packaging/linux/build-deb.sh`） | `sudo apt install ./…deb`（解析依赖，勿用 `dpkg -i`） | Depends 带版本化 `libicu` 备选项 | hicolor 七尺寸 |
| `.rpm` | `rpmbuild`（`tools/Packaging/linux/rpm/build-rpm.sh` + spec） | `sudo dnf install ./…rpm` | Requires 带 ICU soname 区间 | hicolor 七尺寸 |
| AppImage | `appimagetool`（`tools/Packaging/linux/appimage/build-appimage.sh`） | `chmod +x` 后直接运行，无需安装 | 无（运行时解析） | 根 PNG（`.DirIcon`） |

两点依赖细节，都是"runner 自带所以只能靠断言保证"的同一类缺口：

- **ICU 必须显式声明**。应用未启用 InvariantGlobalization，.NET 运行时**启动时
  dlopen** `libicuuc` / `libicui18n` 而非链接它们，因此 `dpkg` 的 shlibs 与
  `rpmbuild` 的 find-requires 都看不到这一依赖。deb 用版本化 `libicuXX` 备选项、
  rpm 用 soname 区间显式钉住；缺了它包能装上、却在最小系统上启动即 FailFast。
- **rpm 的 Version 禁 `-`**。`-` 是版本 / release 分隔符，故 `0.0.1-beta.5-preview`
  规整为 `0.0.1~beta.5~preview`（`~` 在 rpm 版本比较中低于任何字符，免费复现 semver
  预发布序）；发布资产名保留原始 semver、去掉 release 段。

AppImage 的图标只以根 PNG 承载（appimagetool 把它链为 `.DirIcon`），镜像内没有
hicolor 树——squashfs 里没有任何东西消费 `/usr/share/icons`，桌面集成装的是
`.DirIcon` 而非主题目录，故七尺寸主题帧只在 .deb / .rpm 上随包。AppImage 是
type-2 运行时，CI runner 无 FUSE，因此 appimagetool 与其自身都以
`--appimage-extract-and-run` / `--appimage-extract` 运行。

**Flatpak 本版不提供，理由与 5.1 / 5.2 直接相关**：注入依赖对 `/dev/uinput` 的写
权限（5.2），而 Flatpak 沙箱默认不授予该权限，且桌面门户（xdg-desktop-portal）目前
没有暴露 uinput 写入口——沙箱内注入在默认桌面上不可用。5.2.1 的「一键修复」
（pkexec 写 `/etc/udev/rules.d` + usermod）与 5.1 的 systemd 用户单元常驻模型在
沙箱内同样不成立（无法写 `/etc`、无法管理用户单元）。待 libeis / 输入门户成熟后
再评估接入；期间请用 deb / rpm / AppImage。安装与卸载的人工验收项见
[`QA-CHECKLIST.md`](QA-CHECKLIST.md) 第 20 节。
