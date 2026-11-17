# DeziroJoy 0.0.5-alpha.6 发布说明（源稿）

> 本文是 0.0.5-alpha.6 发布说明的源稿。GitHub Release 的 notes 由
> `.github/workflows/release.yml` 按 tag 读取本文件生成：发布体为 `## DeziroJoy v…`
> 标题至 `### 本版提交` 之间的内容（提交列表由工作流自动追加），文档版本声明与包名
> 由工作流按实际 tag 改写。

## DeziroJoy v0.0.5-alpha.6

本版是定向修复预览版，不推进其他 RoadMap 功能：把 DInput 逐键学习接入应用、让通用面板
完整显示长标签、精简 Windows 手柄兼容性重复文案，并记录中英文手柄键盘的布局调研与后续
原型方案。alpha.1–alpha.5 的交付继续有效，详见各自发布说明。

### DInput：从原始枚举到应用内逐键学习

- **真机根因已经定位**：Windows 上连接的 `3537:1041` DInput 设备，SDL 3.4.20 能枚举出
  19 按钮／6 轴／1 方向帽的主 HID 接口，以及 8 按钮／无轴的辅助接口；但 SDL 没有它们的
  标准 Gamepad 映射，所以旧 provider 返回 `NoGamepads`。这不是 SDL 无法读取 DInput，
  也不能靠假装标准 Xbox 布局解决。
- 新增「设置 → 后台服务 → DInput / 未映射手柄」：扫描并列出原始接口及控件数，默认选择
  按钮和轴较多的接口，也允许手动选择。学习区只在选择 SDL3 后端时显示。
- 「逐键学习并保存」在应用内完成按钮、轴和方向帽捕获。没有的项目可跳过，取消或关闭不保存；
  不按 VID／PID 猜测按钮或轴的顺序。
- 向导与 CLI `Sdl3Probe map` 共用捕获会话、SDL 校验和原子保存实现，写入同一个
  `sdl-mappings.json`。同 GUID 更新，其他设备条目不变；文件不能完整读取时拒绝覆盖。
- 保存后调用既有安全重启流程，让 daemon 读取新映射；重连后的设备快照与真实输入才是接管
  成功的证据。CLI 流程保留，供诊断和无 GUI 环境使用。

### 通用面板：标签需要多少宽度，就给多少宽度

- 移除卡片固定 `64px` 宽度与长标签省略号。`64px` 现在只是短名称的最小宽度，长名称按内容
  扩展卡片，单行完整显示。
- 不为所有卡片强行分配最长宽度；面键继续保持 2×2，其他分区沿用换行和滚动，不覆盖邻卡或
  操作栏。

### Windows 手柄兼容性：状态与边界各说一次

- 顶部只保留一段共同说明：当前用户、可恢复、ms-gamebar 的处理范围、其他启动方式、录制设置
  与弹窗限制。
- 「控制器唤起」和「静默处理程序」分别只报告自己的状态，不再重复“已关闭／已注册”和相同
  的长篇说明。路径失效、被其他程序接管及恢复失败仍保留各自提示与正确操作。

### 中英文手柄键盘调研（仅 RoadMap，未实施新键盘）

- 结论不是强行四选一，而是“同一文本／候选内核 + 可切换布局”：完整 QWERTY 作为默认基线；
  Daisywheel 作为减少移动的备选；九键作为熟悉手机九键用户的可选消歧布局；双拼作为全拼之上
  的进阶编码选项。
- RoadMap 已记录各方案的适用价值、学习成本、中文消歧、混合英文／网址、候选导航、原型任务和
  量化验收标准。当前没有宣称中文输入、轮盘、九键或双拼已经实现。
- 参考包括 Microsoft Research 双摇杆 QWERTY 实验、Steam Daisywheel、Input Labs 的手柄文本
  输入方案比较，以及中文移动输入研究；链接见 [ROADMAP.md](https://github.com/qiyuanhuakai/dezirojoy/blob/v0.0.5-alpha.6/ROADMAP.md) K2。

### 验证边界

- 自动化覆盖 DInput 捕获保存、取消、SDL 拒绝、设备选择、只有成功保存才重启、长标签完整显示、
  兼容性文案与滚动布局。Windows 上同一组 30 项定向测试通过。
- 真机证据目前证明 `3537:1041` 的原始 HID 枚举和控件数量，不等于其完整布局或 daemon 接管
  已验收。请按 [docs/QA-CHECKLIST.md](https://github.com/qiyuanhuakai/dezirojoy/blob/v0.0.5-alpha.6/docs/QA-CHECKLIST.md)
  第 29 节完成真实逐键学习，核对每个输入的高亮与执行。
- 本版没有继续推进 S2／S3 的其他验收、P1–P4 性能工作或 K1–K3 实现；旧后端仍保留，键盘仅更新
  调研与计划。

### 下载

| 文件 | 平台 | 说明 |
| --- | --- | --- |
| `DeziroJoy-0.0.5-alpha.6-win-x64-setup.exe` | Windows | 自包含 per-user 安装包，Intel / AMD 64 位 |
| `DeziroJoy-0.0.5-alpha.6-win-arm64-setup.exe` | 同上，ARM64 |
| `DeziroJoy_0.0.5-alpha.6_amd64.deb` | Linux | 自包含 deb，Intel / AMD 64 位 |
| `DeziroJoy_0.0.5-alpha.6_arm64.deb` | 同上，ARM64 |
| `DeziroJoy-0.0.5-alpha.6-x86_64.rpm` | Linux | 自包含 rpm，Intel / AMD 64 位 |
| `DeziroJoy-0.0.5-alpha.6-aarch64.rpm` | 同上，ARM64 |
| `DeziroJoy-0.0.5-alpha.6-x86_64.AppImage` | Linux | 便携 AppImage，Intel / AMD 64 位 |
| `DeziroJoy-0.0.5-alpha.6-aarch64.AppImage` | 同上，ARM64 |
| `*.sha256` | — | SHA256 校验和 |

安装器不注册自启动、计划任务或 udev 规则；卸载不删除用户配置。未做代码签名，Windows 首次
运行可能出现 SmartScreen 提示。

### 质量门禁

- Release 构建 0 警告 0 错误；全量测试在 Linux 通过：**3418 项，3408 通过、0 失败、
  10 项 Windows 专属跳过**。
- 本版自动化与真机证据的边界如上；未把原始枚举写成所有 DInput 已支持，也未把键盘调研写成
  已交付功能。

### 本版提交

（由 release.yml 生成发布 notes 时自动追加。）
