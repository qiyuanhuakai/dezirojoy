# 虚拟手柄性能基线工具

## 测量路径

`VirtualGamepadProvider` 在 `IGamepadProvider` 层生成确定性输入，不安装虚拟 HID 驱动。
被测链路仍为生产代码：

```text
虚拟输入 → EngineHost 墙钟循环 → MappingEngine → CommandDispatcher
         → SendInputOutputSink → Windows SendInput
```

daemon、呈现宿主和 GUI 都是真实独立进程。`SessionKeyboardThroughputTests` 的模拟时间步与
`RecordingSink` 不参与本测量。daemon 由采集器直接启动，与采集器同为非提权进程；本轮不覆盖
计划任务拉起的提权 daemon。所有进程都启用本章指标采集，因此结果包含测量代码本身的开销。

**边界**：这里不测量蓝牙／USB／XInput 送达延迟，也不测量目标应用收到并处理输入的延迟。
`InputToInjection` 是从虚拟 provider 开始 Poll 到该 Poll 对应的首个 `SendInput` 返回；
它不是端到端“手感延迟”。需要验证 OS 输入通道时，应另做物理手柄或系统级 HID 驱动测试。

## 场景

- `none`：无手柄。
- `idle`：一只静止手柄。
- `mouse`：右摇杆往返满幅偏转，持续真实鼠标注入，净位移近似为零。
- `macro`：持续长按 A，运行 `hold F23` + `every 50ms tap F24` 复杂宏。
- `multi`：四只虚拟手柄、错相持续鼠标输入。
- 界面形态：GUI 打开、GUI 完全退出、临时层浮层、屏幕键盘。
- 生命周期：长连续运行，以及反复创建／销毁 GUI。

宏只使用 F23/F24，鼠标使用往返方波；脚本在场景结束和异常退出时恢复光标位置。
每次运行使用独立的 `APPDATA` / `LOCALAPPDATA` / `TEMP` / `TMP`，不会读写日常配置。

## 运行

先构建 Release，关闭所有既有 DeziroJoy 进程，然后执行：

```powershell
dotnet build src/DeziroJoy.App/DeziroJoy.App.csproj -c Release
python tools/PerfBaseline/run_baseline.py
```

快速校验矩阵：

```powershell
python tools/PerfBaseline/run_baseline.py `
  --duration 5 --long-duration 5 --warmup-seconds 1 `
  --sample-interval 0.5 --gui-cycles 1 `
  --gui-open-seconds 2 --gui-closed-seconds 1 `
  --output artifacts/performance/matrix-smoke
```

正式运行默认：普通场景 20 秒、连续场景 120 秒、GUI 反复开关 6 次，
每 250 ms 采样一次。所有时间均为墙钟时间。

从指定证据目录重新生成报告：

```powershell
python tools/PerfBaseline/render_report.py `
  artifacts/performance/beta10-20261115 docs/PERFORMANCE-BASELINE.md
```

## 证据文件

每次输出目录包含：

- `metadata.json`：版本、提交、工作区脏状态、OS、逻辑核心数、参数与测量边界。
- `resources.csv`：逐 250 ms、逐进程 CPU 累计时间、工作集、私有字节、可见窗口数。
- `metrics-<role>-<pid>.json`：进程内每秒快照；含 Tick／注入延迟直方图、累计分配、各代 GC 次数和 SendInput 成功／失败计数。
- `scenario-metrics.json`：各场景预热清零后的起止指标。
- `summary.json`：按场景、按角色及合计汇总；其中的合计峰值是各角色峰值之和，不是严格同一时刻的峰值。
- `*.out.log` / `*.err.log`：真实 daemon／呈现宿主／GUI 日志。

完成后脚本会校验：应有输入的场景确有成功 SendInput，所有场景没有失败 SendInput，GUI／浮层／
屏幕键盘场景实际采到可见窗口。屏幕键盘持有输入租约，普通映射注入按设计暂停，因此该场景
不要求成功注入样本。GUI 反复开关场景当前通过创建后终止 GUI 进程施加生命周期压力，
不等同于用户点击关闭按钮的优雅退出验收。

CPU 百分比 = 进程 CPU 时间增量 ÷ 墙钟秒数 × 100，100% 表示占满一个逻辑核心，
不是整机利用率。工作集与私有字节分开报告，不能混为同一个“内存”数字。
分配量来自 `GC.GetTotalAllocatedBytes(false)`，是累计分配而非托管堆驻留大小。

Tick 和输入延迟采用固定 0.01 ms 线性直方图，40.96 ms 内给出分位数；超出部分保留
count/mean/max 和 `overflowCount`。若溢出区间影响 p95/p99，报告会明确标为下界，
不会把 40.96 ms 当成实际上限。
