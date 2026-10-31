# 输入注入权限模型与常驻架构

本文记录两类已实证结论：**为何在部分"强制置顶"应用中映射失效**，以及 **Avalonia 应用的内存构成**。
两者共同指向同一个架构决定：把引擎与输入注入移入一个不加载 UI 栈的常驻进程。

---

## 1. 置顶应用中映射失效 —— 根因

### 1.1 现象

用户测试发现：打开部分"强制置顶"的应用后，手柄映射失效（鼠标位移与按键注入均无效果）。

### 1.2 根因：UIPI（用户界面特权隔离）

这不是缺陷，而是 Windows 的既定安全设计。

DeziroJoy 的 `src/DeziroJoy.App/app.manifest` **未声明 `<requestedExecutionLevel>`**，
因此进程以默认的 `asInvoker` 启动，即**中等完整性级别（medium integrity）**，未提权。

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

### 1.3 代码侧的放大因素

`src/DeziroJoy.Platform.Windows/Output/SendInputOutputSink.cs:121` 的 `Send()` **完全丢弃了
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

另有两项已确认的实现细节，影响修复方案：

- 鼠标位移使用 `MOUSEEVENTF_MOVE` **相对**位移（非绝对坐标），因此可**排除** DPI、
  多显示器、虚拟桌面坐标映射等因素；键使用 `WVk` + `WScan`（虚拟键 + 扫描码）。
- 组合键由 `src/DeziroJoy.Core/Platform/CommandDispatcher.cs:103-131` 拆成**多次独立 SendInput**
  （如 Ctrl+C 为 4 次调用）。若其中一次被丢弃，修饰键可能卡住；目前只有 `ReleaseAll()`
  能清理，且它仅在切换配置/暂停/停止时触发。

### 1.4 已验证的输入路径事实

| 项 | 事实 |
|---|---|
| 唯一注入点 | `SendInputOutputSink.cs:121` |
| 鼠标 | `MOUSEEVENTF_MOVE` 相对位移，无 `ABSOLUTE`/`VIRTUALDESK` |
| 滚轮 | `MOUSEEVENTF_WHEEL` / `MOUSEEVENTF_HWHEEL`，单位 120 |
| 键盘 | 虚拟键 `WVk` + `MapVirtualKeyW` 扫描码；`KEYEVENTF_SCANCODE` 已定义但未使用 |
| 错误处理 | 无。返回值丢弃，`GetLastError` 从不读取 |
| 提权感知 | 全仓无 `requireAdministrator` / `runas` / `IsUserAnAdmin` / 完整性查询 |
| 前台窗口感知 | 全仓无 `GetForegroundWindow` / `AttachThreadInput` / `SetForegroundWindow` |

### 1.5 修复方向

注入必须发生在**提权进程**中。可选路径：

1. **提权的用户会话代理进程**（推荐）——GUI 保持非提权，注入移入提权代理。
2. **UIAccess 应用**——`uiAccess="true"` + Authenticode 代码签名 + 安装于 `Program Files`，
   可使中等完整性进程绕过 UIPI。需要代码签名证书，作为备选记录。

---

## 2. 为什么常驻进程必须是"用户会话代理"而非 Windows 服务

一个自然的想法是把引擎做成 Windows 服务。**此路不通**：

Windows 自 Vista 起实施 **Session 0 隔离**——服务运行于 Session 0，与用户交互桌面
（Session 1+）隔离。Session 0 中的进程**无法**向用户桌面注入输入。

因此正确形态是：**在用户会话内运行的提权代理进程**，由它持有真实的
`SendInputOutputSink`。启动途径：

| 途径 | 说明 |
|---|---|
| 登录时计划任务（`schtasks /rl HIGHEST`） | 一次性授权，之后免每次 UAC 弹窗，推荐 |
| GUI 触发一次性 UAC 提权 | 实现最简单，但每次启动都弹窗 |
| 运行时 `ShellExecute` `runas` | 按需提权 |

GUI（非提权）通过**命名管道 IPC** 与代理通信；GUI 侧提供 `IOutputSink` 的 IPC 实现。
`src/DeziroJoy.App/Services/PlatformFactory.cs:39-67` 的 `TryCreateSink` 是唯一构造点，
改造集中于此。

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

### 这如何与常驻架构互相印证

若引擎与注入移入**无窗口的常驻进程**，该进程永不加载 Avalonia/Skia/字体栈，
其 RSS 回落至 .NET 地板（约 30 MB）。GUI 仅在从托盘打开时加载 UI 栈，
隐藏即可退出——145 MB 的 UI 栈在大部分时间不存在。

这直接回答了用户的方案选择：**"大部分时间不显示前端窗口"不仅改善体验，也消除内存顾虑。**

---

## 4. 待办

- [ ] `SendInputOutputSink.cs:121` 增加返回值与 `GetLastError` 检查，使失败不再无声
- [ ] 组合键部分丢弃时强制释放已按下的修饰键
- [ ] 提权用户会话代理 + 命名管道 IPC（`PlatformFactory.TryCreateSink` 为唯一构造点）
- [ ] 登录时计划任务 `/rl HIGHEST` 的安装入口与 UI
- [ ] `--daemon` 无窗口 CLI 模式（当前 `Program.cs` 为纯 Avalonia 桌面生命周期，无参数解析）
- [ ] 评估 `InvariantGlobalization` 对中文 UI 的影响
