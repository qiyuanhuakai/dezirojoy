# DeziroJoy 0.0.5-alpha.5 发布说明（源稿）

> 本文是 0.0.5-alpha.5 发布说明的源稿。GitHub Release 的 notes 由
> `.github/workflows/release.yml` 按 tag 读取本文件生成：发布体为 `## DeziroJoy v…`
> 标题至 `### 本版提交` 之间的内容（提交列表由工作流自动追加），文档版本声明与包名
> 由工作流按实际 tag 改写。

## DeziroJoy v0.0.5-alpha.5

本版执行 **S3：SDL3 成为默认输入后端**（旧 XInput／evdev 退为显式回退选项），并带来
面键语义按设备字形统一、DInput 映射设备语义不变、Windows／X11 更新修复、通用面板
紧凑化与设置精简、托盘进程固定 Software 渲染，以及内存根因与进程合并边界的说明。
alpha.1–alpha.4 的交付继续有效，详见各自发布说明，此处不再重复。

### 本版更新

#### S3：SDL3 成为默认后端

- **默认读取 SDL3**：新安装、从未选择过后端的升级、设置文件缺失或损坏，一律经 SDL3
  统一 provider 读取；`input-backend.json` 中无法识别的取值按默认 SDL3 读取并说明原因。
- **旧后端退为显式回退**：XInput／evdev（现称“旧后端回退”）保留给仍生活在旧设备身份下
  的配置与可诊断回滚；alpha.1–alpha.4 里显式选择过旧后端的用户在升级后**保持旧后端**
  （旧写法 `platform` 仍被识别），不被悄悄切到新默认。
- **切换语义不变**：设置页「后台服务」卡片的「输入后端」一节选项改为
  「SDL3 统一后端（默认）」／「旧后端回退（XInput / evdev）」；保存并重启常驻服务生效，
  启动失败时保持原后端并报告，不静默改读另一个。卡片与 daemon 摘要始终显示**真正在
  运行的 backend**。
- **Guide 键随后端可用**：SDL3 在各平台都报告 Guide，因此 Windows 上 Guide 变为可绑定
  （XInput 没有 Guide 位；evdev 侧原有 BTN_MODE 不变）。

#### NS／Xbox 面键语义一致

- **逻辑按键按设备字形决定，不按位置决定**：SDL 的 south/east/west/north 是菱形里的
  四个**位置**，每个位置上印的字因手柄而异（Xbox／DualShock：南 A／东 B／西 X／北 Y；
  任天堂：南 B／东 A／西 Y／北 X；GameCube：南 A／东 X／西 B／北 Y）。现在
  `Sdl3FaceButtonMap` 读取 SDL 报给每台设备的字形（`SDL_GetGamepadButtonLabel`），
  据此决定每个位置的逻辑按键；SDL 读不到字形时回退位置表。
- **标签与执行同一来源**：状态 tracker 与控件描述都用这一张表——面板上叫 X 的控件绑定
  的就是“按下物理 X 产生的那次输入”。此前按位置固定映射会让任天堂手柄的物理 X（北位）
  执行逻辑 Y（面板显示 X、映射执行 Y）；只改显示不改语义是治标。设备重映射时 tracker
  随之更换，旧布局下按住的键不会以新名字残留。
- **映射卡片与面板不打架**：映射描述里原始扩展码的显示与通用面板用同一套名称。

#### DInput 映射设备语义不变

- 经 `map` 流程补充映射的 DInput 设备**保持位置面键语义**：这类设备报不出 SDL 可识别的
  字形，每个槽取位置表答案——这正是用户手写映射时的假定（取证流程的键就是 SDL 的位置
  键）。字形驱动的改动不影响映射补充流程。

#### Windows／X11 更新修复

- **Windows：停止以“证实退出”为门**。更新的 daemon 停止改走监督者的、以证据为准的停止
  （`StopDaemonForUpdateAsync`）：安装程序只允许在 daemon 进程**可证明已退出**后开始——
  常驻 daemon 持有安装目录文件时 setup 的 DeleteFile 会以拒绝访问（错误 5）回滚，
  这正是 alpha.3 更新失败的根因。无法确认退出时更新取消并明说
  （“无法确认守护进程已退出，更新已取消；映射功能不受影响，请重试。”），映射继续。
- **Windows：提权停止专用入口**。更新停止链可请提权副本**只结束**已确认身份的 daemon 并
  退出，不成为替代进程——更新要替换安装目录，替代进程会成为必须再停一次、且可能在安装
  中途重新锁住文件的 daemon。身份校验与恢复路径同一套。
