# DeziroJoy 设计文档

> 本文描述 DeziroJoy 的界面设计系统、交互模型与常驻架构。它是实现与评审的共同基准。
> 对应技术背景见 [`docs/ELEVATION-AND-DAEMON.md`](ELEVATION-AND-DAEMON.md)。

---

## 1. 定位与原则

DeziroJoy 是一个**手柄 → 鼠标/键盘**映射工具。它的使用场景决定了一切设计取舍：

| 场景特征 | 设计推论 |
|---|---|
| 用户手握手柄，不便操作鼠标 | 所有功能必须可**仅用手柄**完成配置 |
| 映射启动后前台是游戏/全屏应用 | 配置界面必须**可完全隐藏**，常驻后台 |
| 目标是提权进程（见权限文档） | 注入必须发生在**提权代理**中 |
| 需长时间挂机 | 前端窗口大部分时间**不存在**，而非最小化 |

由此得到四条原则：

1. **手柄优先** —— 任何交互都不能假设用户能方便地使用鼠标或键盘。
2. **静默常驻** —— 默认状态是后台运行；打开界面是一次显式动作。
3. **一次一决策** —— 每个界面元素只问一个问题，避免嵌套选择。
4. **可解释** —— 每个可调参数都必须说明它改变了什么。

---

## 2. 设计词元（Design Tokens）

Avalonia 应用的"设计系统"等价物是**主题资源字典 + 词元命名**。全部词元集中于
`src/DeziroJoy.App/Themes/Palette.axaml`，以 `ThemeDictionaries` 提供 Light / Dark /
Default 三套取值。

### 2.1 规则

- **必须用 `{DynamicResource TokenName}` 引用**。`{StaticResource}` 遇到 ThemeDictionaries
  键会在运行时抛异常。
- **切换只能设在 `Application.RequestedThemeVariant`**。设在 Window 上是 local 优先级，
  会把该窗口钉死，不再随应用切换。
- **视图层不允许出现硬编码颜色**。所有颜色必须来自词元，否则主题切换会漏出。

### 2.2 词元分组

| 组 | 词元 | 用途 |
|---|---|---|
| 表面 | `AppBackgroundBrush` | 应用底色 |
| 表面 | `CardBrush` / `CardBorderBrush` | 卡片与其边框 |
| 表面 | `SubtleBrush` | 次要表面、分隔 |
| 表面 | `SunkenBrush` | 内嵌预览区等下沉表面 |
| 表面 | `PanelBrush` / `TitleBarBrush` | 右侧面板 / 标题栏 |
| 文本 | `TextPrimaryBrush` / `TextSecondaryBrush` / `HintTextBrush` / `MutedTextBrush` | 四级文本 |
| 文本 | `ToggleForegroundBrush` | 开关类控件文字 |
| 强调 | `AccentBrush` / `AccentHoverBrush` / `AccentPressedBrush` | 主操作三态 |
| 强调 | `AccentForegroundBrush` / `AccentSurfaceBrush` | 强调底上的文字 / 淡强调面 |
| 控件 | `ControlForegroundBrush` / `ControlBorderBrush` / `ControlHoverBrush` | 按钮等 |
| 控件 | `CaptionGlyphBrush` / `CaptionHoverBrush` | 标题栏按钮 |
| 语义 | `ConnectedBrush` / `IdleDotBrush` | 连接状态 |
| 语义 | `DangerBrush` / `DangerForegroundBrush` | 关闭等破坏性操作 |
| 手柄可视化 | `PadBodyBrush` / `PadIdleBrush` / `PadPressedBrush` / `PadLabelBrush` | 手柄外形与按键 |
| 手柄可视化 | `StickPadBrush` / `StickPadStrokeBrush` / `StickRingBrush` / `StickThumbBrush` | 摇杆 |
| 手柄可视化 | `DpadCenterBrush` | 十字键中心 |
| 预览 | `PreviewBorderBrush` / `TrackBrush` / `BadgeTextBrush` | 曲线图与预览区 |
| 浮层 | `OverlayBackgroundBrush` / `OverlayTextBrush` | 遮罩与浮层文字 |

### 2.3 明暗取值取向

Dark 沿用既有的深灰阶梯（`#101010` 底 → `#1B1B1B` 卡 → `#232323` 次表面）与青色强调
`#4CC2FF`。Light **不是简单反色**，而是一套独立的可读阶梯：暖白底、明确的边框阶梯，
强调色加深到 `#0A6FBF` 一档以满足白底上的 4.5:1 文本对比度。

