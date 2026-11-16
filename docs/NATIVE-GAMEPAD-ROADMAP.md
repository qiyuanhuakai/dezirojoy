# 原生 PlayStation／Nintendo Switch 手柄接入路线

## 结论与边界

当前产品的 Windows 输入后端仍是 **XInput**，Linux 是 **evdev**；这仍是默认后端。
0.0.5-alpha.1 已交付本文推荐的 SDL3 路线首段成果——独立探针（S1）与可显式启用的初始
统一 provider（S2 初始）——但未经真机验收，也未改变默认后端；本文的阶段划分与验收标准
继续有效，作为后续推进依据。SDL3 后端的使用、诊断与升级回退见
[docs/SDL3-BACKEND.md](SDL3-BACKEND.md)。

原厂 DualShock 4、DualSense、Switch Pro 和 Joy-Con 本身就足以构成接入价值：它们可以不依赖
额外转换程序进入现有“输入 → 分层／手势／DSL → 键鼠注入”管线。价值不取决于能找到多少
“没有 XInput 的第三方型号”。

与虚拟 Xbox 手柄方案不同，DeziroJoy 的目标是键鼠输出，不需要先把这些设备模拟成 Xbox
手柄。直接读取原始设备可避免不必要的驱动与转换层。

## 推荐路线：先做独立 SDL3 provider 探针

SDL3 的标准手柄模型已有 PS4、PS5、Switch Pro、左／右 Joy-Con 和 Joy-Con Pair 类型，适合作为
通用接入评估起点，而不是一开始为每种设备各写一份 HID 报告解析器。

SDL3 还提供后续扩展所需的读取通道：

- `SDL_GetGamepadTouchpadFinger`：触点是否按下、归一化 x／y（左上为原点）与压力；
- `SDL_GamepadHasSensor` / `SDL_SetGamepadSensorEnabled` / `SDL_GetGamepadSensorData`：
  传感器能力、启用与数据读取。

这些 API 的能力仍取决于设备与平台，必须逐项探测，不能按手柄名称假定触摸板或体感一定可用。

官方资料：

- [SDL_GamepadType](https://wiki.libsdl.org/SDL3/SDL_GamepadType)
- [SDL_GetGamepadTouchpadFinger](https://wiki.libsdl.org/SDL3/SDL_GetGamepadTouchpadFinger)
- [SDL_GetGamepadSensorData](https://wiki.libsdl.org/SDL3/SDL_GetGamepadSensorData)

## 分期实施

### 阶段 0：独立探针，不替换 XInput

先验证 SDL3 的部署、设备枚举和原始输入，不改现有 daemon 的默认 provider：

1. 输出 SDL 版本、设备类型、GUID、VID／PID、序列号、路径和实例 ID；
2. 验证标准按钮、摇杆、扳机、连接／断开与后台输入；
3. 探测触摸板数量、触点数、传感器类型和数据率；
4. 覆盖 USB／蓝牙、重复连接、同型号双设备和休眠恢复；
5. 同时观察 XInput 枚举，建立重复设备识别记录。

### 阶段 1：普通输入接入现有引擎

新增独立 provider，把标准按钮与双摇杆归一化为现有 `IGamepadState`，直接复用预设、临时层、
前台规则、手势与 DSL。XInput 保持默认；SDL provider 先作为显式可选后端，避免一次切换引入
全量回归。

设备布局需要保留来源信息：PS 的叉／圈／方块／三角、NS 的 A／B／X／Y 与 Xbox 物理位置及
标签不能混为一谈。映射语义采用“物理位置”还是“设备字面标签”必须在 UI 和迁移规则中明确。

### 阶段 2：扩展输入模型

当前 `IGamepadState` 只有按钮掩码、双摇杆和额外按钮码，没有触摸板或体感字段。不能把这些
数据伪装成普通按钮来宣布“已支持”。应在 Core 中增加独立、可选、带能力描述的输入模型，
再分别设计：

- 触摸板 → 绝对／相对指针、点击、分区按钮；
- 加速度计／陀螺仪 → 鼠标移动、方向盘或姿态映射；
- 采样率、时间戳、断连清零和暂停释放语义。

这些扩展要经过确定性 Core 测试，不能只靠真机观察。

## 设备身份与去重是接入前置条件

SDL 的 `SDL_JoystickID` 是本次运行内的实例 ID，不能直接作为跨重启配置身份。持久身份需要
组合 SDL GUID、可获得的序列号、设备路径／ContainerId 与设备类型，并定义：

- 有序列号设备：优先使用稳定序列号；
- 无序列号设备：使用路径等较稳定信息，但明确换端口可能改变身份；
- 同型号多设备：不能仅靠 VID／PID 共用一份状态，需提供可区分的实例身份与用户提示；
- Joy-Con：单只与 Pair 是否共享配置需要显式规则，不能因枚举形态变化而悄悄换配置。

同一设备同时出现在 SDL 与 XInput 时，必须按 ContainerId／路径／GUID 等证据去重，确保只有
一个活动 provider 持有它。无法确认关联时宁可提示用户选择，也不能让两个 provider 同时驱动
映射，造成双击、重复鼠标速度或输出所有权混乱。

## 与 GameInput v4 的关系

GameInput v4 是 Windows 上另一条待验证路线，尤其适合核实 Guide／Share、后台行为和 XInput
槽位关联；它不能替代跨平台 SDL 的评估。仓库中的
[`tools/GameInputProbe`](../tools/GameInputProbe/README.md) 只验证 GameInput，不改变当前后端。

选择正式后端应以设备矩阵结果为准：覆盖、身份稳定性、后台行为、额外能力、部署限制和维护
成本，而不是仅凭 API 名称。也可长期采用 XInput 默认、SDL 扩展覆盖、GameInput 按验证结果补充
的组合，但同一物理设备仍须只有一个权威输入源。

## 最小真机矩阵

| 设备 | 标准输入 | 热插拔／后台 | 身份／去重 | 扩展能力 |
|---|---|---|---|---|
| DualShock 4（USB／蓝牙） | 按钮、摇杆、扳机 | 断开、重连、失焦 | 序列号／路径、XInput 重复 | 触摸板、体感 |
| DualSense（USB／蓝牙） | 同上 | 同上 | 同上 | 触摸板、体感 |
| Switch Pro | 同上 | 同上 | 同上 | 体感（按实际申报） |
| 左右 Joy-Con／Pair | 单只与组合 | 拆分、重配对 | 单只／Pair 身份规则 | 体感（按实际申报） |
| 两只同型号设备 | 独立状态 | 独立断开 | 实例不串配置 | 不适用 |

每项结果都应记录 SDL／系统版本、连接方式和日志。编译通过或 SDL 类型存在，不等于该设备
已经完成支持验证。