- **X11／Linux：链式更新的后半段回到调用用户**。包管理器以 root 运行（pkexec），但其后的
  systemd **用户**单元与重启的应用必须以调用用户身份运行：`runuser`／`setpriv` 显式携带
  `HOME/USER/LOGNAME`、XDG 路径与 `DISPLAY/XAUTHORITY`、`DBUS_SESSION_BUS_ADDRESS`、
  `XDG_RUNTIME_DIR`（经 `env` 传入并单引号转义）。否则 pkexec 的极简环境会让
  `systemctl --user` 找不到用户总线、应用开不了窗口——更新“成功”却留下 root 语境的单元、
  root 的配置目录和没有 GUI 的桌面。AppImage 路径同样做单引号转义。

#### 通用面板紧凑化与设置精简

- **一卡一按钮**：卡片只剩“打开该控件的映射设置”一个动作并承载实时状态；隐藏、摇杆外观
  与执行切换移到分区下方的操作栏，作用于编辑器当前打开的控件。卡片因此是匀整的单行
  网格：无错位列、无换行标签、无堆叠开关，动作指针与手柄导航都可达。
- **面键 2×2 菱形**：四枚面键按手柄自己的菱形排成 2×2 块，不再横贯舞台。
- 长标签卡内省略，全文在卡片 tooltip；分区仍共处一个滚动区，操作栏固定其下。
- **设置精简**：设置页提示文案收敛为一行式；自动启动的管理员权限提示只在确实需要的
  Windows 上出现。

#### 托盘进程固定 Software 渲染

- 常驻呈现宿主（`--tray`：托盘图标 + 临时层提示与屏幕键盘两个浮层，整个登录会话存活）
  **固定使用 Software 渲染**：一个从不关闭的宿主会为一个图标和两个小浮层让
  ANGLE／D3D11 GPU 栈存活整个登录会话。用户的渲染偏好属于主窗口，不属于常驻宿主。
- 这是基线而非锁定：`DEZIROJOY_RENDER` 仍可覆盖（诊断对照用）；持久化选择**故意不被
  托盘读取**——给主窗口选 GPU 不会把 GPU 栈悄悄交回常驻宿主。

#### 内存根因与进程合并边界（说明）

- **根因是框架地板，不是应用逻辑**：历史实测中最小 Avalonia 空窗约 176 MB RSS／
  114 MB 私有，DeziroJoy 自身增量约 76 MB RSS／53 MB 私有；地板的大头是 GPU 图形栈
  （Intel Graphics Compiler 81 MB、D3D 用户模式驱动、ANGLE 等）。这也是 P1 的 Software
  模式能省下约 120 MB 工作集的原因——省的是图形栈，不是应用。
- **三进程拆分正是为把地板从常驻路径上拿走**：daemon 不加载 UI 栈（常驻但轻），GUI 可
  整体释放（关窗即退出），托盘只保图标与浮层。WSL 口径下默认渲染约 283–289 MiB 工作集
  ／185–191 MiB 私有、Software 约 163–165 MiB 工作集／75–77 MiB 私有、CPU 未上升；
  当前托盘约 208 MiB 工作集／157 MiB 私有是问题现场参考（与测试环境不可直接比较）。