---

## 3. 布局语法

单窗口，三列固定骨架（自定义标题栏之下）：

```
┌──────────────────────────────────────────────────────────┐
│ [icon] DeziroJoy 手柄映射工具            [—] [×]          │  44px 标题栏
├──────────┬───────────────────────────┬───────────────────┤
│ 290px    │  *（标签页）               │ 350px             │
│ 连接     │  · 按键映射                │  手柄状态          │
│ 引擎     │  · 鼠标灵敏度              │   · 实时可视化器   │
│ 配置文件 │  · 滚轮灵敏度              │   · 提示文本      │
│ +添加映射│                           │                   │
└──────────┴───────────────────────────┴───────────────────┘
```

- 标题栏 44px，承担拖拽移动；窗口四边四角由 8 个 4–8px 透明握柄承担缩放
  （`WindowDecorations="None"` 移除了原生边框，必须自建，见 `ElementRole` 用法）。
- 左列是**状态与动作**，中列是**配置**，右列是**反馈**。反馈永远可见，不与标签页切换耦合。
- 窗口最小 1180×780；低于此宽度三列不可读，故设为硬下限而非自适应折叠。

---

## 4. 交互模型

### 4.1 手柄优先

映射工具的使用中，用户手在手柄上。因此：

- 每个"确认"位置同时接受 **Esc = 取消** 与 **Enter = 确定**。
- 导航支持手柄十字键/左摇杆在可聚焦元素间移动。
- 关键操作（启动/停止映射）提供单一明确入口，并可通过手柄触发。

### 4.2 一次一决策

编辑器遵循"先选来源类型，再选具体来源"：

```
触发来源
  ○ 按键        →  仅显示按键选择器（按类别分组）
  ● 摇杆        →  仅显示摇杆选择器
```

来源类型一旦确定，另一类选择器**不渲染**，而非置灰。这消除了"选了下拉框却不知道另一个
下拉框是否相关"的认知负担。

### 4.3 实时捕获

配置来源时进入捕获态：

- 收到**手柄输入** → 自动选中对应的手柄按键/摇杆。
- 收到**键盘输入** → 自动配置对应键，组合键逐键累积（Ctrl 按下 → C 按下 → Ctrl+C）。
- 捕获态需有明确的视觉提示与退出方式（Esc）。

### 4.4 参数可解释

鼠标灵敏度与滚轮灵敏度的每个选项都必须附带"这改变了什么"的说明。速度采用**物理单位
px/s**，与 DPI 和屏幕分辨率解耦——这是相比 AntiMicroX 的核心改进，也必须在界面上说明。

---

## 5. 常驻架构

前端窗口是**可选的**。默认生命周期：

```
登录 ──▶ 提权代理（无窗口，RSS ≈ 30MB）──┬──▶ 注入输入（SendInput / uinput）
                                          └──▶ 命名管道 IPC
打开界面 ◀── 非提权 GUI（Avalonia，RSS ≈ 185MB）──┘
关闭界面 ──▶ GUI 退出，代理继续
```

关键点：

- **代理必须在用户会话内**。Session 0 中的 Windows 服务无法向用户桌面注入输入，
  因此"做成服务"是错误形态。
- **代理必须提权**。非提权进程的 `SendInput` 会被 UIPI 静默丢弃。
- **GUI 按需加载**。UI 栈（Avalonia/Skia/字体）约 145MB，只在界面可见时存在。

托盘提供：显示主窗口 / 退出。关闭或最小化主窗口 = 隐藏到托盘，而非退出。

---

## 6. 质量门禁

- 引擎与视图模型逻辑：单元测试 + headless GUI 测试。
- 视觉改动：Xvfb 截图真面验证；标题栏类仅 Windows 可见的问题必须显式标注需用户验证。
- 每次提交前：`dotnet build` 0 错误 0 警告，`dotnet test` 全绿。
- 提交信息：`type(scope): 英文主题` + 可选英文 bullet body。

---

## 7. 待办

- [ ] 编辑器来源类型先行 + 实时捕获（含键盘组合键捕获）
- [ ] 可配置按键按类别分组
- [ ] 灵敏度各选项的说明文字
- [ ] 配置导入/导出
- [ ] 主题切换的 UI 入口
- [ ] Esc/Enter 快捷键与手柄导航
- [ ] 托盘与提权常驻代理
- [ ] 非标准手柄按键的接受与映射
