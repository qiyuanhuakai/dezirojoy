# DeziroJoy 0.0.5-alpha.7 发布说明（源稿）

> 本文是 0.0.5-alpha.7 发布说明的源稿。GitHub Release 的 notes 由
> `.github/workflows/release.yml` 按 tag 读取本文件生成，提交列表由工作流自动追加。

## DeziroJoy v0.0.5-alpha.7

本版集中修复 DInput 连续学习、重复 HID 接口、布局／动作预设管理与 Windows 自动更新。
alpha.1–alpha.6 的交付继续有效，详见各自发布说明。

### DInput：连续学习、极值与残留轴

- 未成功捕获时持续更新基线，松开后可以重新捕获；刚完成捕获的同一控件须先回中，防止
  串到下一步。下一步提示前已经激活的新方向也可直接识别。
- 修复 `short.MinValue` 溢出：摇杆推到底产生 `-32768` 时，先拓宽为 `int` 再取绝对值，
  不再因 `OverflowException` 静默终止捕获。真机日志确认这正是左 X 后左 Y 无反应的根因。
- 只接受上一次捕获后新变为活动的控件。上一帧已经活动、但未选中的重复轴不再串到下一步；
  本设备同一物理 Y 方向重复出现在 `a1/a2` 时，不会把 `a2` 误学成右摇杆。
- SDL 捕获在单一后台线程执行；取消立即关闭对话框，关闭路径只执行一次，不再递归关闭或
  留下挡住配置的窗口。未预期的后台异常会显示原因，不再无提示地停止。
- 向导显示上一项的实际控件标识（如 `leftx = a0`），便于核对学习结果。
- 通过绑定或设置齿轮打开设置页都会扫描设备。探针新增只读 `raw` 命令，可记录原始按钮、
  轴和方向帽变化，不写配置。

### 一只物理手柄，只进入一个运行时与一套动作预设

- SDL 能把一只 DInput 手柄的多个 HID 接口枚举成多个 Gamepad。此前 `COL01` 与 `COL02`
  各自生成动作预设，daemon 可能执行辅助接口的默认配置，而 GUI 学习／编辑主接口，造成
  通用面板、自定义动作和实际注入不一致。
- 对 Windows HID 路径能明确证明属于同一物理设备的 `COLxx` 接口，daemon 现在选择按钮、
  轴和方向帽更丰富的接口进入运行时；辅助接口不再生成第二套活动配置。
- 不按 VID/PID 写死型号。两只相同型号的物理手柄具有不同实例路径，不会被合并；现有配置
  文件保留，不因去重被删除。
- 真机已核对保存映射：双摇杆为 `a0/a1` 与 `a3/a4`，按钮和方向帽来自实际学习；debug
  daemon 接管后只加载 `COL01`。

### 布局学习与动作预设分开管理

- 「DInput / 未映射手柄」改为「手柄布局学习（SDL）」。已有自定义映射和 SDL 已有标准映射
  的设备也会列出，标注“已有映射，可重学”；Xbox 等标准手柄同样可以主动重新学习。
- 保存前保留原映射；保存仍按 GUID 更新同一条目。布局学习决定物理控件对应的逻辑按键，
  动作预设决定逻辑控件执行什么，两者不再由界面文案混为一层。
- 主页连接卡同时显示设备布局与当前动作预设名称，并明确“布局决定输入含义；动作预设决定
  执行什么”。
- `PresetStore.SaveSlot` 现在同时持久化设备键与正确的 `PresetSlot`，不再让 preset2/3/4
  文件内部仍标成槽 1。

### Windows 更新：等待真实退出，不启动替代 daemon

- 计划任务或服务的停止请求是异步的。更新会有界等待进程、映射通道与实例／注入槽位真正
  释放，不再立即检查一次就进入常规恢复流程。
- 更新停止链不再调用“恢复 daemon”，避免先启动替代 daemon 再停止的竞态。无计划任务且需要
  提权时，只启动“验证身份、停止、退出”的副本，不成为替代 daemon。
- 无法证明退出时仍取消更新，不会放宽检查或冒险替换被占用的文件。

### 验证边界

- Release 全量测试在 Linux 通过：**3430 项，3420 通过、0 失败、10 项 Windows 专属跳过**；
  其中 Platform 605 项，601 通过、4 跳过。
- Windows 定向复核：Platform **58 项通过**，App **151 项通过**，覆盖捕获极值、残留轴、
  复合接口去重、重学入口、取消窗口、预设槽和更新停止链。
- Windows 真机完整学习、保存和 daemon 主接口接管已通过；计划任务拥有的提权 daemon 连续
  三次更新停止均成功，无替代实例残留。
- 完整应用内下载／安装／重启，以及去重后的通用面板高亮和新增动作实际执行，继续按
  [docs/QA-CHECKLIST.md](https://github.com/qiyuanhuakai/dezirojoy/blob/v0.0.5-alpha.7/docs/QA-CHECKLIST.md)
  第 30 节复验，不提前宣称这些人工项目已验收。

### 下载

| 文件 | 平台 | 说明 |
| --- | --- | --- |
| `DeziroJoy-0.0.5-alpha.7-win-x64-setup.exe` | Windows | 自包含 per-user 安装包，Intel / AMD 64 位 |
| `DeziroJoy-0.0.5-alpha.7-win-arm64-setup.exe` | 同上，ARM64 |
| `DeziroJoy_0.0.5-alpha.7_amd64.deb` | Linux | 自包含 deb，Intel / AMD 64 位 |
| `DeziroJoy_0.0.5-alpha.7_arm64.deb` | 同上，ARM64 |
| `DeziroJoy-0.0.5-alpha.7-x86_64.rpm` | Linux | 自包含 rpm，Intel / AMD 64 位 |
| `DeziroJoy-0.0.5-alpha.7-aarch64.rpm` | 同上，ARM64 |
| `DeziroJoy-0.0.5-alpha.7-x86_64.AppImage` | Linux | 便携 AppImage，Intel / AMD 64 位 |
| `DeziroJoy-0.0.5-alpha.7-aarch64.AppImage` | 同上，ARM64 |
| `*.sha256` | — | SHA256 校验和 |

安装器不注册自启动、计划任务或 udev 规则；卸载不删除用户配置。未做代码签名，Windows 首次
运行可能出现 SmartScreen 提示。

### 本版提交

（由 release.yml 生成发布 notes 时自动追加。）