- **进程合并的边界**：daemon 永远不能并入任何 UI 进程——那会把 UI 地板变成永久常驻，
  且 Windows 上只有提权进程可注入（UIPI），daemon 是注入权威；托盘不能并入渲染窗口
  （托盘不关闭，渲染窗口是可释放的，合并等于让 GUI 栈永久常驻）；渲染窗口也不能并入
  托盘（浮层与图标必须在 GUI 退出后存活）。唯一有合并余地的是两个 GUI 侧进程，而它们
  的生命周期相反，答案就是保持分离、各自做小（托盘固定 Software、GUI 惰性化）。
  详细口径见 [docs/PERFORMANCE-BASELINE.md](https://github.com/qiyuanhuakai/dezirojoy/blob/v0.0.5-alpha.5/docs/PERFORMANCE-BASELINE.md)。

### 性能证据状态

- **P1**：主 GUI 保留可选（持久化选择：平台检测／Software，`DEZIROJOY_RENDER` 与
  `--render` 覆盖）；托盘固定 Software。A／B 内存数据（WSL 内的 Windows 实测）见
  PERFORMANCE-BASELINE：默认约 283–289 MiB WS／185–191 MiB 私有，Software 约
  163–165 MiB WS／75–77 MiB 私有，CPU 未上升。**交互（流畅度、输入到画面、高 DPI）
  仍建议真机复验**后再决定主 GUI 是否默认切换。
- **P2–P4**：维持“实现已落地、自动化与生命周期验证通过、**尚无有效独立 A／B**”的状态，
  本版不新增任何收益数字，也不把性能项写成完成。

### 已知限制与验收边界

- S3 默认切换已落地，但**验收未完成**：真实设备矩阵（现有证据仅限用户实测的 Xbox／NS
  全部按键可用）、同型号双设备、Joy-Con 单只／Pair、休眠恢复、旧后端回退与去重验证
  仍待真机；旧后端**未移除**（回退路径保留）。
- 面键语义按字形：SDL 读不到字形的设备回退位置表；GameCube 等布局的正确性依赖 SDL 报表
  的字形，需真机复核。
- DInput 映射设备保持位置语义（设计如此）；`map` 流程真机复验待补。
- 更新修复（Windows 证实停止、Linux 用户语境链）需真机复验；自动更新链路的历史缺陷
  （`0.0.2-beta.3` 及更早）不变。
- P1 仅 Windows 有效；Software 初始化失败导致启动失败（设计行为）。P2–P4 无 A／B 前
  不宣称收益。
- Game Bar 两层均不保证抑制所有弹窗（实测部分 Win11 LTSC 仍可能弹窗）。

### 升级与回退

- **升级到 alpha.5 后默认后端变为 SDL3**：从未选择过的安装直接读 SDL3；显式选过旧后端
  的安装保持旧后端。首次以 SDL3 连接时按 alpha.2 起的保守规则迁移旧配置（一对一且无
  反证时逐槽复制、旧文件只读、marker 防重复复制）。
- **回退**：设置页「后台服务」卡片选「旧后端回退（XInput / evdev）」并保存重启；或重新
  安装 0.0.2——配置均保留。旧后端在本版保留，回退可控。
- 完整说明见 [docs/SDL3-BACKEND.md](https://github.com/qiyuanhuakai/dezirojoy/blob/v0.0.5-alpha.5/docs/SDL3-BACKEND.md)。

### 下载

| 文件 | 平台 | 说明 |
| --- | --- | --- |
| `DeziroJoy-0.0.5-alpha.5-win-x64-setup.exe` | Windows | 自包含 per-user 安装包，Intel / AMD 64 位 |
| `DeziroJoy-0.0.5-alpha.5-win-arm64-setup.exe` | 同上，ARM64 |
| `DeziroJoy_0.0.5-alpha.5_amd64.deb` | Linux | 自包含 deb，Intel / AMD 64 位 |
| `DeziroJoy_0.0.5-alpha.5_arm64.deb` | 同上，ARM64 |
| `DeziroJoy-0.0.5-alpha.5-x86_64.rpm` | Linux | 自包含 rpm，Intel / AMD 64 位 |
| `DeziroJoy-0.0.5-alpha.5-aarch64.rpm` | 同上，ARM64 |
| `DeziroJoy-0.0.5-alpha.5-x86_64.AppImage` | Linux | 便携 AppImage，Intel / AMD 64 位 |
| `DeziroJoy-0.0.5-alpha.5-aarch64.AppImage` | 同上，ARM64 |
| `*.sha256` | — | SHA256 校验和 |

安装器不注册自启动、计划任务或 udev 规则；卸载不删除用户配置，并会尝试交回
ms-gamebar 静默处理程序注册。未做代码签名，Windows 首次运行可能出现 SmartScreen
提示。

### 质量门禁

- Release 构建 0 警告 0 错误；全量测试在 Linux 通过：**3413 项，3403 通过、0 失败、
  10 项 Windows 专属跳过**。
- 本版改动均有自动化测试（默认解析与升级兼容、面键语义表与回退、映射设备位置语义保持、
  更新停止门与用户语境链、面板紧凑布局、托盘渲染基线、内存口径）；S3 真机矩阵、更新
  修复真机、P1 交互复验与 P2–P4 的独立 A／B**未做**——自动化通过不等于这些场景已验证。
- 人工验收清单：[docs/QA-CHECKLIST.md](https://github.com/qiyuanhuakai/dezirojoy/blob/v0.0.5-alpha.5/docs/QA-CHECKLIST.md)（第 23–28 节）；
  SDL3 使用与回退：[docs/SDL3-BACKEND.md](https://github.com/qiyuanhuakai/dezirojoy/blob/v0.0.5-alpha.5/docs/SDL3-BACKEND.md)；性能口径：
  [docs/PERFORMANCE-BASELINE.md](https://github.com/qiyuanhuakai/dezirojoy/blob/v0.0.5-alpha.5/docs/PERFORMANCE-BASELINE.md)；阶段进度：
  [ROADMAP.md](https://github.com/qiyuanhuakai/dezirojoy/blob/v0.0.5-alpha.5/ROADMAP.md)。

### 本版提交

（由 release.yml 生成发布 notes 时自动追加。）
